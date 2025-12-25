import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../core/utils/date_utils.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import '../../../domain/entities/label.dart';
import '../../providers/task_provider.dart';
import '../badges/priority_badge.dart';
import '../badges/label_badge.dart';
import '../badges/project_badge.dart';

/// Detail view for a task - can be used as a modal or sidebar
class TaskDetail extends ConsumerStatefulWidget {
  final TaskEntity task;
  final ProjectEntity? project;
  final List<ProjectEntity> projects;
  final VoidCallback? onClose;
  final void Function(TaskEntity)? onUpdate;
  final void Function(TaskEntity)? onDelete;
  final void Function(TaskEntity)? onComplete;

  const TaskDetail({
    super.key,
    required this.task,
    this.project,
    this.projects = const [],
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
  late String? _editProjectId;
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
    _editProjectId = widget.task.projectId;

    _titleController.addListener(_onEditChanged);
    _descriptionController.addListener(_onEditChanged);
  }

  @override
  void didUpdateWidget(covariant TaskDetail oldWidget) {
    super.didUpdateWidget(oldWidget);
    // When a different task is selected, reset the state
    if (oldWidget.task.id != widget.task.id) {
      _titleController.text = widget.task.title;
      _descriptionController.text = widget.task.description ?? '';
      setState(() {
        _editPriority = widget.task.priority;
        _editDueDate = widget.task.dueDate;
        _editLabelNames = widget.task.labels.map((l) => l.name).toList();
        _editProjectId = widget.task.projectId;
        _isEditing = false;
        _hasUnsavedChanges = false;
      });
    }
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
                  const SizedBox(height: 16),
                  _buildMetadataCard(context),
                  if (widget.task.labels.isNotEmpty || _isEditing) ...[
                    const SizedBox(height: 16),
                    _buildLabelsSection(context),
                  ],
                  const SizedBox(height: 16),
                  _buildDatesSection(context),
                  if (!widget.task.isNative) ...[
                    const SizedBox(height: 16),
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
      crossAxisAlignment: CrossAxisAlignment.center,
      children: [
        _buildCheckbox(),
        const SizedBox(width: 12),
        Expanded(
          child: _isEditing
              ? Container(
                  padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                  decoration: BoxDecoration(
                    color: Colors.white,
                    border: Border.all(color: AppTheme.gray300),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: TextField(
                    controller: _titleController,
                    style: Theme.of(context).textTheme.titleMedium?.copyWith(
                          fontWeight: FontWeight.w600,
                        ),
                    decoration: const InputDecoration(
                      border: InputBorder.none,
                      hintText: 'Task title',
                      isDense: true,
                      contentPadding: EdgeInsets.zero,
                    ),
                    maxLines: null,
                  ),
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
    final hasDescription = widget.task.description?.isNotEmpty == true;

    // Hide empty description section when not editing
    if (!_isEditing && !hasDescription) {
      return const SizedBox.shrink();
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Icon(LucideIcons.alignLeft, size: 16, color: AppTheme.gray400),
            const SizedBox(width: 8),
            Text(
              'Description',
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w500,
                color: AppTheme.gray500,
              ),
            ),
          ],
        ),
        const SizedBox(height: 8),
        _isEditing
            ? Container(
                decoration: BoxDecoration(
                  color: Colors.white,
                  border: Border.all(color: AppTheme.gray300),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: TextField(
                  controller: _descriptionController,
                  style: Theme.of(context).textTheme.bodyMedium,
                  decoration: InputDecoration(
                    border: InputBorder.none,
                    hintText: 'Add a description...',
                    hintStyle: TextStyle(color: AppTheme.gray400),
                    contentPadding: const EdgeInsets.all(12),
                  ),
                  maxLines: 3,
                ),
              )
            : Container(
                width: double.infinity,
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: AppTheme.gray50,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  widget.task.description!,
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: AppTheme.gray700,
                      ),
                ),
              ),
      ],
    );
  }

  Widget _buildMetadataCard(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: AppTheme.gray50,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: AppTheme.gray200),
      ),
      child: _buildMetadataSection(context),
    );
  }

  Widget _buildMetadataSection(BuildContext context) {
    // Get current project for display
    // When editing and projects list is available, look up by editProjectId
    // Otherwise fall back to the passed project prop
    final currentProject = _isEditing && widget.projects.isNotEmpty && _editProjectId != null
        ? widget.projects.where((p) => p.id == _editProjectId).firstOrNull ?? widget.project
        : widget.project;

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
                  showAlways: true,
                ),
        ),
        const SizedBox(height: 10),

        // Due Date
        _buildMetadataRow(
          context,
          icon: LucideIcons.calendar,
          label: 'Due Date',
          child: _isEditing ? _buildDueDatePicker(context) : _buildDueDateDisplay(),
        ),
        const SizedBox(height: 10),

        // Project
        _buildMetadataRow(
          context,
          icon: LucideIcons.folder,
          label: 'Project',
          child: _isEditing
              ? _buildProjectSelector()
              : currentProject != null
                  ? ProjectBadge(project: currentProject)
                  : Text(
                      'No project',
                      style: TextStyle(fontSize: 13, color: AppTheme.gray400),
                    ),
        ),
      ],
    );
  }

  Widget _buildProjectSelector() {
    // Filter projects by task's integration
    final filteredProjects = widget.projects
        .where((p) => p.integrationId == widget.task.integrationId)
        .toList();

    // Validate that current project exists in filtered list to prevent DropdownButton crash
    final isValidProject = _editProjectId == null ||
        filteredProjects.any((p) => p.id == _editProjectId);
    final effectiveValue = isValidProject ? _editProjectId : null;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 2),
      decoration: BoxDecoration(
        color: Colors.white,
        border: Border.all(color: AppTheme.gray300),
        borderRadius: BorderRadius.circular(8),
      ),
      child: DropdownButton<String?>(
        value: effectiveValue,
        hint: Text('Select project', style: TextStyle(color: AppTheme.gray400, fontSize: 13)),
        isExpanded: true,
        items: filteredProjects.map((project) {
          return DropdownMenuItem<String?>(
            value: project.id,
            child: Row(
              children: [
                Container(
                  width: 10,
                  height: 10,
                  decoration: BoxDecoration(
                    color: _parseProjectColor(project.color),
                    borderRadius: BorderRadius.circular(2),
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    project.name,
                    style: const TextStyle(fontSize: 13),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              ],
            ),
          );
        }).toList(),
        onChanged: (value) {
          setState(() {
            _editProjectId = value;
            _hasUnsavedChanges = true;
          });
        },
        underline: const SizedBox.shrink(),
        isDense: true,
        icon: Icon(LucideIcons.chevronDown, size: 16, color: AppTheme.gray400),
      ),
    );
  }

  Color _parseProjectColor(String colorStr) {
    if (colorStr.startsWith('#')) {
      try {
        return Color(int.parse(colorStr.substring(1), radix: 16) + 0xFF000000);
      } catch (_) {
        return AppTheme.gray500;
      }
    }
    return AppTheme.gray500;
  }

  Widget _buildPrioritySelector() {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 2),
      decoration: BoxDecoration(
        color: Colors.white,
        border: Border.all(color: AppTheme.gray300),
        borderRadius: BorderRadius.circular(8),
      ),
      child: DropdownButton<int>(
        value: _editPriority,
        isExpanded: true,
        items: [
          DropdownMenuItem(
            value: 1,
            child: _buildPriorityOption('Urgent', AppTheme.priorityColors[1]!),
          ),
          DropdownMenuItem(
            value: 2,
            child: _buildPriorityOption('High', AppTheme.priorityColors[2]!),
          ),
          DropdownMenuItem(
            value: 3,
            child: _buildPriorityOption('Normal', AppTheme.priorityColors[3]!),
          ),
          DropdownMenuItem(
            value: 4,
            child: _buildPriorityOption('Low', AppTheme.priorityColors[4]!),
          ),
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
        icon: Icon(LucideIcons.chevronDown, size: 16, color: AppTheme.gray400),
      ),
    );
  }

  Widget _buildPriorityOption(String label, Color color) {
    return Row(
      children: [
        Container(
          width: 8,
          height: 8,
          decoration: BoxDecoration(
            color: color,
            shape: BoxShape.circle,
          ),
        ),
        const SizedBox(width: 8),
        Text(label, style: const TextStyle(fontSize: 13)),
      ],
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
    return InkWell(
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
      borderRadius: BorderRadius.circular(8),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
        decoration: BoxDecoration(
          color: Colors.white,
          border: Border.all(color: AppTheme.gray300),
          borderRadius: BorderRadius.circular(8),
        ),
        child: Row(
          children: [
            Icon(LucideIcons.calendar, size: 14, color: AppTheme.gray400),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                _editDueDate != null
                    ? AppDateUtils.formatForDisplay(_editDueDate!)
                    : 'Select date',
                style: TextStyle(
                  fontSize: 13,
                  color: _editDueDate != null ? AppTheme.gray700 : AppTheme.gray400,
                ),
              ),
            ),
            if (_editDueDate != null)
              GestureDetector(
                onTap: () => setState(() {
                  _editDueDate = null;
                  _hasUnsavedChanges = true;
                }),
                child: Icon(LucideIcons.x, size: 14, color: AppTheme.gray400),
              ),
          ],
        ),
      ),
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
        const SizedBox(width: 10),
        SizedBox(
          width: 80,
          child: Text(
            label,
            style: TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w500,
              color: AppTheme.gray600,
            ),
          ),
        ),
        Expanded(child: child),
      ],
    );
  }

  Widget _buildLabelsSection(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: AppTheme.gray50,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: AppTheme.gray200),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(LucideIcons.tag, size: 14, color: AppTheme.gray400),
              const SizedBox(width: 6),
              Text(
                'Labels',
                style: TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w500,
                  color: AppTheme.gray500,
                ),
              ),
            ],
          ),
          const SizedBox(height: 10),
          _isEditing ? _buildLabelsEditor(context) : _buildLabelsDisplay(),
        ],
      ),
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
    final labelsAsync = ref.read(localLabelsProvider);

    showDialog(
      context: context,
      builder: (dialogContext) {
        final newLabelController = TextEditingController();
        bool showNewLabelField = false;

        return StatefulBuilder(
          builder: (context, setDialogState) {
            return AlertDialog(
              title: Row(
                children: [
                  Icon(LucideIcons.tag, size: 20, color: AppTheme.primaryBlue),
                  const SizedBox(width: 8),
                  const Text('Add Label'),
                ],
              ),
              content: SizedBox(
                width: 300,
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    // Existing labels
                    labelsAsync.when(
                      data: (labels) {
                        // Filter labels by task's integration and exclude already added
                        final availableLabels = labels
                            .where((l) =>
                                l.integrationId == widget.task.integrationId &&
                                !_editLabelNames.contains(l.name))
                            .toList();

                        if (availableLabels.isEmpty && !showNewLabelField) {
                          return Padding(
                            padding: const EdgeInsets.only(bottom: 12),
                            child: Text(
                              'No existing labels available',
                              style: TextStyle(
                                color: AppTheme.gray500,
                                fontSize: 13,
                              ),
                            ),
                          );
                        }

                        return Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            if (availableLabels.isNotEmpty) ...[
                              Text(
                                'Select existing label',
                                style: TextStyle(
                                  fontSize: 12,
                                  fontWeight: FontWeight.w500,
                                  color: AppTheme.gray500,
                                ),
                              ),
                              const SizedBox(height: 8),
                              Wrap(
                                spacing: 8,
                                runSpacing: 8,
                                children: availableLabels.map((label) {
                                  return InkWell(
                                    onTap: () {
                                      setState(() {
                                        _editLabelNames.add(label.name);
                                        _hasUnsavedChanges = true;
                                      });
                                      Navigator.pop(dialogContext);
                                    },
                                    borderRadius: BorderRadius.circular(16),
                                    child: Container(
                                      padding: const EdgeInsets.symmetric(
                                        horizontal: 12,
                                        vertical: 6,
                                      ),
                                      decoration: BoxDecoration(
                                        color: _parseProjectColor(label.color)
                                            .withValues(alpha: 0.1),
                                        borderRadius: BorderRadius.circular(16),
                                        border: Border.all(
                                          color: _parseProjectColor(label.color)
                                              .withValues(alpha: 0.3),
                                        ),
                                      ),
                                      child: Text(
                                        label.name,
                                        style: TextStyle(
                                          fontSize: 13,
                                          color: _parseProjectColor(label.color),
                                          fontWeight: FontWeight.w500,
                                        ),
                                      ),
                                    ),
                                  );
                                }).toList(),
                              ),
                              const SizedBox(height: 16),
                            ],
                          ],
                        );
                      },
                      loading: () => const Padding(
                        padding: EdgeInsets.all(16),
                        child: Center(
                          child: SizedBox(
                            width: 20,
                            height: 20,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          ),
                        ),
                      ),
                      error: (_, _) => const SizedBox.shrink(),
                    ),

                    // Create new label section
                    if (showNewLabelField) ...[
                      Text(
                        'Create new label',
                        style: TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w500,
                          color: AppTheme.gray500,
                        ),
                      ),
                      const SizedBox(height: 8),
                      TextField(
                        controller: newLabelController,
                        autofocus: true,
                        decoration: InputDecoration(
                          hintText: 'Label name',
                          hintStyle: TextStyle(color: AppTheme.gray400),
                          border: OutlineInputBorder(
                            borderRadius: BorderRadius.circular(8),
                          ),
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 12,
                            vertical: 10,
                          ),
                        ),
                        onSubmitted: (value) {
                          if (value.trim().isNotEmpty) {
                            setState(() {
                              _editLabelNames.add(value.trim());
                              _hasUnsavedChanges = true;
                            });
                            Navigator.pop(dialogContext);
                          }
                        },
                      ),
                    ] else ...[
                      // Button to show new label field
                      InkWell(
                        onTap: () => setDialogState(() => showNewLabelField = true),
                        borderRadius: BorderRadius.circular(8),
                        child: Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 12,
                            vertical: 10,
                          ),
                          decoration: BoxDecoration(
                            border: Border.all(color: AppTheme.gray300),
                            borderRadius: BorderRadius.circular(8),
                          ),
                          child: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              Icon(LucideIcons.plus, size: 16, color: AppTheme.gray500),
                              const SizedBox(width: 6),
                              Text(
                                'Create new label',
                                style: TextStyle(
                                  fontSize: 13,
                                  color: AppTheme.gray600,
                                ),
                              ),
                            ],
                          ),
                        ),
                      ),
                    ],
                  ],
                ),
              ),
              actions: [
                TextButton(
                  onPressed: () => Navigator.pop(dialogContext),
                  child: const Text('Cancel'),
                ),
                if (showNewLabelField)
                  FilledButton(
                    onPressed: () {
                      final value = newLabelController.text.trim();
                      if (value.isNotEmpty) {
                        setState(() {
                          _editLabelNames.add(value);
                          _hasUnsavedChanges = true;
                        });
                        Navigator.pop(dialogContext);
                      }
                    },
                    child: const Text('Add'),
                  ),
              ],
            );
          },
        );
      },
    );
  }

  Widget _buildDatesSection(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: AppTheme.gray50,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: AppTheme.gray200),
      ),
      child: Column(
        children: [
          _buildDateRow(
            context,
            label: 'Created',
            date: widget.task.createdAt,
          ),
          if (widget.task.updatedAt != null) ...[
            const SizedBox(height: 6),
            _buildDateRow(
              context,
              label: 'Updated',
              date: widget.task.updatedAt!,
            ),
          ],
          if (widget.task.completedAt != null) ...[
            const SizedBox(height: 6),
            _buildDateRow(
              context,
              label: 'Completed',
              date: widget.task.completedAt!,
            ),
          ],
        ],
      ),
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
                onPressed: () => _confirmDelete(context),
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
      _editProjectId = widget.task.projectId;
      _isEditing = false;
      _hasUnsavedChanges = false;
    });
  }

  void _confirmDelete(BuildContext context) {
    showDialog(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Row(
          children: [
            Icon(LucideIcons.alertTriangle, size: 20, color: AppTheme.errorRed),
            const SizedBox(width: 8),
            const Text('Delete Task'),
          ],
        ),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text('Are you sure you want to delete this task?'),
            const SizedBox(height: 12),
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: AppTheme.gray100,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Text(
                widget.task.title,
                style: const TextStyle(fontWeight: FontWeight.w500),
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
              ),
            ),
            const SizedBox(height: 12),
            Text(
              'This action cannot be undone.',
              style: TextStyle(fontSize: 13, color: AppTheme.gray500),
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () {
              Navigator.pop(dialogContext);
              widget.onDelete?.call(widget.task);
            },
            style: FilledButton.styleFrom(
              backgroundColor: AppTheme.errorRed,
            ),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
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
      projectId: _editProjectId,
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
