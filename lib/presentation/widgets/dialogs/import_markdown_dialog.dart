import 'dart:convert';

import 'package:file_selector/file_selector.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';
import 'package:toastification/toastification.dart';
import 'package:uuid/uuid.dart';

import '../../../app/app_theme.dart';
import '../../../core/utils/markdown_task_parser.dart';
import '../../../domain/entities/task.dart';
import '../../providers/task_provider.dart';
import '../../providers/repository_provider.dart';

/// Import mode selection
enum ImportMode { paste, file }

/// Duplicate handling strategy
enum DuplicateStrategy { skip, createNew }

/// Dialog for importing tasks from markdown text or files
class ImportMarkdownDialog extends ConsumerStatefulWidget {
  const ImportMarkdownDialog({super.key});

  /// Show the import dialog and return the count of imported tasks
  static Future<int?> show(BuildContext context) async {
    return showDialog<int>(
      context: context,
      builder: (context) => const ImportMarkdownDialog(),
    );
  }

  @override
  ConsumerState<ImportMarkdownDialog> createState() =>
      _ImportMarkdownDialogState();
}

class _ImportMarkdownDialogState extends ConsumerState<ImportMarkdownDialog> {
  ImportMode _mode = ImportMode.paste;
  final _textController = TextEditingController();
  String? _selectedFileName;

  // Preview state
  List<ParsedTask>? _parsedTasks;
  Set<String> _duplicateTitles = {};
  DuplicateStrategy _duplicateStrategy = DuplicateStrategy.skip;

  // UI state
  bool _isProcessing = false;
  bool _isParsing = false;

