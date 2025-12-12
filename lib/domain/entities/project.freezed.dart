// GENERATED CODE - DO NOT MODIFY BY HAND
// coverage:ignore-file
// ignore_for_file: type=lint
// ignore_for_file: unused_element, deprecated_member_use, deprecated_member_use_from_same_package, use_function_type_syntax_for_parameters, unnecessary_const, avoid_init_to_null, invalid_override_different_default_values_named, prefer_expression_function_bodies, annotate_overrides, invalid_annotation_target, unnecessary_question_mark

part of 'project.dart';

// **************************************************************************
// FreezedGenerator
// **************************************************************************

// dart format off
T _$identity<T>(T value) => value;

/// @nodoc
mixin _$ProjectEntity {

 String get id; String get name; String? get description; String get color; String? get icon; String? get parentId; int get sortOrder; bool get isFavorite; bool get isArchived; Map<String, dynamic>? get integrations; DateTime get createdAt; DateTime? get updatedAt; TaskProvider? get provider; int? get taskCount;
/// Create a copy of ProjectEntity
/// with the given fields replaced by the non-null parameter values.
@JsonKey(includeFromJson: false, includeToJson: false)
@pragma('vm:prefer-inline')
$ProjectEntityCopyWith<ProjectEntity> get copyWith => _$ProjectEntityCopyWithImpl<ProjectEntity>(this as ProjectEntity, _$identity);

  /// Serializes this ProjectEntity to a JSON map.
  Map<String, dynamic> toJson();


@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is ProjectEntity&&(identical(other.id, id) || other.id == id)&&(identical(other.name, name) || other.name == name)&&(identical(other.description, description) || other.description == description)&&(identical(other.color, color) || other.color == color)&&(identical(other.icon, icon) || other.icon == icon)&&(identical(other.parentId, parentId) || other.parentId == parentId)&&(identical(other.sortOrder, sortOrder) || other.sortOrder == sortOrder)&&(identical(other.isFavorite, isFavorite) || other.isFavorite == isFavorite)&&(identical(other.isArchived, isArchived) || other.isArchived == isArchived)&&const DeepCollectionEquality().equals(other.integrations, integrations)&&(identical(other.createdAt, createdAt) || other.createdAt == createdAt)&&(identical(other.updatedAt, updatedAt) || other.updatedAt == updatedAt)&&(identical(other.provider, provider) || other.provider == provider)&&(identical(other.taskCount, taskCount) || other.taskCount == taskCount));
}

@JsonKey(includeFromJson: false, includeToJson: false)
@override
int get hashCode => Object.hash(runtimeType,id,name,description,color,icon,parentId,sortOrder,isFavorite,isArchived,const DeepCollectionEquality().hash(integrations),createdAt,updatedAt,provider,taskCount);

@override
String toString() {
  return 'ProjectEntity(id: $id, name: $name, description: $description, color: $color, icon: $icon, parentId: $parentId, sortOrder: $sortOrder, isFavorite: $isFavorite, isArchived: $isArchived, integrations: $integrations, createdAt: $createdAt, updatedAt: $updatedAt, provider: $provider, taskCount: $taskCount)';
}


}

