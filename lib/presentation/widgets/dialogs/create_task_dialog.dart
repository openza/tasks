import 'package:flutter/material.dart';
import 'package:lucide_icons/lucide_icons.dart';
import 'package:uuid/uuid.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import '../../../domain/entities/label.dart';

/// Dialog for creating a new task
class CreateTaskDialog extends StatefulWidget {
  final List<ProjectEntity> projects;
  final List<LabelEntity> labels;
  final String? defaultProjectId;
  final void Function(TaskEntity task)? onCreate;

  const CreateTaskDialog({
    super.key,
    this.projects = const [],
    this.labels = const [],
    this.defaultProjectId,
    this.onCreate,
  });

  static Future<TaskEntity?> show(
    BuildContext context, {
    List<ProjectEntity> projects = const [],
    List<LabelEntity> labels = const [],
    String? defaultProjectId,
  }) async {
    return showDialog<TaskEntity>(
      context: context,
      builder: (context) => CreateTaskDialog(
        projects: projects,
        labels: labels,
        defaultProjectId: defaultProjectId,
        onCreate: (task) => Navigator.of(context).pop(task),
      ),
    );
  }

  @override
  State<CreateTaskDialog> createState() => _CreateTaskDialogState();
}

class _CreateTaskDialogState extends State<CreateTaskDialog> {
  final _formKey = GlobalKey<FormState>();
  final _titleController = TextEditingController();
  final _descriptionController = TextEditingController();

  String? _selectedProjectId;
  int _priority = 4; // Default: Low
  DateTime? _dueDate;
  final List<LabelEntity> _selectedLabels = [];

  @override
  void initState() {
    super.initState();
    _selectedProjectId = widget.defaultProjectId;
  }

