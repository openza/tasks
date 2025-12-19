import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../core/utils/date_utils.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import '../../../domain/entities/label.dart';
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
  late int _editPriority;
  late DateTime? _editDueDate;
  late List<String> _editLabelNames;
  bool _hasUnsavedChanges = false;

  @override
  void initState() {
    super.initState();
    _titleController = TextEditingController(text: widget.task.title);
    _descriptionController =
        TextEditingController(text: widget.task.description ?? '');
    _editPriority = widget.task.priority;
    _editDueDate = widget.task.dueDate;
    _editLabelNames = widget.task.labels.map((l) => l.name).toList();

    _titleController.addListener(_onEditChanged);
    _descriptionController.addListener(_onEditChanged);
  }

  void _onEditChanged() {
    if (_isEditing) {
      setState(() => _hasUnsavedChanges = true);
    }
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
                  if (!widget.task.isNative) ...[
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
            onPressed: widget.onClose ?? () {},
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
          child: _isEditing
              ? _buildPrioritySelector()
              : PriorityBadge(
                  priority: widget.task.priority,
                  integrationId: widget.task.integrationId,
                  showLabel: true,
                ),
        ),
        const SizedBox(height: 12),

        // Due Date
        _buildMetadataRow(
          context,
          icon: LucideIcons.calendar,
          label: 'Due Date',
          child: _isEditing ? _buildDueDatePicker(context) : _buildDueDateDisplay(),
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
      ],
    );
  }

  Widget _buildPrioritySelector() {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8),
      decoration: BoxDecoration(
        border: Border.all(color: AppTheme.gray300),
        borderRadius: BorderRadius.circular(6),
      ),
      child: DropdownButton<int>(
        value: _editPriority,
        items: const [
          DropdownMenuItem(value: 1, child: Text('P1 - Urgent')),
          DropdownMenuItem(value: 2, child: Text('P2 - High')),
          DropdownMenuItem(value: 3, child: Text('P3 - Normal')),
          DropdownMenuItem(value: 4, child: Text('P4 - Low')),
        ],
        onChanged: (value) {
          if (value != null) {
            setState(() {
              _editPriority = value;
              _hasUnsavedChanges = true;
            });
          }
        },
        underline: const SizedBox.shrink(),
        isDense: true,
      ),
    );
  }

  Widget _buildDueDateDisplay() {
    if (widget.task.dueDate == null) {
      return Text(
        'No due date',
        style: TextStyle(fontSize: 13, color: AppTheme.gray400),
      );
    }
    return Text(
      AppDateUtils.formatForDisplay(widget.task.dueDate!),
      style: TextStyle(
        fontSize: 13,
        color: widget.task.isOverdue ? AppTheme.errorRed : AppTheme.gray700,
        fontWeight: widget.task.isOverdue ? FontWeight.w600 : FontWeight.normal,
      ),
    );
  }

  Widget _buildDueDatePicker(BuildContext context) {
    return Row(
      children: [
        InkWell(
          onTap: () async {
            final picked = await showDatePicker(
              context: context,
              initialDate: _editDueDate ?? DateTime.now(),
              firstDate: DateTime.now().subtract(const Duration(days: 365)),
              lastDate: DateTime.now().add(const Duration(days: 365 * 5)),
            );
            if (picked != null) {
              setState(() {
                _editDueDate = picked;
                _hasUnsavedChanges = true;
              });
            }
          },
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
            decoration: BoxDecoration(
              border: Border.all(color: AppTheme.gray300),
              borderRadius: BorderRadius.circular(6),
            ),
            child: Row(
              children: [
                Text(
                  _editDueDate != null
                      ? AppDateUtils.formatForDisplay(_editDueDate!)
                      : 'Select date',
                  style: TextStyle(
                    fontSize: 13,
                    color: _editDueDate != null ? AppTheme.gray700 : AppTheme.gray400,
                  ),
                ),
                const SizedBox(width: 8),
                Icon(LucideIcons.calendar, size: 14, color: AppTheme.gray400),
              ],
            ),
          ),
        ),
        if (_editDueDate != null) ...[
          const SizedBox(width: 8),
          IconButton(
            icon: Icon(LucideIcons.x, size: 14, color: AppTheme.gray400),
            onPressed: () => setState(() {
              _editDueDate = null;
              _hasUnsavedChanges = true;
            }),
            tooltip: 'Clear due date',
            constraints: const BoxConstraints(),
            padding: const EdgeInsets.all(4),
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
    if (!_isEditing && widget.task.labels.isEmpty) return const SizedBox.shrink();

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
        _isEditing ? _buildLabelsEditor(context) : _buildLabelsDisplay(),
      ],
    );
  }

  Widget _buildLabelsDisplay() {
    return Wrap(
      spacing: 8,
      runSpacing: 8,
      children: widget.task.labels
          .map((label) => LabelBadge(label: label))
          .toList(),
    );
  }

  Widget _buildLabelsEditor(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            ..._editLabelNames.map((labelName) => _buildEditableLabel(labelName)),
            _buildAddLabelButton(context),
          ],
        ),
      ],
    );
  }

  Widget _buildEditableLabel(String labelName) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
      decoration: BoxDecoration(
        color: AppTheme.primaryBlue.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(16),
        border: Border.all(color: AppTheme.primaryBlue.withValues(alpha: 0.3)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            labelName,
            style: TextStyle(
              fontSize: 12,
              color: AppTheme.primaryBlue,
              fontWeight: FontWeight.w500,
            ),
          ),
          const SizedBox(width: 4),
          InkWell(
            onTap: () {
              setState(() {
                _editLabelNames.remove(labelName);
                _hasUnsavedChanges = true;
              });
            },
            child: Icon(LucideIcons.x, size: 14, color: AppTheme.primaryBlue),
          ),
        ],
      ),
    );
  }

  Widget _buildAddLabelButton(BuildContext context) {
    return InkWell(
      onTap: () => _showAddLabelDialog(context),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        decoration: BoxDecoration(
          color: AppTheme.gray100,
          borderRadius: BorderRadius.circular(16),
          border: Border.all(color: AppTheme.gray300),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(LucideIcons.plus, size: 14, color: AppTheme.gray500),
            const SizedBox(width: 4),
            Text(
              'Add label',
              style: TextStyle(
                fontSize: 12,
                color: AppTheme.gray500,
              ),
            ),
          ],
        ),
      ),
    );
  }

  void _showAddLabelDialog(BuildContext context) {
    final controller = TextEditingController();
    showDialog(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Add Label'),
        content: TextField(
          controller: controller,
          autofocus: true,
          decoration: const InputDecoration(
            hintText: 'Label name',
            border: OutlineInputBorder(),
          ),
          onSubmitted: (value) {
            if (value.trim().isNotEmpty) {
              setState(() {
                _editLabelNames.add(value.trim());
                _hasUnsavedChanges = true;
              });
              Navigator.pop(context);
            }
          },
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () {
              final value = controller.text.trim();
              if (value.isNotEmpty) {
                setState(() {
                  _editLabelNames.add(value);
                  _hasUnsavedChanges = true;
                });
                Navigator.pop(context);
              }
            },
            child: const Text('Add'),
          ),
        ],
      ),
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

    switch (widget.task.integrationId) {
      case 'todoist':
        providerName = 'Todoist';
        providerColor = const Color(0xFFE44332);
        break;
      case 'msToDo':
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
                onPressed: _cancelEditing,
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

  void _cancelEditing() {
    if (_hasUnsavedChanges) {
      _showUnsavedChangesDialog();
    } else {
      _resetEditState();
    }
  }

  void _showUnsavedChangesDialog() {
    showDialog(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Unsaved Changes'),
        content: const Text('You have unsaved changes. Do you want to discard them?'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Keep Editing'),
          ),
          FilledButton(
            onPressed: () {
              Navigator.pop(context);
              _resetEditState();
            },
            style: FilledButton.styleFrom(
              backgroundColor: AppTheme.errorRed,
            ),
            child: const Text('Discard'),
          ),
        ],
      ),
    );
  }

  void _resetEditState() {
    _titleController.text = widget.task.title;
    _descriptionController.text = widget.task.description ?? '';
    setState(() {
      _editPriority = widget.task.priority;
      _editDueDate = widget.task.dueDate;
      _editLabelNames = widget.task.labels.map((l) => l.name).toList();
      _isEditing = false;
      _hasUnsavedChanges = false;
    });
  }

  void _saveChanges() {
    // Create updated labels from edited names
    final updatedLabels = _editLabelNames.map((name) {
      // Try to find existing label, otherwise create new one
      final existing = widget.task.labels.where((l) => l.name == name);
      if (existing.isNotEmpty) {
        return existing.first;
      }
      return LabelEntity(
        id: 'local_${DateTime.now().millisecondsSinceEpoch}_$name',
        name: name,
        color: '#${AppTheme.primaryBlue.toARGB32().toRadixString(16).substring(2)}',
        createdAt: DateTime.now(),
        integrationId: widget.task.integrationId,
      );
    }).toList();

    final updatedTask = widget.task.copyWith(
      title: _titleController.text.trim(),
      description: _descriptionController.text.trim(),
      priority: _editPriority,
      dueDate: _editDueDate,
      labels: updatedLabels,
      updatedAt: DateTime.now(),
    );
    widget.onUpdate?.call(updatedTask);
    setState(() {
      _isEditing = false;
      _hasUnsavedChanges = false;
    });
  }
}
