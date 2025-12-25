import 'package:flutter/material.dart';
import 'package:lucide_icons/lucide_icons.dart';
import 'package:uuid/uuid.dart';

import '../../../app/app_theme.dart';
import '../../../domain/entities/project.dart';

/// Predefined color palette for projects
const List<String> projectColors = [
  '#808080', // Gray
  '#3b82f6', // Blue
  '#10b981', // Green
  '#ef4444', // Red
  '#f59e0b', // Amber
  '#8b5cf6', // Purple
  '#ec4899', // Pink
  '#06b6d4', // Cyan
];

/// Dialog for creating or editing a local project
class ProjectDialog extends StatefulWidget {
  /// The project to edit (null for create mode)
  final ProjectEntity? project;

  /// Callback when project is saved
  final void Function(ProjectEntity project)? onSave;

  const ProjectDialog({
    super.key,
    this.project,
    this.onSave,
  });

  /// Show dialog to create a new project
  static Future<ProjectEntity?> showCreate(BuildContext context) async {
    return showDialog<ProjectEntity>(
      context: context,
      builder: (context) => ProjectDialog(
        onSave: (project) => Navigator.of(context).pop(project),
      ),
    );
  }

  /// Show dialog to edit an existing project
  static Future<ProjectEntity?> showEdit(
    BuildContext context,
    ProjectEntity project,
  ) async {
    return showDialog<ProjectEntity>(
      context: context,
      builder: (context) => ProjectDialog(
        project: project,
        onSave: (project) => Navigator.of(context).pop(project),
      ),
    );
  }

  bool get isEditMode => project != null;

  @override
  State<ProjectDialog> createState() => _ProjectDialogState();
}

class _ProjectDialogState extends State<ProjectDialog> {
  final _formKey = GlobalKey<FormState>();
  final _nameController = TextEditingController();
  late String _selectedColor;

  @override
  void initState() {
    super.initState();
    _nameController.text = widget.project?.name ?? '';
    _selectedColor = widget.project?.color ?? projectColors[1]; // Default: Blue
  }

  @override
  void dispose() {
    _nameController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final isEdit = widget.isEditMode;

    return Dialog(
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      child: Container(
        width: 400,
        constraints: const BoxConstraints(maxHeight: 400),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            _buildHeader(context, isEdit),
            Flexible(
              child: SingleChildScrollView(
                padding: const EdgeInsets.all(20),
                child: Form(
                  key: _formKey,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      _buildNameField(context),
                      const SizedBox(height: 20),
                      _buildColorPicker(context),
                    ],
                  ),
                ),
              ),
            ),
            _buildFooter(context, isEdit),
          ],
        ),
      ),
    );
  }

  Widget _buildHeader(BuildContext context, bool isEdit) {
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
              color: _parseColor(_selectedColor).withValues(alpha: 0.15),
              borderRadius: BorderRadius.circular(8),
            ),
            child: Icon(
              isEdit ? LucideIcons.pencil : LucideIcons.folderPlus,
              size: 20,
              color: _parseColor(_selectedColor),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  isEdit ? 'Edit Project' : 'Create Project',
                  style: Theme.of(context).textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                ),
                Text(
                  isEdit
                      ? 'Update project details'
                      : 'Add a new local project',
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

  Widget _buildNameField(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _buildSectionLabel(context, LucideIcons.type, 'Project Name'),
        const SizedBox(height: 8),
        TextFormField(
          controller: _nameController,
          autofocus: true,
          decoration: InputDecoration(
            hintText: 'Enter project name',
            border: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
            ),
          ),
          validator: (value) {
            if (value == null || value.trim().isEmpty) {
              return 'Please enter a project name';
            }
            if (value.trim().length > 100) {
              return 'Name must be 100 characters or less';
            }
            return null;
          },
        ),
      ],
    );
  }

  Widget _buildColorPicker(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _buildSectionLabel(context, LucideIcons.palette, 'Color'),
        const SizedBox(height: 8),
        Wrap(
          spacing: 10,
          runSpacing: 10,
          children: projectColors.map((color) {
            final isSelected = _selectedColor == color;
            return GestureDetector(
              onTap: () => setState(() => _selectedColor = color),
              child: Container(
                width: 36,
                height: 36,
                decoration: BoxDecoration(
                  color: _parseColor(color),
                  borderRadius: BorderRadius.circular(8),
                  border: Border.all(
                    color: isSelected ? AppTheme.gray900 : Colors.transparent,
                    width: 2,
                  ),
                  boxShadow: isSelected
                      ? [
                          BoxShadow(
                            color: _parseColor(color).withValues(alpha: 0.4),
                            blurRadius: 8,
                            spreadRadius: 1,
                          )
                        ]
                      : null,
                ),
                child: isSelected
                    ? const Icon(LucideIcons.check, size: 18, color: Colors.white)
                    : null,
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

  Widget _buildFooter(BuildContext context, bool isEdit) {
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
              onPressed: _saveProject,
              icon: Icon(
                isEdit ? LucideIcons.save : LucideIcons.plus,
                size: 18,
              ),
              label: Text(isEdit ? 'Save Changes' : 'Create Project'),
            ),
          ),
        ],
      ),
    );
  }

  void _saveProject() {
    if (!_formKey.currentState!.validate()) return;

    final now = DateTime.now();
    final project = widget.project;

    final savedProject = ProjectEntity(
      id: project?.id ?? 'proj_${const Uuid().v4()}',
      integrationId: 'openza_tasks',
      name: _nameController.text.trim(),
      description: project?.description,
      color: _selectedColor,
      icon: project?.icon,
      parentId: project?.parentId,
      sortOrder: project?.sortOrder ?? 0,
      isFavorite: project?.isFavorite ?? false,
      isArchived: project?.isArchived ?? false,
      createdAt: project?.createdAt ?? now,
      updatedAt: now,
    );

    widget.onSave?.call(savedProject);
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
