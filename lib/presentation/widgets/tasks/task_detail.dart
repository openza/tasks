import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lucide_icons/lucide_icons.dart';

import '../../../app/app_theme.dart';
import '../../../core/utils/date_utils.dart';
import '../../../domain/entities/task.dart';
import '../../../domain/entities/project.dart';
import '../../../domain/entities/label.dart';
import '../../providers/task_provider.dart';
import '../badges/label_badge.dart';

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
    _descriptionController = TextEditingController(
      text: widget.task.description ?? '',
    );
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
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final borderColor = isDark ? AppTheme.gray700 : AppTheme.gray200;

    return Container(
      width: 400,
      decoration: BoxDecoration(
        color: Theme.of(context).colorScheme.surface,
        border: Border(left: BorderSide(color: borderColor)),
      ),
      child: Column(
        children: [
          _buildHeader(context),
          Expanded(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(20),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  _buildTitleSection(context),
                  const SizedBox(height: 20),
                  _buildDescriptionSection(context),
                  const SizedBox(height: 20),
                  _buildMetadataCard(context),
                  if (widget.task.labels.isNotEmpty || _isEditing) ...[
                    const SizedBox(height: 16),
                    _buildLabelsSection(context),
                  ],
                  const SizedBox(height: 16),
                  _buildDatesSection(context),
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
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final borderColor = isDark ? AppTheme.gray700 : AppTheme.gray200;
    final iconColor = isDark ? AppTheme.gray400 : AppTheme.gray500;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
      decoration: BoxDecoration(
        border: Border(bottom: BorderSide(color: borderColor)),
      ),
      child: Row(
        children: [
          Text(
            'Task Details',
            style: Theme.of(
              context,
            ).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w600),
          ),
          // Provider indicator (inline, subtle)
          if (!widget.task.isNative) ...[
            const SizedBox(width: 10),
            _buildInlineProviderBadge(),
          ],
          const Spacer(),
          if (!_isEditing)
            IconButton(
              icon: Icon(LucideIcons.pencil, size: 18, color: iconColor),
              onPressed: () => setState(() => _isEditing = true),
              tooltip: 'Edit',
            ),
          IconButton(
            icon: Icon(LucideIcons.x, size: 18, color: iconColor),
            onPressed: widget.onClose ?? () {},
            tooltip: 'Close',
          ),
        ],
      ),
    );
  }

  Widget _buildInlineProviderBadge() {
    Color providerColor;
    String label;

    switch (widget.task.integrationId) {
      case 'todoist':
        providerColor = const Color(0xFFE44332);
        label = 'Todoist';
        break;
      case 'msToDo':
        providerColor = const Color(0xFF00A4EF);
        label = 'MS To-Do';
        break;
      default:
        return const SizedBox.shrink();
    }

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
      decoration: BoxDecoration(
        color: providerColor.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(4),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Container(
            width: 6,
            height: 6,
            decoration: BoxDecoration(
              color: providerColor,
              shape: BoxShape.circle,
            ),
          ),
          const SizedBox(width: 4),
          Text(
            label,
            style: TextStyle(
              fontSize: 10,
              color: providerColor,
              fontWeight: FontWeight.w500,
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildTitleSection(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final titleColor =
        widget.task.isCompleted
            ? AppTheme.gray400
            : isDark
            ? AppTheme.gray100
            : AppTheme.gray900;
    final inputBg = isDark ? AppTheme.gray800 : Colors.white;
    final inputBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Row(
      crossAxisAlignment: CrossAxisAlignment.center,
      children: [
        _buildCheckbox(),
        const SizedBox(width: 14),
        Expanded(
          child:
              _isEditing
                  ? Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 12,
                      vertical: 10,
                    ),
                    decoration: BoxDecoration(
                      color: inputBg,
                      border: Border.all(color: inputBorder),
                      borderRadius: BorderRadius.circular(8),
                    ),
                    child: TextField(
                      controller: _titleController,
                      style: TextStyle(
                        fontSize: 18,
                        fontWeight: FontWeight.w600,
                        color: titleColor,
                      ),
                      decoration: InputDecoration(
                        border: InputBorder.none,
                        hintText: 'Task title',
                        hintStyle: TextStyle(color: AppTheme.gray400),
                        isDense: true,
                        contentPadding: EdgeInsets.zero,
                      ),
                      maxLines: null,
                    ),
                  )
                  : Text(
                    widget.task.title,
                    style: TextStyle(
                      fontSize: 18,
                      fontWeight: FontWeight.w600,
                      decoration:
                          widget.task.isCompleted
                              ? TextDecoration.lineThrough
                              : null,
                      color: titleColor,
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
                widget.task.isCompleted
                    ? AppTheme.accentPink
                    : AppTheme.gray400,
            width: 2,
          ),
          color:
              widget.task.isCompleted
                  ? AppTheme.accentPink
                  : Colors.transparent,
        ),
        child:
            widget.task.isCompleted
                ? const Icon(Icons.check, size: 16, color: Colors.white)
                : null,
      ),
    );
  }

  Widget _buildDescriptionSection(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final hasDescription = widget.task.description?.isNotEmpty == true;

    // Hide empty description section when not editing
    if (!_isEditing && !hasDescription) {
      return const SizedBox.shrink();
    }

    final inputBg = isDark ? AppTheme.gray800 : Colors.white;
    final inputBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;
    final descBg = isDark ? AppTheme.gray800 : AppTheme.gray100;
    final descTextColor = isDark ? AppTheme.gray300 : AppTheme.gray700;

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
                color: inputBg,
                border: Border.all(color: inputBorder),
                borderRadius: BorderRadius.circular(8),
              ),
              child: TextField(
                controller: _descriptionController,
                style: Theme.of(
                  context,
                ).textTheme.bodyMedium?.copyWith(color: descTextColor),
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
                color: descBg,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Text(
                widget.task.description!,
                style: Theme.of(
                  context,
                ).textTheme.bodyMedium?.copyWith(color: descTextColor),
              ),
            ),
      ],
    );
  }

  Widget _buildMetadataCard(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final cardBg = isDark ? AppTheme.gray800 : AppTheme.gray100;
    final borderColor = isDark ? AppTheme.gray700 : AppTheme.gray200;

    // When editing, use the form-like layout for better UX
    if (_isEditing) {
      return Container(
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: cardBg,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: borderColor),
        ),
        child: _buildMetadataEditSection(context),
      );
    }

    // When viewing, use inline chips layout for natural feel
    return _buildMetadataChipsSection(context);
  }

  /// Inline chips layout for viewing mode - natural, card-like feel
  Widget _buildMetadataChipsSection(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final chipColor = isDark ? AppTheme.gray400 : AppTheme.gray600;

    // Muted red for overdue, consistent with task_card.dart
    const overdueColor = Color(0xFFB85C5C);

    return Wrap(
      spacing: 8,
      runSpacing: 8,
      children: [
        // Priority chip
        _buildMetadataChip(
          icon: LucideIcons.flag,
          label: _getPriorityLabel(widget.task.priority),
          color: chipColor,
        ),

        // Due date chip
        if (widget.task.dueDate != null)
          _buildMetadataChip(
            icon: LucideIcons.calendar,
            label: AppDateUtils.formatForDisplay(widget.task.dueDate!),
            color:
                widget.task.isOverdue && !widget.task.isCompleted
                    ? overdueColor
                    : chipColor,
          ),

        // Project chip
        if (widget.project != null)
          _buildMetadataChip(
            icon: LucideIcons.folder,
            label: widget.project!.name,
            color: chipColor,
          ),
      ],
    );
  }

  Widget _buildMetadataChip({
    required IconData icon,
    required String label,
    required Color color,
  }) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(16),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 14, color: color),
          const SizedBox(width: 6),
          Text(
            label,
            style: TextStyle(
              fontSize: 12,
              color: color,
              fontWeight: FontWeight.w500,
            ),
          ),
        ],
      ),
    );
  }

  String _getPriorityLabel(int priority) {
    switch (priority) {
      case 1:
        return 'Urgent';
      case 2:
        return 'High';
      case 3:
        return 'Normal';
      case 4:
      default:
        return 'Low';
    }
  }

  /// Form-like layout for editing mode
  Widget _buildMetadataEditSection(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // Priority
        _buildMetadataRow(
          context,
          icon: LucideIcons.flag,
          label: 'Priority',
          child: _buildPrioritySelector(),
        ),
        const SizedBox(height: 10),

        // Due Date
        _buildMetadataRow(
          context,
          icon: LucideIcons.calendar,
          label: 'Due Date',
          child: _buildDueDatePicker(context),
        ),
        const SizedBox(height: 10),

        // Project
        _buildMetadataRow(
          context,
          icon: LucideIcons.folder,
          label: 'Project',
          child: _buildProjectSelector(),
        ),
      ],
    );
  }

  Widget _buildProjectSelector() {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final inputBg = isDark ? AppTheme.gray800 : Colors.white;
    final inputBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    // Filter projects by task's integration
    final filteredProjects =
        widget.projects
            .where((p) => p.integrationId == widget.task.integrationId)
            .toList();

    // Validate that current project exists in filtered list to prevent DropdownButton crash
    final isValidProject =
        _editProjectId == null ||
        filteredProjects.any((p) => p.id == _editProjectId);
    final effectiveValue = isValidProject ? _editProjectId : null;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 2),
      decoration: BoxDecoration(
        color: inputBg,
        border: Border.all(color: inputBorder),
        borderRadius: BorderRadius.circular(8),
      ),
      child: DropdownButton<String?>(
        value: effectiveValue,
        hint: Text(
          'Select project',
          style: TextStyle(color: AppTheme.gray400, fontSize: 13),
        ),
        isExpanded: true,
        dropdownColor: inputBg,
        items:
            filteredProjects.map((project) {
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
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final inputBg = isDark ? AppTheme.gray800 : Colors.white;
    final inputBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 2),
      decoration: BoxDecoration(
        color: inputBg,
        border: Border.all(color: inputBorder),
        borderRadius: BorderRadius.circular(8),
      ),
      child: DropdownButton<int>(
        value: _editPriority,
        isExpanded: true,
        dropdownColor: inputBg,
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
          decoration: BoxDecoration(color: color, shape: BoxShape.circle),
        ),
        const SizedBox(width: 8),
        Text(label, style: const TextStyle(fontSize: 13)),
      ],
    );
  }

  Widget _buildDueDatePicker(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final inputBg = isDark ? AppTheme.gray800 : Colors.white;
    final inputBorder = isDark ? AppTheme.gray600 : AppTheme.gray300;
    final textColor = isDark ? AppTheme.gray300 : AppTheme.gray700;

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
          color: inputBg,
          border: Border.all(color: inputBorder),
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
                  color: _editDueDate != null ? textColor : AppTheme.gray400,
                ),
              ),
            ),
            if (_editDueDate != null)
              GestureDetector(
                onTap:
                    () => setState(() {
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
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final cardBg = isDark ? AppTheme.gray800 : AppTheme.gray100;
    final borderColor = isDark ? AppTheme.gray700 : AppTheme.gray200;

    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: cardBg,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: borderColor),
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
      children:
          widget.task.labels.map((label) => LabelBadge(label: label)).toList(),
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
            ..._editLabelNames.map(
              (labelName) => _buildEditableLabel(labelName),
            ),
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
              style: TextStyle(fontSize: 12, color: AppTheme.gray500),
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
                        final availableLabels =
                            labels
                                .where(
                                  (l) =>
                                      l.integrationId ==
                                          widget.task.integrationId &&
                                      !_editLabelNames.contains(l.name),
                                )
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
                                children:
                                    availableLabels.map((label) {
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
                                            color: _parseProjectColor(
                                              label.color,
                                            ).withValues(alpha: 0.1),
                                            borderRadius: BorderRadius.circular(
                                              16,
                                            ),
                                            border: Border.all(
                                              color: _parseProjectColor(
                                                label.color,
                                              ).withValues(alpha: 0.3),
                                            ),
                                          ),
                                          child: Text(
                                            label.name,
                                            style: TextStyle(
                                              fontSize: 13,
                                              color: _parseProjectColor(
                                                label.color,
                                              ),
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
                      loading:
                          () => const Padding(
                            padding: EdgeInsets.all(16),
                            child: Center(
                              child: SizedBox(
                                width: 20,
                                height: 20,
                                child: CircularProgressIndicator(
                                  strokeWidth: 2,
                                ),
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
                        onTap:
                            () =>
                                setDialogState(() => showNewLabelField = true),
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
                              Icon(
                                LucideIcons.plus,
                                size: 16,
                                color: AppTheme.gray500,
                              ),
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
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final textColor = isDark ? AppTheme.gray400 : AppTheme.gray500;

    // Build a compact inline display of timestamps
    final List<String> timestamps = [];
    timestamps.add(
      'Created ${AppDateUtils.formatForDisplay(widget.task.createdAt)}',
    );
    if (widget.task.updatedAt != null) {
      timestamps.add(
        'Updated ${AppDateUtils.formatForDisplay(widget.task.updatedAt!)}',
      );
    }
    if (widget.task.completedAt != null) {
      timestamps.add(
        'Completed ${AppDateUtils.formatForDisplay(widget.task.completedAt!)}',
      );
    }

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 4),
      child: Row(
        children: [
          Icon(LucideIcons.clock, size: 12, color: textColor),
          const SizedBox(width: 6),
          Expanded(
            child: Text(
              timestamps.join(' • '),
              style: TextStyle(fontSize: 11, color: textColor),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildFooter(BuildContext context) {
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final borderColor = isDark ? AppTheme.gray700 : AppTheme.gray200;

    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        border: Border(top: BorderSide(color: borderColor)),
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
                label: Text(widget.task.isCompleted ? 'Reopen' : 'Complete'),
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
      builder:
          (context) => AlertDialog(
            title: const Text('Unsaved Changes'),
            content: const Text(
              'You have unsaved changes. Do you want to discard them?',
            ),
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
      builder:
          (dialogContext) => AlertDialog(
            title: Row(
              children: [
                Icon(
                  LucideIcons.alertTriangle,
                  size: 20,
                  color: AppTheme.errorRed,
                ),
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
    final updatedLabels =
        _editLabelNames.map((name) {
          // Try to find existing label, otherwise create new one
          final existing = widget.task.labels.where((l) => l.name == name);
          if (existing.isNotEmpty) {
            return existing.first;
          }
          return LabelEntity(
            id: 'local_${DateTime.now().millisecondsSinceEpoch}_$name',
            name: name,
            color:
                '#${AppTheme.primaryBlue.toARGB32().toRadixString(16).substring(2)}',
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
