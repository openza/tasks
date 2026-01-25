import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import '../../../../core/utils/logger.dart';
import '../../../../core/utils/markdown_task_parser.dart';
import '../../../../domain/entities/task.dart';

/// Reads and parses Obsidian vault files to extract tasks.
///
/// Scans a vault folder recursively for .md files and extracts GFM checkboxes
/// as tasks. Uses heading context for stable task identity.
class ObsidianVaultReader {
  /// Maximum file size to process (5MB)
  static const _maxFileSizeBytes = 5 * 1024 * 1024;

  /// Heading pattern for extracting heading hierarchy
  static final _headingPattern = RegExp(r'^(#{1,6})\s+(.+)$', multiLine: true);

  /// Read all tasks from an Obsidian vault.
  ///
  /// [vaultPath] - The root path of the Obsidian vault
  /// [lastScanTime] - Optional timestamp to skip files not modified since then
  ///
  /// Returns a list of [TaskEntity] objects extracted from markdown files.
  Future<List<TaskEntity>> readAllTasks(
    String vaultPath, {
    DateTime? lastScanTime,
  }) async {
    final vaultDir = Directory(vaultPath);
    if (!await vaultDir.exists()) {
      AppLogger.warning('Obsidian vault not found: $vaultPath');
      return [];
    }

    final vaultId = _generateVaultId(vaultPath);
    final tasks = <TaskEntity>[];
    final now = DateTime.now();

    await for (final entity in vaultDir.list(recursive: true)) {
      if (entity is! File) continue;
      if (!entity.path.endsWith('.md')) continue;

      // Skip hidden files and folders (e.g., .obsidian/)
      final relativePath = p.relative(entity.path, from: vaultPath);
      if (relativePath.startsWith('.') || relativePath.contains('/.')) {
        continue;
      }

      try {
        // Check file size
        final stat = await entity.stat();
        if (stat.size > _maxFileSizeBytes) {
          AppLogger.warning('Skipping large file: $relativePath (${stat.size} bytes)');
          continue;
        }

        // Skip unchanged files if lastScanTime provided
        if (lastScanTime != null && stat.modified.isBefore(lastScanTime)) {
          continue;
        }

        final fileTasks = await _parseFile(
          entity,
          vaultId: vaultId,
          vaultPath: vaultPath,
          relativePath: relativePath,
          extractedAt: now,
        );
        tasks.addAll(fileTasks);
      } catch (e) {
        AppLogger.warning('Error parsing file $relativePath: $e');
        continue;
      }
    }

    AppLogger.info('Extracted ${tasks.length} tasks from Obsidian vault');
    return tasks;
  }

  /// Validate that a vault path is accessible.
  Future<bool> isVaultAccessible(String path) async {
    try {
      final dir = Directory(path);
      if (!await dir.exists()) return false;

      // Try to list the directory to check permissions
      await dir.list().first.timeout(
        const Duration(seconds: 2),
        onTimeout: () => throw TimeoutException('Directory access timeout'),
      );
      return true;
    } catch (e) {
      // Directory is empty but accessible
      if (e is StateError && e.message.contains('No element')) {
        return true;
      }
      return false;
    }
  }

  /// Parse a single markdown file and extract tasks.
  Future<List<TaskEntity>> _parseFile(
    File file, {
    required String vaultId,
    required String vaultPath,
    required String relativePath,
    required DateTime extractedAt,
  }) async {
    String content;
    try {
      content = await file.readAsString();
    } catch (e) {
      // Try with latin1 fallback for encoding issues
      try {
        content = latin1.decode(await file.readAsBytes());
      } catch (_) {
        AppLogger.warning('Could not decode file: $relativePath');
        return [];
      }
    }

    final parsedTasks = MarkdownTaskParser.parse(content);
    if (parsedTasks.isEmpty) return [];

    final projectName = _extractProjectName(relativePath);
    final headingMap = _buildHeadingMap(content);
    final tasks = <TaskEntity>[];
    final lines = content.split('\n');

    // Find line numbers for each task
    int taskIndex = 0;
    for (int lineNum = 0; lineNum < lines.length && taskIndex < parsedTasks.length; lineNum++) {
      final line = lines[lineNum];
      if (_isCheckboxLine(line)) {
        final parsed = parsedTasks[taskIndex];
        final headingPath = _getHeadingPathForLine(headingMap, lineNum);
        // Include line number to make IDs unique for duplicate task titles
        final externalId = _generateTaskId(vaultId, relativePath, headingPath, parsed.title, lineNum);

        // Generate project ID from file path for virtual project grouping
        final projectId = _generateProjectId(vaultId, relativePath);

        tasks.add(TaskEntity(
          id: 'obsidian_$externalId',
          externalId: externalId,
          integrationId: 'obsidian',
          title: parsed.title,
          description: null,
          projectId: null, // User will organize locally
          priority: 2, // Default priority
          status: parsed.isCompleted ? TaskStatus.completed : TaskStatus.pending,
          createdAt: extractedAt,
          updatedAt: extractedAt,
          completedAt: parsed.isCompleted ? extractedAt : null,
          providerMetadata: {
            'sourceTask': {
              'vaultPath': vaultPath,
              'filePath': relativePath,
              'projectId': projectId, // For virtual project grouping
              'projectName': projectName,
              'headingPath': headingPath,
              'lineNumber': lineNum + 1, // 1-indexed for display
            },
          },
        ));

        taskIndex++;
      }
    }

    return tasks;
  }

