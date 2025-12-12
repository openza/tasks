// GENERATED CODE - DO NOT MODIFY BY HAND
// coverage:ignore-file
// ignore_for_file: type=lint
// ignore_for_file: unused_element, deprecated_member_use, deprecated_member_use_from_same_package, use_function_type_syntax_for_parameters, unnecessary_const, avoid_init_to_null, invalid_override_different_default_values_named, prefer_expression_function_bodies, annotate_overrides, invalid_annotation_target, unnecessary_question_mark

part of 'task.dart';

// **************************************************************************
// FreezedGenerator
// **************************************************************************

// dart format off
T _$identity<T>(T value) => value;

/// @nodoc
mixin _$TaskEntity {

 String get id; String get title; String? get description; String? get projectId; String? get parentId; int get priority; TaskStatus get status; DateTime? get dueDate; String? get dueTime;// Enhanced local features
 int? get estimatedDuration; int? get actualDuration; int get energyLevel; TaskContext get context; bool get focusTime; String? get notes;// External integration data
 Map<String, dynamic>? get sourceTask; Map<String, dynamic>? get integrations;// Timestamps
 DateTime get createdAt; DateTime? get updatedAt; DateTime? get completedAt;// Joined data (populated from relations)
 List<LabelEntity> get labels; ProjectEntity? get project;// Source provider
 TaskProvider? get provider;
/// Create a copy of TaskEntity
/// with the given fields replaced by the non-null parameter values.
@JsonKey(includeFromJson: false, includeToJson: false)
@pragma('vm:prefer-inline')
$TaskEntityCopyWith<TaskEntity> get copyWith => _$TaskEntityCopyWithImpl<TaskEntity>(this as TaskEntity, _$identity);

  /// Serializes this TaskEntity to a JSON map.
  Map<String, dynamic> toJson();


@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is TaskEntity&&(identical(other.id, id) || other.id == id)&&(identical(other.title, title) || other.title == title)&&(identical(other.description, description) || other.description == description)&&(identical(other.projectId, projectId) || other.projectId == projectId)&&(identical(other.parentId, parentId) || other.parentId == parentId)&&(identical(other.priority, priority) || other.priority == priority)&&(identical(other.status, status) || other.status == status)&&(identical(other.dueDate, dueDate) || other.dueDate == dueDate)&&(identical(other.dueTime, dueTime) || other.dueTime == dueTime)&&(identical(other.estimatedDuration, estimatedDuration) || other.estimatedDuration == estimatedDuration)&&(identical(other.actualDuration, actualDuration) || other.actualDuration == actualDuration)&&(identical(other.energyLevel, energyLevel) || other.energyLevel == energyLevel)&&(identical(other.context, context) || other.context == context)&&(identical(other.focusTime, focusTime) || other.focusTime == focusTime)&&(identical(other.notes, notes) || other.notes == notes)&&const DeepCollectionEquality().equals(other.sourceTask, sourceTask)&&const DeepCollectionEquality().equals(other.integrations, integrations)&&(identical(other.createdAt, createdAt) || other.createdAt == createdAt)&&(identical(other.updatedAt, updatedAt) || other.updatedAt == updatedAt)&&(identical(other.completedAt, completedAt) || other.completedAt == completedAt)&&const DeepCollectionEquality().equals(other.labels, labels)&&(identical(other.project, project) || other.project == project)&&(identical(other.provider, provider) || other.provider == provider));
}

@JsonKey(includeFromJson: false, includeToJson: false)
@override
int get hashCode => Object.hashAll([runtimeType,id,title,description,projectId,parentId,priority,status,dueDate,dueTime,estimatedDuration,actualDuration,energyLevel,context,focusTime,notes,const DeepCollectionEquality().hash(sourceTask),const DeepCollectionEquality().hash(integrations),createdAt,updatedAt,completedAt,const DeepCollectionEquality().hash(labels),project,provider]);

@override
String toString() {
  return 'TaskEntity(id: $id, title: $title, description: $description, projectId: $projectId, parentId: $parentId, priority: $priority, status: $status, dueDate: $dueDate, dueTime: $dueTime, estimatedDuration: $estimatedDuration, actualDuration: $actualDuration, energyLevel: $energyLevel, context: $context, focusTime: $focusTime, notes: $notes, sourceTask: $sourceTask, integrations: $integrations, createdAt: $createdAt, updatedAt: $updatedAt, completedAt: $completedAt, labels: $labels, project: $project, provider: $provider)';
}


}

