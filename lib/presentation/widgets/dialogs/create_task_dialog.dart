import 'package:flutter/material.dart';
import 'package:lucide_icons/lucide_icons.dart';
import 'package:uuid/uuid.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import '../../../domain/entities/label.dart';

/// Simplified dialog for creating a new task
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
    // Filter to local projects and labels only
    final localProjects = projects
        .where((p) => p.integrationId == 'openza_tasks')
        .toList();
    final localLabels = labels
        .where((l) => l.integrationId == 'openza_tasks')
        .toList();

    return showDialog<TaskEntity>(
      context: context,
      builder: (context) => CreateTaskDialog(
        projects: localProjects,
        labels: localLabels,
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
  bool _showDescription = false;

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

  void _submitIfValid() {
    if (_titleController.text.trim().isNotEmpty) {
      _createTask();
    }
  }

  @override
  Widget build(BuildContext context) {
    return Dialog(
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
      child: Container(
        width: 600,
        padding: const EdgeInsets.all(20),
        child: Form(
          key: _formKey,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _buildHeader(context),
              const SizedBox(height: 16),
              _buildTitleField(context),
              const SizedBox(height: 12),
              _buildDescriptionField(context),
              const SizedBox(height: 16),
              _buildMetadataToolbar(context),
              const SizedBox(height: 20),
              _buildFooter(context),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildHeader(BuildContext context) {
    return Row(
      children: [
        Text(
          'New Task',
          style: TextStyle(
            fontSize: 16,
            fontWeight: FontWeight.w600,
            color: AppTheme.gray700,
          ),
        ),
        const Spacer(),
        IconButton(
          icon: Icon(LucideIcons.x, size: 18, color: AppTheme.gray400),
          onPressed: () => Navigator.of(context).pop(),
          padding: EdgeInsets.zero,
          constraints: const BoxConstraints(minWidth: 32, minHeight: 32),
        ),
      ],
    );
  }

  Widget _buildTitleField(BuildContext context) {
    return TextFormField(
      controller: _titleController,
      autofocus: true,
      style: const TextStyle(fontSize: 16),
      onFieldSubmitted: (_) => _submitIfValid(),
      decoration: InputDecoration(
        hintText: 'What needs to be done?',
        hintStyle: TextStyle(color: AppTheme.gray400, fontSize: 16),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
          borderSide: BorderSide(color: AppTheme.gray200),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
          borderSide: BorderSide(color: AppTheme.gray200),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
          borderSide: BorderSide(color: AppTheme.primaryBlue, width: 1.5),
        ),
        contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
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
    if (!_showDescription) {
      return GestureDetector(
        onTap: () => setState(() => _showDescription = true),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
          decoration: BoxDecoration(
            color: AppTheme.gray50,
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: AppTheme.gray200),
          ),
          child: Row(
            children: [
              Icon(LucideIcons.plus, size: 14, color: AppTheme.gray400),
              const SizedBox(width: 8),
              Text(
                'Add description',
                style: TextStyle(color: AppTheme.gray400, fontSize: 14),
              ),
            ],
          ),
        ),
      );
    }

    return TextFormField(
      controller: _descriptionController,
      maxLines: 3,
      autofocus: true,
      style: const TextStyle(fontSize: 14),
      decoration: InputDecoration(
        hintText: 'Add more details...',
        hintStyle: TextStyle(color: AppTheme.gray400, fontSize: 14),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
          borderSide: BorderSide(color: AppTheme.gray200),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
          borderSide: BorderSide(color: AppTheme.gray200),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(8),
          borderSide: BorderSide(color: AppTheme.primaryBlue, width: 1.5),
        ),
        contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      ),
    );
  }

  Widget _buildMetadataToolbar(BuildContext context) {
    return Wrap(
      spacing: 8,
      runSpacing: 8,
      children: [
        _buildProjectPill(context),
        _buildPriorityPill(context),
        _buildDatePill(context),
        if (widget.labels.isNotEmpty) _buildLabelsPill(context),
      ],
    );
  }

  Widget _buildProjectPill(BuildContext context) {
    final selectedProject = _selectedProjectId != null
        ? widget.projects.where((p) => p.id == _selectedProjectId).firstOrNull
        : null;
    final displayName = selectedProject?.name ?? 'Inbox';
    final color = selectedProject != null
        ? _parseColor(selectedProject.color)
        : AppTheme.gray500;

    return PopupMenuButton<String?>(
      onSelected: (value) => setState(() => _selectedProjectId = value),
      tooltip: 'Select project',
      offset: const Offset(0, 36),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
      itemBuilder: (context) => [
        PopupMenuItem(
          value: null,
          child: Row(
            children: [
              Icon(LucideIcons.inbox, size: 14, color: AppTheme.gray500),
              const SizedBox(width: 8),
              const Text('Inbox'),
            ],
          ),
        ),
        ...widget.projects.map((p) => PopupMenuItem(
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
      child: _buildPillContainer(
        icon: LucideIcons.folder,
        label: displayName,
        color: color,
        isActive: selectedProject != null,
      ),
    );
  }

  Widget _buildPriorityPill(BuildContext context) {
    final priorityLabels = {1: 'High', 2: 'Medium', 3: 'Normal', 4: 'Low'};
    final color = AppTheme.priorityColors[_priority] ?? AppTheme.gray500;

    return PopupMenuButton<int>(
      onSelected: (value) => setState(() => _priority = value),
      tooltip: 'Set priority',
      offset: const Offset(0, 36),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
      itemBuilder: (context) => [1, 2, 3, 4]
          .map((p) => PopupMenuItem(
                value: p,
                child: Row(
                  children: [
                    Container(
                      width: 10,
                      height: 10,
                      decoration: BoxDecoration(
                        color: AppTheme.priorityColors[p],
                        shape: BoxShape.circle,
                      ),
                    ),
                    const SizedBox(width: 8),
                    Text(priorityLabels[p]!),
                  ],
                ),
              ))
          .toList(),
      child: _buildPillContainer(
        icon: LucideIcons.flag,
        label: priorityLabels[_priority]!,
        color: color,
        isActive: _priority != 4,
      ),
    );
  }

  Widget _buildDatePill(BuildContext context) {
    return GestureDetector(
      onTap: _selectDueDate,
      child: _buildPillContainer(
        icon: LucideIcons.calendar,
        label: _dueDate != null ? _formatDate(_dueDate!) : 'No date',
        color: _dueDate != null ? AppTheme.primaryBlue : AppTheme.gray500,
        isActive: _dueDate != null,
        trailing: _dueDate != null
            ? GestureDetector(
                onTap: () => setState(() => _dueDate = null),
                child: Icon(LucideIcons.x, size: 12, color: AppTheme.gray400),
              )
            : null,
      ),
    );
  }

  Widget _buildLabelsPill(BuildContext context) {
    final hasLabels = _selectedLabels.isNotEmpty;

    return PopupMenuButton<LabelEntity>(
      onSelected: (label) {
        setState(() {
          if (_selectedLabels.contains(label)) {
            _selectedLabels.remove(label);
          } else {
            _selectedLabels.add(label);
          }
        });
      },
      tooltip: 'Add labels',
      offset: const Offset(0, 36),
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
      itemBuilder: (context) => widget.labels
          .map((l) => PopupMenuItem(
                value: l,
                child: Row(
                  children: [
                    Container(
                      width: 10,
                      height: 10,
                      decoration: BoxDecoration(
                        color: _parseColor(l.color),
                        shape: BoxShape.circle,
                      ),
                    ),
                    const SizedBox(width: 8),
                    Expanded(child: Text(l.name)),
                    if (_selectedLabels.contains(l))
                      Icon(LucideIcons.check, size: 14, color: AppTheme.primaryBlue),
                  ],
                ),
              ))
          .toList(),
      child: _buildPillContainer(
        icon: LucideIcons.tag,
        label: hasLabels ? '${_selectedLabels.length} label${_selectedLabels.length > 1 ? 's' : ''}' : 'Labels',
        color: hasLabels ? AppTheme.accentPink : AppTheme.gray500,
        isActive: hasLabels,
      ),
    );
  }

  Widget _buildPillContainer({
    required IconData icon,
    required String label,
    required Color color,
    bool isActive = false,
    Widget? trailing,
  }) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
      decoration: BoxDecoration(
        color: isActive ? color.withValues(alpha: 0.1) : AppTheme.gray50,
        borderRadius: BorderRadius.circular(6),
        border: Border.all(
          color: isActive ? color.withValues(alpha: 0.3) : AppTheme.gray200,
        ),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 14, color: isActive ? color : AppTheme.gray400),
          const SizedBox(width: 6),
          Text(
            label,
            style: TextStyle(
              fontSize: 13,
              color: isActive ? color : AppTheme.gray600,
              fontWeight: isActive ? FontWeight.w500 : FontWeight.normal,
            ),
          ),
          if (trailing != null) ...[
            const SizedBox(width: 4),
            trailing,
          ],
        ],
      ),
    );
  }

  Widget _buildFooter(BuildContext context) {
    return Row(
      mainAxisAlignment: MainAxisAlignment.end,
      children: [
        Text(
          'Press Enter to add',
          style: TextStyle(fontSize: 12, color: AppTheme.gray400),
        ),
        const SizedBox(width: 16),
        FilledButton.icon(
          onPressed: _createTask,
          icon: const Icon(LucideIcons.plus, size: 16),
          label: const Text('Add Task'),
          style: FilledButton.styleFrom(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
          ),
        ),
      ],
    );
  }

  Future<void> _selectDueDate() async {
    final date = await showDatePicker(
      context: context,
      initialDate: _dueDate ?? DateTime.now(),
      firstDate: DateTime.now().subtract(const Duration(days: 365)),
      lastDate: DateTime.now().add(const Duration(days: 365 * 2)),
    );
    if (date != null && mounted) {
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
    final projectId = _selectedProjectId ?? 'proj_inbox';

    final task = TaskEntity(
      id: const Uuid().v4(),
      integrationId: 'openza_tasks',
      title: _titleController.text.trim(),
      description: _descriptionController.text.trim().isNotEmpty
          ? _descriptionController.text.trim()
          : null,
      priority: _priority,
      status: TaskStatus.pending,
      projectId: projectId,
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