/// @nodoc
abstract mixin class $ProjectEntityCopyWith<$Res>  {
  factory $ProjectEntityCopyWith(ProjectEntity value, $Res Function(ProjectEntity) _then) = _$ProjectEntityCopyWithImpl;
@useResult
$Res call({
 String id, String name, String? description, String color, String? icon, String? parentId, int sortOrder, bool isFavorite, bool isArchived, Map<String, dynamic>? integrations, DateTime createdAt, DateTime? updatedAt, TaskProvider? provider, int? taskCount
});




}
/// @nodoc
class _$ProjectEntityCopyWithImpl<$Res>
    implements $ProjectEntityCopyWith<$Res> {
  _$ProjectEntityCopyWithImpl(this._self, this._then);

  final ProjectEntity _self;
  final $Res Function(ProjectEntity) _then;

/// Create a copy of ProjectEntity
/// with the given fields replaced by the non-null parameter values.
@pragma('vm:prefer-inline') @override $Res call({Object? id = null,Object? name = null,Object? description = freezed,Object? color = null,Object? icon = freezed,Object? parentId = freezed,Object? sortOrder = null,Object? isFavorite = null,Object? isArchived = null,Object? integrations = freezed,Object? createdAt = null,Object? updatedAt = freezed,Object? provider = freezed,Object? taskCount = freezed,}) {
  return _then(_self.copyWith(
id: null == id ? _self.id : id // ignore: cast_nullable_to_non_nullable
as String,name: null == name ? _self.name : name // ignore: cast_nullable_to_non_nullable
as String,description: freezed == description ? _self.description : description // ignore: cast_nullable_to_non_nullable
as String?,color: null == color ? _self.color : color // ignore: cast_nullable_to_non_nullable
as String,icon: freezed == icon ? _self.icon : icon // ignore: cast_nullable_to_non_nullable
as String?,parentId: freezed == parentId ? _self.parentId : parentId // ignore: cast_nullable_to_non_nullable
as String?,sortOrder: null == sortOrder ? _self.sortOrder : sortOrder // ignore: cast_nullable_to_non_nullable
as int,isFavorite: null == isFavorite ? _self.isFavorite : isFavorite // ignore: cast_nullable_to_non_nullable
as bool,isArchived: null == isArchived ? _self.isArchived : isArchived // ignore: cast_nullable_to_non_nullable
as bool,integrations: freezed == integrations ? _self.integrations : integrations // ignore: cast_nullable_to_non_nullable
as Map<String, dynamic>?,createdAt: null == createdAt ? _self.createdAt : createdAt // ignore: cast_nullable_to_non_nullable
as DateTime,updatedAt: freezed == updatedAt ? _self.updatedAt : updatedAt // ignore: cast_nullable_to_non_nullable
as DateTime?,provider: freezed == provider ? _self.provider : provider // ignore: cast_nullable_to_non_nullable
as TaskProvider?,taskCount: freezed == taskCount ? _self.taskCount : taskCount // ignore: cast_nullable_to_non_nullable
as int?,
  ));
}

}