/// @nodoc
abstract mixin class $TaskEntityCopyWith<$Res>  {
  factory $TaskEntityCopyWith(TaskEntity value, $Res Function(TaskEntity) _then) = _$TaskEntityCopyWithImpl;
@useResult
$Res call({
 String id, String title, String? description, String? projectId, String? parentId, int priority, TaskStatus status, DateTime? dueDate, String? dueTime, int? estimatedDuration, int? actualDuration, int energyLevel, TaskContext context, bool focusTime, String? notes, Map<String, dynamic>? sourceTask, Map<String, dynamic>? integrations, DateTime createdAt, DateTime? updatedAt, DateTime? completedAt, List<LabelEntity> labels, ProjectEntity? project, TaskProvider? provider
});


$ProjectEntityCopyWith<$Res>? get project;

}
/// @nodoc
class _$TaskEntityCopyWithImpl<$Res>
    implements $TaskEntityCopyWith<$Res> {
  _$TaskEntityCopyWithImpl(this._self, this._then);

  final TaskEntity _self;
  final $Res Function(TaskEntity) _then;

/// Create a copy of TaskEntity
/// with the given fields replaced by the non-null parameter values.
@pragma('vm:prefer-inline') @override $Res call({Object? id = null,Object? title = null,Object? description = freezed,Object? projectId = freezed,Object? parentId = freezed,Object? priority = null,Object? status = null,Object? dueDate = freezed,Object? dueTime = freezed,Object? estimatedDuration = freezed,Object? actualDuration = freezed,Object? energyLevel = null,Object? context = null,Object? focusTime = null,Object? notes = freezed,Object? sourceTask = freezed,Object? integrations = freezed,Object? createdAt = null,Object? updatedAt = freezed,Object? completedAt = freezed,Object? labels = null,Object? project = freezed,Object? provider = freezed,}) {
  return _then(_self.copyWith(
id: null == id ? _self.id : id // ignore: cast_nullable_to_non_nullable
as String,title: null == title ? _self.title : title // ignore: cast_nullable_to_non_nullable
as String,description: freezed == description ? _self.description : description // ignore: cast_nullable_to_non_nullable
as String?,projectId: freezed == projectId ? _self.projectId : projectId // ignore: cast_nullable_to_non_nullable
as String?,parentId: freezed == parentId ? _self.parentId : parentId // ignore: cast_nullable_to_non_nullable
as String?,priority: null == priority ? _self.priority : priority // ignore: cast_nullable_to_non_nullable
as int,status: null == status ? _self.status : status // ignore: cast_nullable_to_non_nullable
as TaskStatus,dueDate: freezed == dueDate ? _self.dueDate : dueDate // ignore: cast_nullable_to_non_nullable
as DateTime?,dueTime: freezed == dueTime ? _self.dueTime : dueTime // ignore: cast_nullable_to_non_nullable
as String?,estimatedDuration: freezed == estimatedDuration ? _self.estimatedDuration : estimatedDuration // ignore: cast_nullable_to_non_nullable
as int?,actualDuration: freezed == actualDuration ? _self.actualDuration : actualDuration // ignore: cast_nullable_to_non_nullable
as int?,energyLevel: null == energyLevel ? _self.energyLevel : energyLevel // ignore: cast_nullable_to_non_nullable
as int,context: null == context ? _self.context : context // ignore: cast_nullable_to_non_nullable
as TaskContext,focusTime: null == focusTime ? _self.focusTime : focusTime // ignore: cast_nullable_to_non_nullable
as bool,notes: freezed == notes ? _self.notes : notes // ignore: cast_nullable_to_non_nullable
as String?,sourceTask: freezed == sourceTask ? _self.sourceTask : sourceTask // ignore: cast_nullable_to_non_nullable
as Map<String, dynamic>?,integrations: freezed == integrations ? _self.integrations : integrations // ignore: cast_nullable_to_non_nullable
as Map<String, dynamic>?,createdAt: null == createdAt ? _self.createdAt : createdAt // ignore: cast_nullable_to_non_nullable
as DateTime,updatedAt: freezed == updatedAt ? _self.updatedAt : updatedAt // ignore: cast_nullable_to_non_nullable
as DateTime?,completedAt: freezed == completedAt ? _self.completedAt : completedAt // ignore: cast_nullable_to_non_nullable
as DateTime?,labels: null == labels ? _self.labels : labels // ignore: cast_nullable_to_non_nullable
as List<LabelEntity>,project: freezed == project ? _self.project : project // ignore: cast_nullable_to_non_nullable
as ProjectEntity?,provider: freezed == provider ? _self.provider : provider // ignore: cast_nullable_to_non_nullable
as TaskProvider?,
  ));
}
/// Create a copy of TaskEntity
/// with the given fields replaced by the non-null parameter values.
@override
@pragma('vm:prefer-inline')
$ProjectEntityCopyWith<$Res>? get project {
    if (_self.project == null) {
    return null;
  }

  return $ProjectEntityCopyWith<$Res>(_self.project!, (value) {
    return _then(_self.copyWith(project: value));
  });
}
}