  /// Check if a line is a checkbox line
  bool _isCheckboxLine(String line) {
    return RegExp(r'^\s*[-*+]\s*\[([ xX])\]').hasMatch(line);
  }

  /// Generate a stable ID for a vault (hash of path)
  String _generateVaultId(String vaultPath) {
    final bytes = utf8.encode(vaultPath);
    return sha256.convert(bytes).toString().substring(0, 16);
  }

  /// Generate a stable project ID from file path.
  ///
  /// Groups tasks by their parent directory (or file name for root files).
  String _generateProjectId(String vaultId, String relativePath) {
    // Use the parent directory for grouping, or file name for root files
    final dir = p.dirname(relativePath);
    final groupKey = dir == '.' ? p.basenameWithoutExtension(relativePath) : dir;
    final input = '$vaultId:$groupKey';
    final bytes = utf8.encode(input);
    return sha256.convert(bytes).toString().substring(0, 16);
  }

  /// Generate a stable task ID from context.
  ///
  /// Format: SHA256(vaultId:relativePath:headingPath:lineNum:title) truncated to 32 chars
  /// Line number is included to make IDs unique for duplicate task titles.
  String _generateTaskId(
    String vaultId,
    String relativePath,
    String headingPath,
    String title,
    int lineNum,
  ) {
    final input = '$vaultId:$relativePath:$headingPath:$lineNum:$title';
    final bytes = utf8.encode(input);
    return sha256.convert(bytes).toString().substring(0, 32);
  }

  /// Extract project name from file path.
  ///
  /// Examples:
  /// - 'work.md' → 'Work'
  /// - 'projects/client-a.md' → 'Client A'
  /// - 'daily/2024-01-15.md' → 'Daily'
  String _extractProjectName(String relativePath) {
    final fileName = p.basenameWithoutExtension(relativePath);

    // Use parent folder name if file is in a subfolder
    final dir = p.dirname(relativePath);
    if (dir != '.' && dir.isNotEmpty) {
      final folderName = p.basename(dir);
      return _titleCase(folderName.replaceAll(RegExp(r'[-_]'), ' '));
    }

    return _titleCase(fileName.replaceAll(RegExp(r'[-_]'), ' '));
  }

  /// Convert string to title case
  String _titleCase(String input) {
    if (input.isEmpty) return input;
    return input.split(' ').map((word) {
      if (word.isEmpty) return word;
      return word[0].toUpperCase() + word.substring(1).toLowerCase();
    }).join(' ');
  }

  /// Build a map of line numbers to their heading context.
  ///
  /// Returns a map where each key is a line number and the value is the
  /// heading hierarchy at that point (e.g., "Project > Phase > Tasks")
  Map<int, String> _buildHeadingMap(String content) {
    final lines = content.split('\n');
    final headingStack = <String>[];
    final headingLevels = <int>[];
    final headingMap = <int, String>{};

    String currentPath = '';

    for (int i = 0; i < lines.length; i++) {
      final match = _headingPattern.firstMatch(lines[i]);
      if (match != null) {
        final level = match.group(1)!.length;
        final text = match.group(2)!.trim();

        // Pop headings of equal or higher level
        while (headingLevels.isNotEmpty && headingLevels.last >= level) {
          headingStack.removeLast();
          headingLevels.removeLast();
        }

        headingStack.add(text);
        headingLevels.add(level);
        currentPath = headingStack.join(' > ');
      }

      headingMap[i] = currentPath;
    }

    return headingMap;
  }

  /// Get the heading path for a specific line number.
  String _getHeadingPathForLine(Map<int, String> headingMap, int lineNumber) {
    // Find the closest heading path at or before this line
    for (int i = lineNumber; i >= 0; i--) {
      if (headingMap.containsKey(i)) {
        return headingMap[i]!;
      }
    }
    return '';
  }
}

/// Exception for timeout scenarios
class TimeoutException implements Exception {
  final String message;
  TimeoutException(this.message);

  @override
  String toString() => message;
}
