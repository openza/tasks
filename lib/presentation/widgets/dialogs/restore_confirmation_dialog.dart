import 'package:flutter/material.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';

/// Dialog for confirming restore actions with typing confirmation
class RestoreConfirmationDialog extends StatefulWidget {
  final String? backupDate;
  final String? fileName;
  final bool isExternalFile;

  const RestoreConfirmationDialog({
    super.key,
    this.backupDate,
    this.fileName,
    this.isExternalFile = false,
  });

  /// Show restore confirmation dialog
  /// Returns true if user confirmed, false/null otherwise
  static Future<bool?> show(
    BuildContext context, {
    String? backupDate,
    String? fileName,
    bool isExternalFile = false,
  }) {
    return showDialog<bool>(
      context: context,
      barrierDismissible: false,
      builder: (context) => RestoreConfirmationDialog(
        backupDate: backupDate,
        fileName: fileName,
        isExternalFile: isExternalFile,
      ),
    );
  }

  @override
  State<RestoreConfirmationDialog> createState() =>
      _RestoreConfirmationDialogState();
}

class _RestoreConfirmationDialogState extends State<RestoreConfirmationDialog> {
  final _textController = TextEditingController();
  bool _isConfirmEnabled = false;

  static const String _confirmText = 'RESTORE';

  @override
  void initState() {
    super.initState();
    _textController.addListener(_onTextChanged);
  }

  @override
  void dispose() {
    _textController.dispose();
    super.dispose();
  }

  void _onTextChanged() {
    final isMatch = _textController.text.trim().toUpperCase() == _confirmText;
    if (isMatch != _isConfirmEnabled) {
      setState(() {
        _isConfirmEnabled = isMatch;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    return Dialog(
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      child: Container(
        width: 420,
        constraints: const BoxConstraints(maxHeight: 500),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            _buildHeader(context),
            Padding(
              padding: const EdgeInsets.all(20),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  _buildWarning(context),
                  const SizedBox(height: 16),
                  _buildBackupInfo(context),
                  const SizedBox(height: 20),
                  _buildConfirmationInput(context),
                ],
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
              color: AppTheme.warningOrange.withValues(alpha: 0.1),
              borderRadius: BorderRadius.circular(8),
            ),
            child: Icon(
              LucideIcons.alertTriangle,
              size: 20,
              color: AppTheme.warningOrange,
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  widget.isExternalFile ? 'Restore from File' : 'Restore Backup',
                  style: Theme.of(context).textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                ),
                Text(
                  'This action will replace your current data',
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: AppTheme.gray500,
                      ),
                ),
              ],
            ),
          ),
          IconButton(
            icon: Icon(LucideIcons.x, color: AppTheme.gray400),
            onPressed: () => Navigator.of(context).pop(false),
          ),
        ],
      ),
    );
  }

  Widget _buildWarning(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: AppTheme.errorRed.withValues(alpha: 0.05),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: AppTheme.errorRed.withValues(alpha: 0.2)),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(LucideIcons.alertCircle, size: 18, color: AppTheme.errorRed),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Warning: Destructive Action',
                  style: TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w600,
                    color: AppTheme.errorRed,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  'All your current tasks, projects, and settings will be permanently replaced. The app will need to restart after restore.',
                  style: TextStyle(
                    fontSize: 12,
                    color: AppTheme.gray700,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildBackupInfo(BuildContext context) {
    final hasInfo = widget.backupDate != null || widget.fileName != null;
    if (!hasInfo) return const SizedBox.shrink();

    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: AppTheme.gray50,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: AppTheme.gray200),
      ),
      child: Row(
        children: [
          Icon(
            widget.isExternalFile ? LucideIcons.file : LucideIcons.hardDrive,
            size: 18,
            color: AppTheme.gray500,
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                if (widget.backupDate != null)
                  Text(
                    widget.isExternalFile
                        ? 'Restoring from external file'
                        : 'Backup from ${widget.backupDate}',
                    style: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w500,
                    ),
                  ),
                if (widget.fileName != null) ...[
                  const SizedBox(height: 2),
                  Text(
                    widget.fileName!,
                    style: TextStyle(
                      fontSize: 12,
                      color: AppTheme.gray500,
                    ),
                    overflow: TextOverflow.ellipsis,
                  ),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildConfirmationInput(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        RichText(
          text: TextSpan(
            style: TextStyle(fontSize: 13, color: AppTheme.gray700),
            children: [
              const TextSpan(text: 'To confirm, type '),
              TextSpan(
                text: _confirmText,
                style: TextStyle(
                  fontWeight: FontWeight.bold,
                  color: AppTheme.errorRed,
                ),
              ),
              const TextSpan(text: ' below:'),
            ],
          ),
        ),
        const SizedBox(height: 8),
        TextField(
          controller: _textController,
          autofocus: true,
          decoration: InputDecoration(
            hintText: 'Type $_confirmText to confirm',
            hintStyle: TextStyle(color: AppTheme.gray400),
            contentPadding:
                const EdgeInsets.symmetric(horizontal: 12, vertical: 12),
            border: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
              borderSide: BorderSide(color: AppTheme.gray300),
            ),
            enabledBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
              borderSide: BorderSide(color: AppTheme.gray300),
            ),
            focusedBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
              borderSide: BorderSide(
                color: _isConfirmEnabled
                    ? AppTheme.successGreen
                    : AppTheme.primaryBlue,
                width: 2,
              ),
            ),
            suffixIcon: _isConfirmEnabled
                ? Icon(LucideIcons.checkCircle, color: AppTheme.successGreen)
                : null,
          ),
        ),
      ],
    );
  }

  Widget _buildFooter(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        border: Border(top: BorderSide(color: AppTheme.gray200)),
      ),
      child: Row(
        children: [
          Expanded(
            child: OutlinedButton(
              onPressed: () => Navigator.of(context).pop(false),
              child: const Text('Cancel'),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            flex: 2,
            child: FilledButton.icon(
              onPressed: _isConfirmEnabled
                  ? () => Navigator.of(context).pop(true)
                  : null,
              style: FilledButton.styleFrom(
                backgroundColor: AppTheme.warningOrange,
                foregroundColor: Colors.white,
                disabledBackgroundColor: AppTheme.gray200,
                disabledForegroundColor: AppTheme.gray400,
              ),
              icon: const Icon(LucideIcons.rotateCcw, size: 18),
              label: const Text('Restore'),
            ),
          ),
        ],
      ),
    );
  }
}