/// Adds pattern-matching-related methods to [TaskEntity].
extension TaskEntityPatterns on TaskEntity {
/// A variant of `map` that fallback to returning `orElse`.
///
/// It is equivalent to doing:
/// ```dart
/// switch (sealedClass) {
///   case final Subclass value:
///     return ...;
///   case _:
///     return orElse();
/// }
/// ```

@optionalTypeArgs TResult maybeMap<TResult extends Object?>(TResult Function( _TaskEntity value)?  $default,{required TResult orElse(),}){
final _that = this;
switch (_that) {
case _TaskEntity() when $default != null:
return $default(_that);case _:
  return orElse();

}
}
/// A `switch`-like method, using callbacks.
///
/// Callbacks receives the raw object, upcasted.
/// It is equivalent to doing:
/// ```dart
/// switch (sealedClass) {
///   case final Subclass value:
///     return ...;
///   case final Subclass2 value:
///     return ...;
/// }
/// ```

@optionalTypeArgs TResult map<TResult extends Object?>(TResult Function( _TaskEntity value)  $default,){
final _that = this;
switch (_that) {
case _TaskEntity():
return $default(_that);}
}
/// A variant of `map` that fallback to returning `null`.
///
/// It is equivalent to doing:
/// ```dart
/// switch (sealedClass) {
///   case final Subclass value:
///     return ...;
///   case _:
///     return null;
/// }
/// ```

@optionalTypeArgs TResult? mapOrNull<TResult extends Object?>(TResult? Function( _TaskEntity value)?  $default,){
final _that = this;
switch (_that) {
case _TaskEntity() when $default != null:
return $default(_that);case _:
  return null;

}
}
/// A variant of `when` that fallback to an `orElse` callback.
///
/// It is equivalent to doing:
/// ```dart
/// switch (sealedClass) {
///   case Subclass(:final field):
///     return ...;
///   case _:
///     return orElse();
/// }
/// ```

@optionalTypeArgs TResult maybeWhen<TResult extends Object?>(TResult Function( String id,  String title,  String? description,  String? projectId,  String? parentId,  int priority,  TaskStatus status,  DateTime? dueDate,  String? dueTime,  int? estimatedDuration,  int? actualDuration,  int energyLevel,  TaskContext context,  bool focusTime,  String? notes,  Map<String, dynamic>? sourceTask,  Map<String, dynamic>? integrations,  DateTime createdAt,  DateTime? updatedAt,  DateTime? completedAt,  List<LabelEntity> labels,  ProjectEntity? project,  TaskProvider? provider)?  $default,{required TResult orElse(),}) {final _that = this;
switch (_that) {
case _TaskEntity() when $default != null:
return $default(_that.id,_that.title,_that.description,_that.projectId,_that.parentId,_that.priority,_that.status,_that.dueDate,_that.dueTime,_that.estimatedDuration,_that.actualDuration,_that.energyLevel,_that.context,_that.focusTime,_that.notes,_that.sourceTask,_that.integrations,_that.createdAt,_that.updatedAt,_that.completedAt,_that.labels,_that.project,_that.provider);case _:
  return orElse();

}
}
/// A `switch`-like method, using callbacks.
///
/// As opposed to `map`, this offers destructuring.
/// It is equivalent to doing:
/// ```dart
/// switch (sealedClass) {
///   case Subclass(:final field):
///     return ...;
///   case Subclass2(:final field2):
///     return ...;
/// }
/// ```

@optionalTypeArgs TResult when<TResult extends Object?>(TResult Function( String id,  String title,  String? description,  String? projectId,  String? parentId,  int priority,  TaskStatus status,  DateTime? dueDate,  String? dueTime,  int? estimatedDuration,  int? actualDuration,  int energyLevel,  TaskContext context,  bool focusTime,  String? notes,  Map<String, dynamic>? sourceTask,  Map<String, dynamic>? integrations,  DateTime createdAt,  DateTime? updatedAt,  DateTime? completedAt,  List<LabelEntity> labels,  ProjectEntity? project,  TaskProvider? provider)  $default,) {final _that = this;
switch (_that) {
case _TaskEntity():
return $default(_that.id,_that.title,_that.description,_that.projectId,_that.parentId,_that.priority,_that.status,_that.dueDate,_that.dueTime,_that.estimatedDuration,_that.actualDuration,_that.energyLevel,_that.context,_that.focusTime,_that.notes,_that.sourceTask,_that.integrations,_that.createdAt,_that.updatedAt,_that.completedAt,_that.labels,_that.project,_that.provider);}
}
/// A variant of `when` that fallback to returning `null`
///
/// It is equivalent to doing:
/// ```dart
/// switch (sealedClass) {
///   case Subclass(:final field):
///     return ...;
///   case _:
///     return null;
/// }
/// ```

@optionalTypeArgs TResult? whenOrNull<TResult extends Object?>(TResult? Function( String id,  String title,  String? description,  String? projectId,  String? parentId,  int priority,  TaskStatus status,  DateTime? dueDate,  String? dueTime,  int? estimatedDuration,  int? actualDuration,  int energyLevel,  TaskContext context,  bool focusTime,  String? notes,  Map<String, dynamic>? sourceTask,  Map<String, dynamic>? integrations,  DateTime createdAt,  DateTime? updatedAt,  DateTime? completedAt,  List<LabelEntity> labels,  ProjectEntity? project,  TaskProvider? provider)?  $default,) {final _that = this;
switch (_that) {
case _TaskEntity() when $default != null:
return $default(_that.id,_that.title,_that.description,_that.projectId,_that.parentId,_that.priority,_that.status,_that.dueDate,_that.dueTime,_that.estimatedDuration,_that.actualDuration,_that.energyLevel,_that.context,_that.focusTime,_that.notes,_that.sourceTask,_that.integrations,_that.createdAt,_that.updatedAt,_that.completedAt,_that.labels,_that.project,_that.provider);case _:
  return null;

}
}

}