  @override
  void dispose() {
    _textController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Dialog(
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      child: Container(
        width: 600,
        constraints: const BoxConstraints(maxHeight: 700),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            _buildHeader(context),
            Flexible(
              child: SingleChildScrollView(
                padding: const EdgeInsets.all(20),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    _buildModeSelector(context),
                    const SizedBox(height: 20),
                    if (_mode == ImportMode.paste)
                      _buildTextInput(context)
                    else
                      _buildFilePicker(context),
                    if (_parsedTasks != null) ...[
                      const SizedBox(height: 20),
                      _buildDivider(),
                      const SizedBox(height: 20),
                      _buildPreviewSection(context),
                    ],
                  ],
                ),
              ),
            ),
            _buildFooter(context),
          ],
        ),
      ),
    );
  }

  Widget _buildHeader(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        border: Border(bottom: BorderSide(color: AppTheme.gray200)),
      ),
      child: Row(
        children: [
          Container(
            padding: const EdgeInsets.all(8),
            decoration: BoxDecoration(
              color: AppTheme.primaryBlue.withValues(alpha: 0.1),
              borderRadius: BorderRadius.circular(8),
            ),
            child: Icon(
              LucideIcons.fileDown,
              size: 20,
              color: AppTheme.primaryBlue,
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Import from Markdown',
                  style: Theme.of(context).textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                ),
                Text(
                  'Import tasks from markdown checkboxes',
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: AppTheme.gray500,
                      ),
                ),
              ],
            ),
          ),
          IconButton(
            icon: Icon(LucideIcons.x, color: AppTheme.gray400),
            onPressed: () => Navigator.of(context).pop(),
          ),
        ],
      ),
    );
  }

  Widget _buildModeSelector(BuildContext context) {
    return Container(
      decoration: BoxDecoration(
        color: AppTheme.gray100,
        borderRadius: BorderRadius.circular(8),
      ),
      padding: const EdgeInsets.all(4),
      child: Row(
        children: [
          Expanded(
            child: _buildModeTab(
              context,
              mode: ImportMode.paste,
              icon: LucideIcons.clipboard,
              label: 'Paste Text',
            ),
          ),
          Expanded(
            child: _buildModeTab(
              context,
              mode: ImportMode.file,
              icon: LucideIcons.file,
              label: 'Import File',
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildModeTab(
    BuildContext context, {
    required ImportMode mode,
    required IconData icon,
    required String label,
  }) {
    final isSelected = _mode == mode;
    return GestureDetector(
      onTap: () {
        setState(() {
          _mode = mode;
          _parsedTasks = null;
          _duplicateTitles = {};
        });
      },
      child: Container(
        padding: const EdgeInsets.symmetric(vertical: 10, horizontal: 12),
        decoration: BoxDecoration(
          color: isSelected ? Colors.white : Colors.transparent,
          borderRadius: BorderRadius.circular(6),
          boxShadow: isSelected
              ? [
                  BoxShadow(
                    color: Colors.black.withValues(alpha: 0.05),
                    blurRadius: 4,
                    offset: const Offset(0, 1),
                  )
                ]
              : null,
        ),
        child: Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(
              icon,
              size: 16,
              color: isSelected ? AppTheme.primaryBlue : AppTheme.gray500,
            ),
            const SizedBox(width: 8),
            Text(
              label,
              style: TextStyle(
                fontSize: 14,
                fontWeight: isSelected ? FontWeight.w600 : FontWeight.w500,
                color: isSelected ? AppTheme.primaryBlue : AppTheme.gray600,
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildTextInput(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _buildSectionLabel(context, LucideIcons.type, 'Markdown Text'),
        const SizedBox(height: 8),
        TextField(
          controller: _textController,
          maxLines: 8,
          onChanged: (_) => setState(() {}),
          decoration: InputDecoration(
            hintText: 'Paste your markdown here...\n\n- [ ] Task 1\n- [x] Completed task\n- [ ] Task 3',
            hintStyle: TextStyle(color: AppTheme.gray400),
            border: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
            ),
            contentPadding: const EdgeInsets.all(12),
          ),
          style: const TextStyle(
            fontFamily: 'monospace',
            fontSize: 13,
          ),
        ),
        const SizedBox(height: 8),
        Text(
          'Supports: - [ ] task, - [x] completed, * [ ] task, + [ ] task',
          style: Theme.of(context).textTheme.bodySmall?.copyWith(
                color: AppTheme.gray500,
              ),
        ),
      ],
    );
  }

  Widget _buildFilePicker(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _buildSectionLabel(context, LucideIcons.file, 'Markdown File'),
        const SizedBox(height: 8),
        InkWell(
          onTap: _pickFile,
          borderRadius: BorderRadius.circular(8),
          child: Container(
            padding: const EdgeInsets.all(24),
            decoration: BoxDecoration(
              border: Border.all(
                color: _selectedFileName != null
                    ? AppTheme.primaryBlue
                    : AppTheme.gray300,
                style: BorderStyle.solid,
              ),
              borderRadius: BorderRadius.circular(8),
              color: _selectedFileName != null
                  ? AppTheme.primaryBlue.withValues(alpha: 0.05)
                  : null,
            ),
            child: Column(
              children: [
                Icon(
                  _selectedFileName != null
                      ? LucideIcons.fileCheck
                      : LucideIcons.upload,
                  size: 32,
                  color: _selectedFileName != null
                      ? AppTheme.primaryBlue
                      : AppTheme.gray400,
                ),
                const SizedBox(height: 12),
                Text(
                  _selectedFileName ?? 'Click to select a .md file',
                  style: TextStyle(
                    color: _selectedFileName != null
                        ? AppTheme.gray700
                        : AppTheme.gray500,
                    fontWeight: _selectedFileName != null
                        ? FontWeight.w500
                        : FontWeight.normal,
                  ),
                ),
                if (_selectedFileName != null) ...[
                  const SizedBox(height: 4),
                  Text(
                    'Click to change file',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AppTheme.gray400,
                        ),
                  ),
                ],
              ],
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildPreviewSection(BuildContext context) {
    final tasks = _parsedTasks!;
    final duplicateCount = _duplicateTitles.length;
    final hasAnyDuplicates = duplicateCount > 0;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            _buildSectionLabel(context, LucideIcons.eye, 'Preview'),
            const Spacer(),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
              decoration: BoxDecoration(
                color: AppTheme.primaryBlue.withValues(alpha: 0.1),
                borderRadius: BorderRadius.circular(12),
              ),
              child: Text(
                '${tasks.length} task${tasks.length == 1 ? '' : 's'} found',
                style: TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w500,
                  color: AppTheme.primaryBlue,
                ),
              ),
            ),
          ],
        ),
        const SizedBox(height: 12),

        // Duplicate warning
        if (hasAnyDuplicates) ...[
          Container(
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(
              color: AppTheme.amber50,
              borderRadius: BorderRadius.circular(8),
              border: Border.all(color: AppTheme.amber200),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Icon(
                      LucideIcons.alertTriangle,
                      size: 16,
                      color: AppTheme.amber600,
                    ),
                    const SizedBox(width: 8),
                    Text(
                      '$duplicateCount duplicate${duplicateCount == 1 ? '' : 's'} found',
                      style: TextStyle(
                        fontWeight: FontWeight.w600,
                        color: AppTheme.amber600,
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                Row(
                  children: [
                    Text(
                      'Handle duplicates:',
                      style: TextStyle(
                        fontSize: 13,
                        color: AppTheme.gray600,
                      ),
                    ),
                    const SizedBox(width: 12),
                    _buildDuplicateOption(
                      DuplicateStrategy.skip,
                      'Skip',
                    ),
                    const SizedBox(width: 8),
                    _buildDuplicateOption(
                      DuplicateStrategy.createNew,
                      'Create anyway',
                    ),
                  ],
                ),
              ],
            ),
          ),
          const SizedBox(height: 12),
        ],

        // Task preview list
        Container(
          constraints: const BoxConstraints(maxHeight: 200),
          decoration: BoxDecoration(
            border: Border.all(color: AppTheme.gray200),
            borderRadius: BorderRadius.circular(8),
          ),
          child: ListView.separated(
            shrinkWrap: true,
            itemCount: tasks.length,
            separatorBuilder: (_, _) => Divider(
              height: 1,
              color: AppTheme.gray200,
            ),
            itemBuilder: (context, index) {
              final task = tasks[index];
              final isDuplicate = _duplicateTitles.contains(task.title.toLowerCase());

              return ListTile(
                dense: true,
                leading: Icon(
                  task.isCompleted
                      ? LucideIcons.checkCircle2
                      : LucideIcons.circle,
                  size: 18,
                  color: task.isCompleted
                      ? AppTheme.successGreen
                      : AppTheme.gray400,
                ),
                title: Text(
                  task.title,
                  style: TextStyle(
                    fontSize: 14,
                    decoration: task.isCompleted
                        ? TextDecoration.lineThrough
                        : null,
                    color: task.isCompleted ? AppTheme.gray500 : AppTheme.gray700,
                  ),
                ),
                trailing: isDuplicate
                    ? Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 6,
                          vertical: 2,
                        ),
                        decoration: BoxDecoration(
                          color: AppTheme.amber100,
                          borderRadius: BorderRadius.circular(4),
                        ),
                        child: Text(
                          'Duplicate',
                          style: TextStyle(
                            fontSize: 10,
                            fontWeight: FontWeight.w500,
                            color: AppTheme.amber600,
                          ),
                        ),
                      )
                    : null,
              );
            },
          ),
        ),
      ],
    );
  }

  Widget _buildDuplicateOption(DuplicateStrategy strategy, String label) {
    final isSelected = _duplicateStrategy == strategy;
    return GestureDetector(
      onTap: () => setState(() => _duplicateStrategy = strategy),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        decoration: BoxDecoration(
          color: isSelected ? AppTheme.amber500 : Colors.white,
          borderRadius: BorderRadius.circular(6),
          border: Border.all(
            color: isSelected ? AppTheme.amber500 : AppTheme.gray300,
          ),
        ),
        child: Text(
          label,
          style: TextStyle(
            fontSize: 12,
            fontWeight: FontWeight.w500,
            color: isSelected ? Colors.white : AppTheme.gray600,
          ),
        ),
      ),
    );
  }

  Widget _buildSectionLabel(BuildContext context, IconData icon, String label) {
    return Row(
      children: [
        Icon(icon, size: 14, color: AppTheme.gray400),
        const SizedBox(width: 6),
        Text(
          label,
          style: TextStyle(
            fontSize: 12,
            fontWeight: FontWeight.w500,
            color: AppTheme.gray500,
          ),
        ),
      ],
    );
  }

  Widget _buildDivider() {
    return Divider(color: AppTheme.gray200, height: 1);
  }

  Widget _buildFooter(BuildContext context) {
    final hasContent = _mode == ImportMode.paste
        ? _textController.text.trim().isNotEmpty
        : _selectedFileName != null;
    final hasParsedTasks = _parsedTasks != null && _parsedTasks!.isNotEmpty;

    return Container(
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        border: Border(top: BorderSide(color: AppTheme.gray200)),
      ),
      child: Row(
        children: [
          Expanded(
            child: OutlinedButton(
              onPressed: () => Navigator.of(context).pop(),
              child: const Text('Cancel'),
            ),
          ),
          const SizedBox(width: 12),
          if (_parsedTasks == null)
            Expanded(
              flex: 2,
              child: FilledButton.icon(
                onPressed: hasContent && !_isParsing ? _parseMarkdown : null,
                icon: _isParsing
                    ? SizedBox(
                        width: 18,
                        height: 18,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          color: Colors.white,
                        ),
                      )
                    : Icon(LucideIcons.eye, size: 18),
                label: Text(_isParsing ? 'Parsing...' : 'Preview'),
              ),
            )
          else
            Expanded(
              flex: 2,
              child: FilledButton.icon(
                onPressed: hasParsedTasks && !_isProcessing
                    ? _importTasks
                    : null,
                icon: _isProcessing
                    ? SizedBox(
                        width: 18,
                        height: 18,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          color: Colors.white,
                        ),
                      )
                    : Icon(LucideIcons.download, size: 18),
                label: Text(_isProcessing ? 'Importing...' : 'Import Tasks'),
              ),
            ),
        ],
      ),
    );
  }

  /// Maximum file size: 5MB
  static const _maxFileSizeBytes = 5 * 1024 * 1024;

  Future<void> _pickFile() async {
    try {
      // Use file_selector which properly uses XDG Desktop Portals on Linux
      final XFile? file = await openFile(
        acceptedTypeGroups: [
          const XTypeGroup(
            label: 'Markdown files',
            extensions: ['md', 'markdown', 'txt'],
          ),
        ],
      );

      if (file != null) {
        final bytes = await file.readAsBytes();

        // Check file size limit
        if (bytes.length > _maxFileSizeBytes) {
          if (mounted) {
            toastification.show(
              context: context,
              type: ToastificationType.error,
              style: ToastificationStyle.fillColored,
              title: const Text('File Too Large'),
              description: const Text('File size must be under 5MB'),
              autoCloseDuration: const Duration(seconds: 5),
            );
          }
          return;
        }

        // Decode with UTF-8 validation
        final String content;
        try {
          content = utf8.decode(bytes);
        } on FormatException {
          if (mounted) {
            toastification.show(
              context: context,
              type: ToastificationType.error,
              style: ToastificationStyle.fillColored,
              title: const Text('Invalid File Encoding'),
              description: const Text('File must be UTF-8 encoded text'),
              autoCloseDuration: const Duration(seconds: 5),
            );
          }
          return;
        }

        _textController.text = content;
        setState(() {
          _selectedFileName = file.name;
          _parsedTasks = null;
          _duplicateTitles = {};
        });
      }
    } catch (e) {
      if (mounted) {
        toastification.show(
          context: context,
          type: ToastificationType.error,
          style: ToastificationStyle.fillColored,
          title: const Text('File Error'),
          description: Text('Failed to read file: $e'),
          autoCloseDuration: const Duration(seconds: 5),
        );
      }
    }
  }

  Future<void> _parseMarkdown() async {
    final markdown = _textController.text;
    if (markdown.trim().isEmpty) return;

    setState(() => _isParsing = true);

    try {
      final parsed = MarkdownTaskParser.parse(markdown);

      if (parsed.isEmpty) {
        if (mounted) {
          toastification.show(
            context: context,
            type: ToastificationType.warning,
            style: ToastificationStyle.fillColored,
            title: const Text('No Tasks Found'),
            description: const Text(
              'No checkbox tasks found. Use format: - [ ] task',
            ),
            autoCloseDuration: const Duration(seconds: 5),
          );
        }
        setState(() => _isParsing = false);
        return;
      }

      // Check for duplicates
      final data = await ref.read(unifiedDataProvider.future);
      final existingTitles = data.tasks
          .map((t) => t.title.toLowerCase())
          .toSet();

      final duplicates = parsed
          .where((p) => existingTitles.contains(p.title.toLowerCase()))
          .map((p) => p.title.toLowerCase())
          .toSet();

      setState(() {
        _parsedTasks = parsed;
        _duplicateTitles = duplicates;
        _isParsing = false;
      });
    } catch (e) {
      if (mounted) {
        toastification.show(
          context: context,
          type: ToastificationType.error,
          style: ToastificationStyle.fillColored,
          title: const Text('Parse Error'),
          description: Text('Failed to parse markdown: $e'),
          autoCloseDuration: const Duration(seconds: 5),
        );
      }
      setState(() => _isParsing = false);
    }
  }

  Future<void> _importTasks() async {
    if (_parsedTasks == null || _parsedTasks!.isEmpty) return;

    setState(() => _isProcessing = true);

    try {
      final repository = await ref.read(taskRepositoryProvider.future);
      final uuid = const Uuid();
      var imported = 0;
      var skipped = 0;

      for (final parsed in _parsedTasks!) {
        // Skip duplicates if strategy is skip
        if (_duplicateStrategy == DuplicateStrategy.skip &&
            _duplicateTitles.contains(parsed.title.toLowerCase())) {
          skipped++;
          continue;
        }

        final now = DateTime.now();
        final task = TaskEntity(
          id: uuid.v4(),
          integrationId: 'openza_tasks',
          title: parsed.title,
          status: parsed.isCompleted ? TaskStatus.completed : TaskStatus.pending,
          completedAt: parsed.isCompleted ? now : null,
          priority: 4, // Default to low
          createdAt: now,
          updatedAt: now,
        );

        await repository.createTask(task);
        imported++;
      }

      // Invalidate providers to refresh UI
      ref.invalidate(localTasksProvider);
      ref.invalidate(unifiedDataProvider);

      if (mounted) {
        Navigator.of(context).pop(imported);

        final message = skipped > 0
            ? 'Imported $imported task${imported == 1 ? '' : 's'}, skipped $skipped duplicate${skipped == 1 ? '' : 's'}'
            : 'Imported $imported task${imported == 1 ? '' : 's'}';

        toastification.show(
          context: context,
          type: ToastificationType.success,
          style: ToastificationStyle.fillColored,
          title: const Text('Import Complete'),
          description: Text(message),
          autoCloseDuration: const Duration(seconds: 3),
        );
      }
    } catch (e) {
      if (mounted) {
        toastification.show(
          context: context,
          type: ToastificationType.error,
          style: ToastificationStyle.fillColored,
          title: const Text('Import Error'),
          description: Text('Failed to import tasks: $e'),
          autoCloseDuration: const Duration(seconds: 5),
        );
      }
      setState(() => _isProcessing = false);
    }
  }
}