  @override
  void dispose() {
    _titleController.dispose();
    _descriptionController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Dialog(
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      child: Container(
        width: 500,
        constraints: const BoxConstraints(maxHeight: 600),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            _buildHeader(context),
            Flexible(
              child: SingleChildScrollView(
                padding: const EdgeInsets.all(20),
                child: Form(
                  key: _formKey,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      _buildTitleField(context),
                      const SizedBox(height: 16),
                      _buildDescriptionField(context),
                      const SizedBox(height: 20),
                      _buildDivider(),
                      const SizedBox(height: 20),
                      _buildProjectSelector(context),
                      const SizedBox(height: 16),
                      _buildPrioritySelector(context),
                      const SizedBox(height: 16),
                      _buildDueDateSelector(context),
                      const SizedBox(height: 16),
                      if (widget.labels.isNotEmpty) ...[
                        _buildLabelsSelector(context),
                      ],
                    ],
                  ),
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
              LucideIcons.plus,
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
                  'Create Task',
                  style: Theme.of(context).textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                ),
                Text(
                  'Add a new task to your list',
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

  Widget _buildTitleField(BuildContext context) {
    return TextFormField(
      controller: _titleController,
      autofocus: true,
      decoration: InputDecoration(
        labelText: 'Task Title',
        hintText: 'What needs to be done?',
        prefixIcon: Icon(LucideIcons.checkCircle, color: AppTheme.gray400),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
        ),
      ),
      validator: (value) {
        if (value == null || value.trim().isEmpty) {
          return 'Please enter a task title';
        }
        return null;
      },
    );
  }

  Widget _buildDescriptionField(BuildContext context) {
    return TextFormField(
      controller: _descriptionController,
      maxLines: 3,
      decoration: InputDecoration(
        labelText: 'Description (optional)',
        hintText: 'Add more details...',
        prefixIcon: Padding(
          padding: const EdgeInsets.only(bottom: 48),
          child: Icon(LucideIcons.alignLeft, color: AppTheme.gray400),
        ),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
        ),
      ),
    );
  }

  Widget _buildDivider() {
    return Divider(color: AppTheme.gray200, height: 1);
  }

  Widget _buildProjectSelector(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _buildSectionLabel(context, LucideIcons.folder, 'Project'),
        const SizedBox(height: 8),
        DropdownButtonFormField<String?>(
          value: _selectedProjectId,
          decoration: InputDecoration(
            border: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
            ),
            contentPadding: const EdgeInsets.symmetric(
              horizontal: 12,
              vertical: 12,
            ),
          ),
          hint: const Text('Select a project'),
          items: [
            const DropdownMenuItem(
              value: null,
              child: Text('No Project (Inbox)'),
            ),
            ...widget.projects.map((p) => DropdownMenuItem(
                  value: p.id,
                  child: Row(
                    children: [
                      Container(
                        width: 10,
                        height: 10,
                        decoration: BoxDecoration(
                          color: _parseColor(p.color),
                          borderRadius: BorderRadius.circular(2),
                        ),
                      ),
                      const SizedBox(width: 8),
                      Text(p.name),
                    ],
                  ),
                )),
          ],
          onChanged: (value) => setState(() => _selectedProjectId = value),
        ),
      ],
    );
  }

  Widget _buildPrioritySelector(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _buildSectionLabel(context, LucideIcons.flag, 'Priority'),
        const SizedBox(height: 8),
        Row(
          children: [
            _buildPriorityChip(1, 'High', AppTheme.priorityColors[1]!),
            const SizedBox(width: 8),
            _buildPriorityChip(2, 'Medium', AppTheme.priorityColors[2]!),
            const SizedBox(width: 8),
            _buildPriorityChip(3, 'Normal', AppTheme.priorityColors[3]!),
            const SizedBox(width: 8),
            _buildPriorityChip(4, 'Low', AppTheme.priorityColors[4]!),
          ],
        ),
      ],
    );
  }

  Widget _buildPriorityChip(int priority, String label, Color color) {
    final isSelected = _priority == priority;
    return Expanded(
      child: GestureDetector(
        onTap: () => setState(() => _priority = priority),
        child: Container(
          padding: const EdgeInsets.symmetric(vertical: 10),
          decoration: BoxDecoration(
            color: isSelected ? color.withValues(alpha: 0.15) : AppTheme.gray50,
            borderRadius: BorderRadius.circular(8),
            border: Border.all(
              color: isSelected ? color : AppTheme.gray200,
              width: isSelected ? 2 : 1,
            ),
          ),
          child: Text(
            label,
            textAlign: TextAlign.center,
            style: TextStyle(
              fontSize: 12,
              fontWeight: isSelected ? FontWeight.w600 : FontWeight.w500,
              color: isSelected ? color : AppTheme.gray600,
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildDueDateSelector(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _buildSectionLabel(context, LucideIcons.calendar, 'Due Date'),
        const SizedBox(height: 8),
        InkWell(
          onTap: _selectDueDate,
          borderRadius: BorderRadius.circular(8),
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 14),
            decoration: BoxDecoration(
              border: Border.all(color: AppTheme.gray300),
              borderRadius: BorderRadius.circular(8),
            ),
            child: Row(
              children: [
                Icon(
                  LucideIcons.calendar,
                  size: 18,
                  color: _dueDate != null ? AppTheme.primaryBlue : AppTheme.gray400,
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    _dueDate != null ? _formatDate(_dueDate!) : 'No due date',
                    style: TextStyle(
                      color: _dueDate != null ? AppTheme.gray700 : AppTheme.gray400,
                    ),
                  ),
                ),
                if (_dueDate != null)
                  GestureDetector(
                    onTap: () => setState(() => _dueDate = null),
                    child: Icon(LucideIcons.x, size: 16, color: AppTheme.gray400),
                  ),
              ],
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildLabelsSelector(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _buildSectionLabel(context, LucideIcons.tag, 'Labels'),
        const SizedBox(height: 8),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: widget.labels.map((label) {
            final isSelected = _selectedLabels.contains(label);
            return GestureDetector(
              onTap: () {
                setState(() {
                  if (isSelected) {
                    _selectedLabels.remove(label);
                  } else {
                    _selectedLabels.add(label);
                  }
                });
              },
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
                decoration: BoxDecoration(
                  color: isSelected
                      ? _parseColor(label.color).withValues(alpha: 0.15)
                      : AppTheme.gray50,
                  borderRadius: BorderRadius.circular(16),
                  border: Border.all(
                    color: isSelected ? _parseColor(label.color) : AppTheme.gray200,
                  ),
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Container(
                      width: 8,
                      height: 8,
                      decoration: BoxDecoration(
                        color: _parseColor(label.color),
                        shape: BoxShape.circle,
                      ),
                    ),
                    const SizedBox(width: 6),
                    Text(
                      label.name,
                      style: TextStyle(
                        fontSize: 12,
                        fontWeight: isSelected ? FontWeight.w600 : FontWeight.w500,
                        color: isSelected ? _parseColor(label.color) : AppTheme.gray600,
                      ),
                    ),
                  ],
                ),
              ),
            );
          }).toList(),
        ),
      ],
    );
  }

  Widget _buildSectionLabel(
      BuildContext context, IconData icon, String label) {
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
              onPressed: () => Navigator.of(context).pop(),
              child: const Text('Cancel'),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            flex: 2,
            child: FilledButton.icon(
              onPressed: _createTask,
              icon: Icon(LucideIcons.plus, size: 18),
              label: const Text('Create Task'),
            ),
          ),
        ],
      ),
    );
  }

  Future<void> _selectDueDate() async {
    final date = await showDatePicker(
      context: context,
      initialDate: _dueDate ?? DateTime.now(),
      firstDate: DateTime.now().subtract(const Duration(days: 365)),
      lastDate: DateTime.now().add(const Duration(days: 365 * 2)),
    );
    if (date != null) {
      setState(() => _dueDate = date);
    }
  }

  String _formatDate(DateTime date) {
    final now = DateTime.now();
    final today = DateTime(now.year, now.month, now.day);
    final dateOnly = DateTime(date.year, date.month, date.day);

    if (dateOnly == today) return 'Today';
    if (dateOnly == today.add(const Duration(days: 1))) return 'Tomorrow';
    if (dateOnly == today.subtract(const Duration(days: 1))) return 'Yesterday';

    return '${date.month}/${date.day}/${date.year}';
  }

  void _createTask() {
    if (!_formKey.currentState!.validate()) return;

    final now = DateTime.now();
    final task = TaskEntity(
      id: const Uuid().v4(),
      integrationId: 'openza_tasks',
      title: _titleController.text.trim(),
      description: _descriptionController.text.trim().isNotEmpty
          ? _descriptionController.text.trim()
          : null,
      priority: _priority,
      status: TaskStatus.pending,
      projectId: _selectedProjectId,
      dueDate: _dueDate,
      labels: _selectedLabels,
      createdAt: now,
      updatedAt: now,
    );

    widget.onCreate?.call(task);
  }

  Color _parseColor(String colorStr) {
    if (colorStr.startsWith('#')) {
      try {
        return Color(int.parse(colorStr.substring(1), radix: 16) + 0xFF000000);
      } catch (_) {
        return AppTheme.gray500;
      }
    }
    return AppTheme.gray500;
  }
}