/// @nodoc
@JsonSerializable()

class _TaskEntity extends TaskEntity {
  const _TaskEntity({required this.id, required this.title, this.description, this.projectId, this.parentId, this.priority = 2, this.status = TaskStatus.pending, this.dueDate, this.dueTime, this.estimatedDuration, this.actualDuration, this.energyLevel = 2, this.context = TaskContext.work, this.focusTime = false, this.notes, final  Map<String, dynamic>? sourceTask, final  Map<String, dynamic>? integrations, required this.createdAt, this.updatedAt, this.completedAt, final  List<LabelEntity> labels = const [], this.project, this.provider}): _sourceTask = sourceTask,_integrations = integrations,_labels = labels,super._();
  factory _TaskEntity.fromJson(Map<String, dynamic> json) => _$TaskEntityFromJson(json);

@override final  String id;
@override final  String title;
@override final  String? description;
@override final  String? projectId;
@override final  String? parentId;
@override@JsonKey() final  int priority;
@override@JsonKey() final  TaskStatus status;
@override final  DateTime? dueDate;
@override final  String? dueTime;
// Enhanced local features
@override final  int? estimatedDuration;
@override final  int? actualDuration;
@override@JsonKey() final  int energyLevel;
@override@JsonKey() final  TaskContext context;
@override@JsonKey() final  bool focusTime;
@override final  String? notes;
// External integration data
 final  Map<String, dynamic>? _sourceTask;
// External integration data
@override Map<String, dynamic>? get sourceTask {
  final value = _sourceTask;
  if (value == null) return null;
  if (_sourceTask is EqualUnmodifiableMapView) return _sourceTask;
  // ignore: implicit_dynamic_type
  return EqualUnmodifiableMapView(value);
}