/// Adds pattern-matching-related methods to [ProjectEntity].
extension ProjectEntityPatterns on ProjectEntity {
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

@optionalTypeArgs TResult maybeMap<TResult extends Object?>(TResult Function( _ProjectEntity value)?  $default,{required TResult orElse(),}){
final _that = this;
switch (_that) {
case _ProjectEntity() when $default != null:
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

@optionalTypeArgs TResult map<TResult extends Object?>(TResult Function( _ProjectEntity value)  $default,){
final _that = this;
switch (_that) {
case _ProjectEntity():
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

@optionalTypeArgs TResult? mapOrNull<TResult extends Object?>(TResult? Function( _ProjectEntity value)?  $default,){
final _that = this;
switch (_that) {
case _ProjectEntity() when $default != null:
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

@optionalTypeArgs TResult maybeWhen<TResult extends Object?>(TResult Function( String id,  String name,  String? description,  String color,  String? icon,  String? parentId,  int sortOrder,  bool isFavorite,  bool isArchived,  Map<String, dynamic>? integrations,  DateTime createdAt,  DateTime? updatedAt,  TaskProvider? provider,  int? taskCount)?  $default,{required TResult orElse(),}) {final _that = this;
switch (_that) {
case _ProjectEntity() when $default != null:
return $default(_that.id,_that.name,_that.description,_that.color,_that.icon,_that.parentId,_that.sortOrder,_that.isFavorite,_that.isArchived,_that.integrations,_that.createdAt,_that.updatedAt,_that.provider,_that.taskCount);case _:
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

@optionalTypeArgs TResult when<TResult extends Object?>(TResult Function( String id,  String name,  String? description,  String color,  String? icon,  String? parentId,  int sortOrder,  bool isFavorite,  bool isArchived,  Map<String, dynamic>? integrations,  DateTime createdAt,  DateTime? updatedAt,  TaskProvider? provider,  int? taskCount)  $default,) {final _that = this;
switch (_that) {
case _ProjectEntity():
return $default(_that.id,_that.name,_that.description,_that.color,_that.icon,_that.parentId,_that.sortOrder,_that.isFavorite,_that.isArchived,_that.integrations,_that.createdAt,_that.updatedAt,_that.provider,_that.taskCount);}
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

@optionalTypeArgs TResult? whenOrNull<TResult extends Object?>(TResult? Function( String id,  String name,  String? description,  String color,  String? icon,  String? parentId,  int sortOrder,  bool isFavorite,  bool isArchived,  Map<String, dynamic>? integrations,  DateTime createdAt,  DateTime? updatedAt,  TaskProvider? provider,  int? taskCount)?  $default,) {final _that = this;
switch (_that) {
case _ProjectEntity() when $default != null:
return $default(_that.id,_that.name,_that.description,_that.color,_that.icon,_that.parentId,_that.sortOrder,_that.isFavorite,_that.isArchived,_that.integrations,_that.createdAt,_that.updatedAt,_that.provider,_that.taskCount);case _:
  return null;

}
}

}

/// @nodoc
@JsonSerializable()

class _ProjectEntity extends ProjectEntity {
  const _ProjectEntity({required this.id, required this.name, this.description, this.color = '#808080', this.icon, this.parentId, this.sortOrder = 0, this.isFavorite = false, this.isArchived = false, final  Map<String, dynamic>? integrations, required this.createdAt, this.updatedAt, this.provider, this.taskCount}): _integrations = integrations,super._();
  factory _ProjectEntity.fromJson(Map<String, dynamic> json) => _$ProjectEntityFromJson(json);

@override final  String id;
@override final  String name;
@override final  String? description;
@override@JsonKey() final  String color;
@override final  String? icon;
@override final  String? parentId;
@override@JsonKey() final  int sortOrder;
@override@JsonKey() final  bool isFavorite;
@override@JsonKey() final  bool isArchived;
 final  Map<String, dynamic>? _integrations;
@override Map<String, dynamic>? get integrations {
  final value = _integrations;
  if (value == null) return null;
  if (_integrations is EqualUnmodifiableMapView) return _integrations;
  // ignore: implicit_dynamic_type
  return EqualUnmodifiableMapView(value);
}

@override final  DateTime createdAt;
@override final  DateTime? updatedAt;
@override final  TaskProvider? provider;
@override final  int? taskCount;

/// Create a copy of ProjectEntity
/// with the given fields replaced by the non-null parameter values.
@override @JsonKey(includeFromJson: false, includeToJson: false)
@pragma('vm:prefer-inline')
_$ProjectEntityCopyWith<_ProjectEntity> get copyWith => __$ProjectEntityCopyWithImpl<_ProjectEntity>(this, _$identity);

@override
Map<String, dynamic> toJson() {
  return _$ProjectEntityToJson(this, );
}

@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is _ProjectEntity&&(identical(other.id, id) || other.id == id)&&(identical(other.name, name) || other.name == name)&&(identical(other.description, description) || other.description == description)&&(identical(other.color, color) || other.color == color)&&(identical(other.icon, icon) || other.icon == icon)&&(identical(other.parentId, parentId) || other.parentId == parentId)&&(identical(other.sortOrder, sortOrder) || other.sortOrder == sortOrder)&&(identical(other.isFavorite, isFavorite) || other.isFavorite == isFavorite)&&(identical(other.isArchived, isArchived) || other.isArchived == isArchived)&&const DeepCollectionEquality().equals(other._integrations, _integrations)&&(identical(other.createdAt, createdAt) || other.createdAt == createdAt)&&(identical(other.updatedAt, updatedAt) || other.updatedAt == updatedAt)&&(identical(other.provider, provider) || other.provider == provider)&&(identical(other.taskCount, taskCount) || other.taskCount == taskCount));
}

@JsonKey(includeFromJson: false, includeToJson: false)
@override
int get hashCode => Object.hash(runtimeType,id,name,description,color,icon,parentId,sortOrder,isFavorite,isArchived,const DeepCollectionEquality().hash(_integrations),createdAt,updatedAt,provider,taskCount);

@override
String toString() {
  return 'ProjectEntity(id: $id, name: $name, description: $description, color: $color, icon: $icon, parentId: $parentId, sortOrder: $sortOrder, isFavorite: $isFavorite, isArchived: $isArchived, integrations: $integrations, createdAt: $createdAt, updatedAt: $updatedAt, provider: $provider, taskCount: $taskCount)';
}


}

/// @nodoc
abstract mixin class _$ProjectEntityCopyWith<$Res> implements $ProjectEntityCopyWith<$Res> {
  factory _$ProjectEntityCopyWith(_ProjectEntity value, $Res Function(_ProjectEntity) _then) = __$ProjectEntityCopyWithImpl;
@override @useResult
$Res call({
 String id, String name, String? description, String color, String? icon, String? parentId, int sortOrder, bool isFavorite, bool isArchived, Map<String, dynamic>? integrations, DateTime createdAt, DateTime? updatedAt, TaskProvider? provider, int? taskCount
});




}
/// @nodoc
class __$ProjectEntityCopyWithImpl<$Res>
    implements _$ProjectEntityCopyWith<$Res> {
  __$ProjectEntityCopyWithImpl(this._self, this._then);

  final _ProjectEntity _self;
  final $Res Function(_ProjectEntity) _then;

/// Create a copy of ProjectEntity
/// with the given fields replaced by the non-null parameter values.
@override @pragma('vm:prefer-inline') $Res call({Object? id = null,Object? name = null,Object? description = freezed,Object? color = null,Object? icon = freezed,Object? parentId = freezed,Object? sortOrder = null,Object? isFavorite = null,Object? isArchived = null,Object? integrations = freezed,Object? createdAt = null,Object? updatedAt = freezed,Object? provider = freezed,Object? taskCount = freezed,}) {
  return _then(_ProjectEntity(
id: null == id ? _self.id : id // ignore: cast_nullable_to_non_nullable
as String,name: null == name ? _self.name : name // ignore: cast_nullable_to_non_nullable
as String,description: freezed == description ? _self.description : description // ignore: cast_nullable_to_non_nullable
as String?,color: null == color ? _self.color : color // ignore: cast_nullable_to_non_nullable
as String,icon: freezed == icon ? _self.icon : icon // ignore: cast_nullable_to_non_nullable
as String?,parentId: freezed == parentId ? _self.parentId : parentId // ignore: cast_nullable_to_non_nullable
as String?,sortOrder: null == sortOrder ? _self.sortOrder : sortOrder // ignore: cast_nullable_to_non_nullable
as int,isFavorite: null == isFavorite ? _self.isFavorite : isFavorite // ignore: cast_nullable_to_non_nullable
as bool,isArchived: null == isArchived ? _self.isArchived : isArchived // ignore: cast_nullable_to_non_nullable
as bool,integrations: freezed == integrations ? _self._integrations : integrations // ignore: cast_nullable_to_non_nullable
as Map<String, dynamic>?,createdAt: null == createdAt ? _self.createdAt : createdAt // ignore: cast_nullable_to_non_nullable
as DateTime,updatedAt: freezed == updatedAt ? _self.updatedAt : updatedAt // ignore: cast_nullable_to_non_nullable
as DateTime?,provider: freezed == provider ? _self.provider : provider // ignore: cast_nullable_to_non_nullable
as TaskProvider?,taskCount: freezed == taskCount ? _self.taskCount : taskCount // ignore: cast_nullable_to_non_nullable
as int?,
  ));
}


}

// dart format on
