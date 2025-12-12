import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../core/utils/date_utils.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import '../badges/priority_badge.dart';
import '../badges/label_badge.dart';
import '../badges/project_badge.dart';

/// Detail view for a task - can be used as a modal or sidebar
class TaskDetail extends ConsumerStatefulWidget {
  final TaskEntity task;
  final ProjectEntity? project;
  final VoidCallback? onClose;
  final void Function(TaskEntity)? onUpdate;
  final void Function(TaskEntity)? onDelete;
  final void Function(TaskEntity)? onComplete;

  const TaskDetail({
    super.key,
    required this.task,
    this.project,
    this.onClose,
    this.onUpdate,
    this.onDelete,
    this.onComplete,
  });

  @override
  ConsumerState<TaskDetail> createState() => _TaskDetailState();
}

class _TaskDetailState extends ConsumerState<TaskDetail> {
  late TextEditingController _titleController;
  late TextEditingController _descriptionController;
  bool _isEditing = false;

  @override
  void initState() {
    super.initState();
    _titleController = TextEditingController(text: widget.task.title);
    _descriptionController =
        TextEditingController(text: widget.task.description ?? '');
  }

  @override
  void dispose() {
    _titleController.dispose();
    _descriptionController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 400,
      decoration: BoxDecoration(
        color: Theme.of(context).colorScheme.surface,
        border: Border(
          left: BorderSide(color: AppTheme.gray200),
        ),
      ),
      child: Column(
        children: [
          _buildHeader(context),
          Expanded(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  _buildTitleSection(context),
                  const SizedBox(height: 16),
                  _buildDescriptionSection(context),
                  const SizedBox(height: 24),
                  _buildMetadataSection(context),
                  const SizedBox(height: 24),
                  _buildLabelsSection(context),
                  const SizedBox(height: 24),
                  _buildDatesSection(context),
                  if (widget.task.provider != null &&
                      widget.task.provider != TaskProvider.local) ...[
                    const SizedBox(height: 24),
                    _buildProviderSection(context),
                  ],
                ],
              ),
            ),
          ),
          _buildFooter(context),
        ],
      ),
    );
  }

  Widget _buildHeader(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      decoration: BoxDecoration(
        border: Border(bottom: BorderSide(color: AppTheme.gray200)),
      ),
      child: Row(
        children: [
          Text(
            'Task Details',
            style: Theme.of(context).textTheme.titleMedium?.copyWith(
                  fontWeight: FontWeight.w600,
                ),
          ),
          const Spacer(),
          if (!_isEditing)
            IconButton(
              icon: Icon(LucideIcons.pencil, size: 18, color: AppTheme.gray500),
              onPressed: () => setState(() => _isEditing = true),
              tooltip: 'Edit',
            ),
          IconButton(
            icon: Icon(LucideIcons.x, size: 18, color: AppTheme.gray500),
            onPressed: widget.onClose,
            tooltip: 'Close',
          ),
        ],
      ),
    );
  }

  Widget _buildTitleSection(BuildContext context) {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _buildCheckbox(),
        const SizedBox(width: 12),
        Expanded(
          child: _isEditing
              ? TextField(
                  controller: _titleController,
                  style: Theme.of(context).textTheme.titleLarge,
                  decoration: const InputDecoration(
                    border: InputBorder.none,
                    hintText: 'Task title',
                  ),
                  maxLines: null,
                )
              : Text(
                  widget.task.title,
                  style: Theme.of(context).textTheme.titleLarge?.copyWith(
                        decoration: widget.task.isCompleted
                            ? TextDecoration.lineThrough
                            : null,
                        color: widget.task.isCompleted
                            ? AppTheme.gray400
                            : AppTheme.gray900,
                      ),
                ),
        ),
      ],
    );
  }

  Widget _buildCheckbox() {
    return GestureDetector(
      onTap: () => widget.onComplete?.call(widget.task),
      child: Container(
        width: 24,
        height: 24,
        decoration: BoxDecoration(
          shape: BoxShape.circle,
          border: Border.all(
            color:
                widget.task.isCompleted ? AppTheme.accentPink : AppTheme.gray400,
            width: 2,
          ),
          color:
              widget.task.isCompleted ? AppTheme.accentPink : Colors.transparent,
        ),
        child: widget.task.isCompleted
            ? const Icon(Icons.check, size: 16, color: Colors.white)
            : null,
      ),
    );
  }

  Widget _buildDescriptionSection(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Icon(LucideIcons.alignLeft, size: 16, color: AppTheme.gray400),
            const SizedBox(width: 8),
            Text(
              'Description',
              style: Theme.of(context).textTheme.labelMedium?.copyWith(
                    color: AppTheme.gray500,
                  ),
            ),
          ],
        ),
        const SizedBox(height: 8),
        _isEditing
            ? TextField(
                controller: _descriptionController,
                style: Theme.of(context).textTheme.bodyMedium,
                decoration: InputDecoration(
                  border: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(8),
                    borderSide: BorderSide(color: AppTheme.gray300),
                  ),
                  hintText: 'Add a description...',
                ),
                maxLines: 4,
              )
            : Container(
                width: double.infinity,
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: AppTheme.gray50,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  widget.task.description?.isNotEmpty == true
                      ? widget.task.description!
                      : 'No description',
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: widget.task.description?.isNotEmpty == true
                            ? AppTheme.gray700
                            : AppTheme.gray400,
                      ),
                ),
              ),
      ],
    );
  }

  Widget _buildMetadataSection(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // Priority
        _buildMetadataRow(
          context,
          icon: LucideIcons.flag,
          label: 'Priority',
          child: PriorityBadge(
            priority: widget.task.priority,
            provider: widget.task.provider,
            showLabel: true,
          ),
        ),
        const SizedBox(height: 12),

        // Project
        if (widget.project != null)
          _buildMetadataRow(
            context,
            icon: LucideIcons.folder,
            label: 'Project',
            child: ProjectBadge(project: widget.project!),
          ),

        // Energy level
        if (widget.task.energyLevel > 0) ...[
          const SizedBox(height: 12),
          _buildMetadataRow(
            context,
            icon: LucideIcons.zap,
            label: 'Energy Level',
            child: Text(
              '${widget.task.energyLevel}/5',
              style: TextStyle(
                fontSize: 13,
                color: AppTheme.energyColors[widget.task.energyLevel] ??
                    AppTheme.gray500,
                fontWeight: FontWeight.w500,
              ),
            ),
          ),
        ],

        // Focus time
        if (widget.task.focusTime) ...[
          const SizedBox(height: 12),
          _buildMetadataRow(
            context,
            icon: LucideIcons.brain,
            label: 'Focus Time',
            child: Text(
              'Required',
              style: TextStyle(
                fontSize: 13,
                color: AppTheme.primaryBlue,
                fontWeight: FontWeight.w500,
              ),
            ),
          ),
        ],

        // Estimated duration
        if (widget.task.estimatedDuration != null) ...[
          const SizedBox(height: 12),
          _buildMetadataRow(
            context,
            icon: LucideIcons.clock,
            label: 'Estimated Duration',
            child: Text(
              '${widget.task.estimatedDuration} minutes',
              style: TextStyle(
                fontSize: 13,
                color: AppTheme.gray700,
              ),
            ),
          ),
        ],
      ],
    );
  }

  Widget _buildMetadataRow(
    BuildContext context, {
    required IconData icon,
    required String label,
    required Widget child,
  }) {
    return Row(
      children: [
        Icon(icon, size: 16, color: AppTheme.gray400),
        const SizedBox(width: 8),
        SizedBox(
          width: 120,
          child: Text(
            label,
            style: Theme.of(context).textTheme.labelMedium?.copyWith(
                  color: AppTheme.gray500,
                ),
          ),
        ),
        child,
      ],
    );
  }

  Widget _buildLabelsSection(BuildContext context) {
    if (widget.task.labels.isEmpty) return const SizedBox.shrink();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Icon(LucideIcons.tag, size: 16, color: AppTheme.gray400),
            const SizedBox(width: 8),
            Text(
              'Labels',
              style: Theme.of(context).textTheme.labelMedium?.copyWith(
                    color: AppTheme.gray500,
                  ),
            ),
          ],
        ),
        const SizedBox(height: 8),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: widget.task.labels
              .map((label) => LabelBadge(label: label))
              .toList(),
        ),
      ],
    );
  }

  Widget _buildDatesSection(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Icon(LucideIcons.calendar, size: 16, color: AppTheme.gray400),
            const SizedBox(width: 8),
            Text(
              'Dates',
              style: Theme.of(context).textTheme.labelMedium?.copyWith(
                    color: AppTheme.gray500,
                  ),
            ),
          ],
        ),
        const SizedBox(height: 8),
        Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: AppTheme.gray50,
            borderRadius: BorderRadius.circular(8),
          ),
          child: Column(
            children: [
              if (widget.task.dueDate != null)
                _buildDateRow(
                  context,
                  label: 'Due',
                  date: widget.task.dueDate!,
                  isOverdue: widget.task.isOverdue,
                ),
              if (widget.task.completedAt != null) ...[
                if (widget.task.dueDate != null) const SizedBox(height: 8),
                _buildDateRow(
                  context,
                  label: 'Completed',
                  date: widget.task.completedAt!,
                ),
              ],
              const SizedBox(height: 8),
              _buildDateRow(
                context,
                label: 'Created',
                date: widget.task.createdAt,
              ),
              if (widget.task.updatedAt != null) ...[
                const SizedBox(height: 8),
                _buildDateRow(
                  context,
                  label: 'Updated',
                  date: widget.task.updatedAt!,
                ),
              ],
            ],
          ),
        ),
      ],
    );
  }

  Widget _buildDateRow(
    BuildContext context, {
    required String label,
    required DateTime date,
    bool isOverdue = false,
  }) {
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        Text(
          label,
          style: TextStyle(
            fontSize: 12,
            color: AppTheme.gray500,
          ),
        ),
        Text(
          AppDateUtils.formatForDisplay(date),
          style: TextStyle(
            fontSize: 12,
            color: isOverdue ? AppTheme.errorRed : AppTheme.gray700,
            fontWeight: isOverdue ? FontWeight.w600 : FontWeight.normal,
          ),
        ),
      ],
    );
  }

  Widget _buildProviderSection(BuildContext context) {
    String providerName;
    Color providerColor;

    switch (widget.task.provider) {
      case TaskProvider.todoist:
        providerName = 'Todoist';
        providerColor = const Color(0xFFE44332);
        break;
      case TaskProvider.msToDo:
        providerName = 'Microsoft To-Do';
        providerColor = const Color(0xFF00A4EF);
        break;
      default:
        return const SizedBox.shrink();
    }

    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: providerColor.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: providerColor.withValues(alpha: 0.3)),
      ),
      child: Row(
        children: [
          Container(
            width: 8,
            height: 8,
            decoration: BoxDecoration(
              color: providerColor,
              shape: BoxShape.circle,
            ),
          ),
          const SizedBox(width: 8),
          Text(
            'Synced from $providerName',
            style: TextStyle(
              fontSize: 12,
              color: providerColor,
              fontWeight: FontWeight.w500,
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildFooter(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        border: Border(top: BorderSide(color: AppTheme.gray200)),
      ),
      child: Row(
        children: [
          if (_isEditing) ...[
            Expanded(
              child: OutlinedButton(
                onPressed: () {
                  _titleController.text = widget.task.title;
                  _descriptionController.text = widget.task.description ?? '';
                  setState(() => _isEditing = false);
                },
                child: const Text('Cancel'),
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: FilledButton(
                onPressed: _saveChanges,
                child: const Text('Save'),
              ),
            ),
          ] else ...[
            Expanded(
              child: OutlinedButton.icon(
                onPressed: () => widget.onDelete?.call(widget.task),
                icon: Icon(LucideIcons.trash2, size: 16),
                label: const Text('Delete'),
                style: OutlinedButton.styleFrom(
                  foregroundColor: AppTheme.errorRed,
                ),
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: FilledButton.icon(
                onPressed: () => widget.onComplete?.call(widget.task),
                icon: Icon(
                  widget.task.isCompleted
                      ? LucideIcons.rotateCcw
                      : LucideIcons.check,
                  size: 16,
                ),
                label:
                    Text(widget.task.isCompleted ? 'Reopen' : 'Complete'),
              ),
            ),
          ],
        ],
      ),
    );
  }

  void _saveChanges() {
    final updatedTask = widget.task.copyWith(
      title: _titleController.text.trim(),
      description: _descriptionController.text.trim(),
      updatedAt: DateTime.now(),
    );
    widget.onUpdate?.call(updatedTask);
    setState(() => _isEditing = false);
  }
}