 final  Map<String, dynamic>? _integrations;
@override Map<String, dynamic>? get integrations {
  final value = _integrations;
  if (value == null) return null;
  if (_integrations is EqualUnmodifiableMapView) return _integrations;
  // ignore: implicit_dynamic_type
  return EqualUnmodifiableMapView(value);
}

// Timestamps
@override final  DateTime createdAt;
@override final  DateTime? updatedAt;
@override final  DateTime? completedAt;
// Joined data (populated from relations)
 final  List<LabelEntity> _labels;
// Joined data (populated from relations)
@override@JsonKey() List<LabelEntity> get labels {
  if (_labels is EqualUnmodifiableListView) return _labels;
  // ignore: implicit_dynamic_type
  return EqualUnmodifiableListView(_labels);
}

@override final  ProjectEntity? project;
// Source provider
@override final  TaskProvider? provider;

/// Create a copy of TaskEntity
/// with the given fields replaced by the non-null parameter values.
@override @JsonKey(includeFromJson: false, includeToJson: false)
@pragma('vm:prefer-inline')
_$TaskEntityCopyWith<_TaskEntity> get copyWith => __$TaskEntityCopyWithImpl<_TaskEntity>(this, _$identity);

@override
Map<String, dynamic> toJson() {
  return _$TaskEntityToJson(this, );
}

@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is _TaskEntity&&(identical(other.id, id) || other.id == id)&&(identical(other.title, title) || other.title == title)&&(identical(other.description, description) || other.description == description)&&(identical(other.projectId, projectId) || other.projectId == projectId)&&(identical(other.parentId, parentId) || other.parentId == parentId)&&(identical(other.priority, priority) || other.priority == priority)&&(identical(other.status, status) || other.status == status)&&(identical(other.dueDate, dueDate) || other.dueDate == dueDate)&&(identical(other.dueTime, dueTime) || other.dueTime == dueTime)&&(identical(other.estimatedDuration, estimatedDuration) || other.estimatedDuration == estimatedDuration)&&(identical(other.actualDuration, actualDuration) || other.actualDuration == actualDuration)&&(identical(other.energyLevel, energyLevel) || other.energyLevel == energyLevel)&&(identical(other.context, context) || other.context == context)&&(identical(other.focusTime, focusTime) || other.focusTime == focusTime)&&(identical(other.notes, notes) || other.notes == notes)&&const DeepCollectionEquality().equals(other._sourceTask, _sourceTask)&&const DeepCollectionEquality().equals(other._integrations, _integrations)&&(identical(other.createdAt, createdAt) || other.createdAt == createdAt)&&(identical(other.updatedAt, updatedAt) || other.updatedAt == updatedAt)&&(identical(other.completedAt, completedAt) || other.completedAt == completedAt)&&const DeepCollectionEquality().equals(other._labels, _labels)&&(identical(other.project, project) || other.project == project)&&(identical(other.provider, provider) || other.provider == provider));
}

@JsonKey(includeFromJson: false, includeToJson: false)
@override
int get hashCode => Object.hashAll([runtimeType,id,title,description,projectId,parentId,priority,status,dueDate,dueTime,estimatedDuration,actualDuration,energyLevel,context,focusTime,notes,const DeepCollectionEquality().hash(_sourceTask),const DeepCollectionEquality().hash(_integrations),createdAt,updatedAt,completedAt,const DeepCollectionEquality().hash(_labels),project,provider]);

@override
String toString() {
  return 'TaskEntity(id: $id, title: $title, description: $description, projectId: $projectId, parentId: $parentId, priority: $priority, status: $status, dueDate: $dueDate, dueTime: $dueTime, estimatedDuration: $estimatedDuration, actualDuration: $actualDuration, energyLevel: $energyLevel, context: $context, focusTime: $focusTime, notes: $notes, sourceTask: $sourceTask, integrations: $integrations, createdAt: $createdAt, updatedAt: $updatedAt, completedAt: $completedAt, labels: $labels, project: $project, provider: $provider)';
}


}

