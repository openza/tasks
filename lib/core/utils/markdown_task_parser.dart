/// Utility class for parsing markdown checkboxes into task data.
///
/// Supports GFM (GitHub Flavored Markdown) checkbox syntax:
/// - `- [ ] Uncompleted task`
/// - `- [x] Completed task`
/// - `* [ ] Asterisk bullet`
/// - `+ [ ] Plus bullet`
class MarkdownTaskParser {
  /// Maximum line length to process (prevents regex DoS)
  static const _maxLineLength = 10000;

  /// Regex pattern for GFM checkboxes
  /// Matches: optional whitespace, bullet (-, *, +), checkbox [ ] or [x], task title
  /// Uses non-greedy .+? to prevent catastrophic backtracking
  static final _checkboxPattern = RegExp(
    r'^\s*[-*+]\s*\[([ xX])\]\s*(.+?)$',
    multiLine: true,
  );

  /// Parse markdown text and extract tasks from checkboxes.
  ///
  /// Returns a list of [ParsedTask] objects, one for each checkbox found.
  /// Non-checkbox lines are ignored. Lines over 10,000 characters are skipped.
  static List<ParsedTask> parse(String markdown) {
    if (markdown.trim().isEmpty) {
      return [];
    }

    // Filter out excessively long lines to prevent regex performance issues
    final lines = markdown.split('\n');
    final safelines = lines.where((line) => line.length <= _maxLineLength);
    final safeMarkdown = safelines.join('\n');

    final matches = _checkboxPattern.allMatches(safeMarkdown);
    final tasks = <ParsedTask>[];

    for (final match in matches) {
      final checkboxState = match.group(1);
      final title = match.group(2)?.trim();

      if (title != null && title.isNotEmpty) {
        tasks.add(ParsedTask(
          title: _sanitizeTitle(title),
          isCompleted: checkboxState?.toLowerCase() == 'x',
        ));
      }
    }

    return tasks;
  }

  /// Parse a single line and return a task if it matches checkbox syntax.
  static ParsedTask? parseLine(String line) {
    if (line.length > _maxLineLength) {
      return null;
    }

    final match = _checkboxPattern.firstMatch(line);
    if (match == null) return null;

    final checkboxState = match.group(1);
    final title = match.group(2)?.trim();

    if (title == null || title.isEmpty) return null;

    return ParsedTask(
      title: _sanitizeTitle(title),
      isCompleted: checkboxState?.toLowerCase() == 'x',
    );
  }

  /// Sanitize task title by trimming and limiting length
  static String _sanitizeTitle(String title) {
    final trimmed = title.trim();
    // Limit title to 500 characters
    if (trimmed.length > 500) {
      return trimmed.substring(0, 500);
    }
    return trimmed;
  }

  /// Check if the given text contains any valid checkboxes
  static bool containsCheckboxes(String markdown) {
    return _checkboxPattern.hasMatch(markdown);
  }

  /// Get count of checkboxes in the markdown text
  static int countCheckboxes(String markdown) {
    return _checkboxPattern.allMatches(markdown).length;
  }
}

/// Represents a parsed task from markdown
class ParsedTask {
  final String title;
  final bool isCompleted;

  const ParsedTask({
    required this.title,
    required this.isCompleted,
  });

  @override
  String toString() => 'ParsedTask(title: $title, isCompleted: $isCompleted)';

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is ParsedTask &&
          runtimeType == other.runtimeType &&
          title == other.title &&
          isCompleted == other.isCompleted;

  @override
  int get hashCode => title.hashCode ^ isCompleted.hashCode;
}
