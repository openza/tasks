import 'dart:io';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';
import 'package:toastification/toastification.dart';

import '../../../app/app_theme.dart';
import '../../../core/services/markdown_exporter.dart';
import '../../providers/task_provider.dart';

/// Dialog for exporting tasks to markdown format
class ExportMarkdownDialog extends ConsumerStatefulWidget {
  const ExportMarkdownDialog({super.key});

  /// Show the export dialog and return true if export was successful
  static Future<bool?> show(BuildContext context) async {
    return showDialog<bool>(
      context: context,
      builder: (context) => const ExportMarkdownDialog(),
    );
  }

  @override
  ConsumerState<ExportMarkdownDialog> createState() =>
      _ExportMarkdownDialogState();
}

class _ExportMarkdownDialogState extends ConsumerState<ExportMarkdownDialog> {
  bool _isGenerating = false;
  bool _isExporting = false;
  String? _generatedMarkdown;
  ExportStats? _stats;
  String? _error;

  @override
  void initState() {
    super.initState();
    _generatePreview();
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
                    if (_error != null)
                      _buildError(context)
                    else if (_isGenerating)
                      _buildLoading(context)
                    else if (_generatedMarkdown != null)
                      _buildPreview(context),
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
              LucideIcons.fileUp,
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
                  'Export to Markdown',
                  style: Theme.of(context).textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                ),
                Text(
                  'Export all tasks and projects as markdown',
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

  Widget _buildLoading(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(40),
        child: Column(
          children: [
            const CircularProgressIndicator(),
            const SizedBox(height: 16),
            Text(
              'Generating preview...',
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AppTheme.gray500,
                  ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildError(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppTheme.errorRed.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: AppTheme.errorRed.withValues(alpha: 0.3)),
      ),
      child: Row(
        children: [
          Icon(LucideIcons.alertCircle, size: 20, color: AppTheme.errorRed),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Export Failed',
                  style: Theme.of(context).textTheme.titleSmall?.copyWith(
                        color: AppTheme.errorRed,
                        fontWeight: FontWeight.w600,
                      ),
                ),
                Text(
                  _error!,
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: AppTheme.errorRed,
                      ),
                ),
              ],
            ),
          ),
          TextButton(
            onPressed: _generatePreview,
            child: const Text('Retry'),
          ),
        ],
      ),
    );
  }

  Widget _buildPreview(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // Statistics
        if (_stats != null) ...[
          _buildStatsSection(context),
          const SizedBox(height: 20),
        ],

        // Preview
        Row(
          children: [
            Icon(LucideIcons.eye, size: 14, color: AppTheme.gray400),
            const SizedBox(width: 6),
            Text(
              'Preview',
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w500,
                color: AppTheme.gray500,
              ),
            ),
          ],
        ),
        const SizedBox(height: 8),
        Container(
          constraints: const BoxConstraints(maxHeight: 300),
          width: double.infinity,
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: AppTheme.gray100,
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: AppTheme.gray200),
          ),
          child: SingleChildScrollView(
            child: SelectableText(
              _getPreviewText(),
              style: const TextStyle(
                fontFamily: 'monospace',
                fontSize: 12,
                height: 1.5,
              ),
            ),
          ),
        ),
        const SizedBox(height: 8),
        Text(
          'Showing first 50 lines. Full export will be saved to file.',
          style: Theme.of(context).textTheme.bodySmall?.copyWith(
                color: AppTheme.gray400,
              ),
        ),
      ],
    );
  }

  Widget _buildStatsSection(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppTheme.primaryBlue.withValues(alpha: 0.05),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: AppTheme.primaryBlue.withValues(alpha: 0.2)),
      ),
      child: Row(
        children: [
          Expanded(
            child: _StatItem(
              icon: LucideIcons.checkSquare,
              label: 'Tasks',
              value: '${_stats!.totalTasks}',
              sublabel:
                  '${_stats!.activeTasks} active, ${_stats!.completedTasks} done',
            ),
          ),
          Container(
            height: 40,
            width: 1,
            color: AppTheme.primaryBlue.withValues(alpha: 0.2),
          ),
          Expanded(
            child: _StatItem(
              icon: LucideIcons.folder,
              label: 'Projects',
              value: '${_stats!.totalProjects}',
            ),
          ),
          Container(
            height: 40,
            width: 1,
            color: AppTheme.primaryBlue.withValues(alpha: 0.2),
          ),
          Expanded(
            child: _StatItem(
              icon: LucideIcons.tag,
              label: 'Labels',
              value: '${_stats!.totalLabels}',
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildFooter(BuildContext context) {
    final canExport = _generatedMarkdown != null && !_isExporting;

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
          Expanded(
            flex: 2,
            child: FilledButton.icon(
              onPressed: canExport ? _exportToFile : null,
              icon: _isExporting
                  ? const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(
                        strokeWidth: 2,
                        color: Colors.white,
                      ),
                    )
                  : const Icon(LucideIcons.download, size: 18),
              label: Text(_isExporting ? 'Exporting...' : 'Export to File'),
            ),
          ),
        ],
      ),
    );
  }

  String _getPreviewText() {
    if (_generatedMarkdown == null) return '';

    final lines = _generatedMarkdown!.split('\n');
    if (lines.length <= 50) return _generatedMarkdown!;

    return '${lines.take(50).join('\n')}\n\n... (${lines.length - 50} more lines)';
  }

  Future<void> _generatePreview() async {
    setState(() {
      _isGenerating = true;
      _error = null;
    });

    try {
      final data = await ref.read(unifiedDataProvider.future);

      // Generate stats first
      final stats = MarkdownExporter.getStats(
        tasks: data.tasks,
        projects: data.projects,
        labels: data.labels,
      );

      // Generate markdown
      final markdown = await MarkdownExporter.export(
        tasks: data.tasks,
        projects: data.projects,
        labels: data.labels,
      );

      if (mounted) {
        setState(() {
          _generatedMarkdown = markdown;
          _stats = stats;
          _isGenerating = false;
        });
      }
    } catch (e) {
      if (mounted) {
        setState(() {
          _error = 'Failed to generate export: $e';
          _isGenerating = false;
        });
      }
    }
  }

  Future<void> _exportToFile() async {
    if (_generatedMarkdown == null) return;

    setState(() => _isExporting = true);

    try {
      // Generate default filename
      final now = DateTime.now();
      final defaultFileName =
          'openza_export_${now.year}${now.month.toString().padLeft(2, '0')}${now.day.toString().padLeft(2, '0')}.md';

      // Show save dialog
      final savePath = await FilePicker.platform.saveFile(
        dialogTitle: 'Save Markdown Export',
        fileName: defaultFileName,
        type: FileType.custom,
        allowedExtensions: ['md'],
      );

      if (savePath == null) {
        // User cancelled
        if (mounted) {
          setState(() => _isExporting = false);
        }
        return;
      }

      // Ensure .md extension
      final filePath = savePath.endsWith('.md') ? savePath : '$savePath.md';

      // Write file
      final file = File(filePath);
      await file.writeAsString(_generatedMarkdown!);

      if (mounted) {
        // Just close dialog and return success - the caller will show toast
        Navigator.of(context).pop(true);
      }
    } catch (e) {
      if (mounted) {
        toastification.show(
          context: context,
          type: ToastificationType.error,
          style: ToastificationStyle.fillColored,
          title: const Text('Export Failed'),
          description: Text('Failed to save file: $e'),
          autoCloseDuration: const Duration(seconds: 5),
        );
        setState(() => _isExporting = false);
      }
    }
  }
}

class _StatItem extends StatelessWidget {
  final IconData icon;
  final String label;
  final String value;
  final String? sublabel;

  const _StatItem({
    required this.icon,
    required this.label,
    required this.value,
    this.sublabel,
  });

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        Icon(icon, size: 16, color: AppTheme.primaryBlue),
        const SizedBox(height: 4),
        Text(
          value,
          style: Theme.of(context).textTheme.titleLarge?.copyWith(
                fontWeight: FontWeight.bold,
                color: AppTheme.primaryBlue,
              ),
        ),
        Text(
          label,
          style: Theme.of(context).textTheme.bodySmall?.copyWith(
                color: AppTheme.gray500,
              ),
        ),
        if (sublabel != null)
          Text(
            sublabel!,
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  fontSize: 10,
                  color: AppTheme.gray400,
                ),
          ),
      ],
    );
  }
}