/// @nodoc
abstract mixin class _$TaskEntityCopyWith<$Res> implements $TaskEntityCopyWith<$Res> {
  factory _$TaskEntityCopyWith(_TaskEntity value, $Res Function(_TaskEntity) _then) = __$TaskEntityCopyWithImpl;
@override @useResult
$Res call({
 String id, String title, String? description, String? projectId, String? parentId, int priority, TaskStatus status, DateTime? dueDate, String? dueTime, int? estimatedDuration, int? actualDuration, int energyLevel, TaskContext context, bool focusTime, String? notes, Map<String, dynamic>? sourceTask, Map<String, dynamic>? integrations, DateTime createdAt, DateTime? updatedAt, DateTime? completedAt, List<LabelEntity> labels, ProjectEntity? project, TaskProvider? provider
});


@override $ProjectEntityCopyWith<$Res>? get project;

}
/// @nodoc
class __$TaskEntityCopyWithImpl<$Res>
    implements _$TaskEntityCopyWith<$Res> {
  __$TaskEntityCopyWithImpl(this._self, this._then);

  final _TaskEntity _self;
  final $Res Function(_TaskEntity) _then;

/// Create a copy of TaskEntity
/// with the given fields replaced by the non-null parameter values.
@override @pragma('vm:prefer-inline') $Res call({Object? id = null,Object? title = null,Object? description = freezed,Object? projectId = freezed,Object? parentId = freezed,Object? priority = null,Object? status = null,Object? dueDate = freezed,Object? dueTime = freezed,Object? estimatedDuration = freezed,Object? actualDuration = freezed,Object? energyLevel = null,Object? context = null,Object? focusTime = null,Object? notes = freezed,Object? sourceTask = freezed,Object? integrations = freezed,Object? createdAt = null,Object? updatedAt = freezed,Object? completedAt = freezed,Object? labels = null,Object? project = freezed,Object? provider = freezed,}) {
  return _then(_TaskEntity(
id: null == id ? _self.id : id // ignore: cast_nullable_to_non_nullable
as String,title: null == title ? _self.title : title // ignore: cast_nullable_to_non_nullable
as String,description: freezed == description ? _self.description : description // ignore: cast_nullable_to_non_nullable
as String?,projectId: freezed == projectId ? _self.projectId : projectId // ignore: cast_nullable_to_non_nullable
as String?,parentId: freezed == parentId ? _self.parentId : parentId // ignore: cast_nullable_to_non_nullable
as String?,priority: null == priority ? _self.priority : priority // ignore: cast_nullable_to_non_nullable
as int,status: null == status ? _self.status : status // ignore: cast_nullable_to_non_nullable
as TaskStatus,dueDate: freezed == dueDate ? _self.dueDate : dueDate // ignore: cast_nullable_to_non_nullable
as DateTime?,dueTime: freezed == dueTime ? _self.dueTime : dueTime // ignore: cast_nullable_to_non_nullable
as String?,estimatedDuration: freezed == estimatedDuration ? _self.estimatedDuration : estimatedDuration // ignore: cast_nullable_to_non_nullable
as int?,actualDuration: freezed == actualDuration ? _self.actualDuration : actualDuration // ignore: cast_nullable_to_non_nullable
as int?,energyLevel: null == energyLevel ? _self.energyLevel : energyLevel // ignore: cast_nullable_to_non_nullable
as int,context: null == context ? _self.context : context // ignore: cast_nullable_to_non_nullable
as TaskContext,focusTime: null == focusTime ? _self.focusTime : focusTime // ignore: cast_nullable_to_non_nullable
as bool,notes: freezed == notes ? _self.notes : notes // ignore: cast_nullable_to_non_nullable
as String?,sourceTask: freezed == sourceTask ? _self._sourceTask : sourceTask // ignore: cast_nullable_to_non_nullable
as Map<String, dynamic>?,integrations: freezed == integrations ? _self._integrations : integrations // ignore: cast_nullable_to_non_nullable
as Map<String, dynamic>?,createdAt: null == createdAt ? _self.createdAt : createdAt // ignore: cast_nullable_to_non_nullable
as DateTime,updatedAt: freezed == updatedAt ? _self.updatedAt : updatedAt // ignore: cast_nullable_to_non_nullable
as DateTime?,completedAt: freezed == completedAt ? _self.completedAt : completedAt // ignore: cast_nullable_to_non_nullable
as DateTime?,labels: null == labels ? _self._labels : labels // ignore: cast_nullable_to_non_nullable
as List<LabelEntity>,project: freezed == project ? _self.project : project // ignore: cast_nullable_to_non_nullable
as ProjectEntity?,provider: freezed == provider ? _self.provider : provider // ignore: cast_nullable_to_non_nullable
as TaskProvider?,
  ));
}

/// Create a copy of TaskEntity
/// with the given fields replaced by the non-null parameter values.
@override
@pragma('vm:prefer-inline')
$ProjectEntityCopyWith<$Res>? get project {
    if (_self.project == null) {
    return null;
  }

  return $ProjectEntityCopyWith<$Res>(_self.project!, (value) {
    return _then(_self.copyWith(project: value));
  });
}
}

// dart format on
