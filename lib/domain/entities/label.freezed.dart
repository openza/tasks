// GENERATED CODE - DO NOT MODIFY BY HAND
// coverage:ignore-file
// ignore_for_file: type=lint
// ignore_for_file: unused_element, deprecated_member_use, deprecated_member_use_from_same_package, use_function_type_syntax_for_parameters, unnecessary_const, avoid_init_to_null, invalid_override_different_default_values_named, prefer_expression_function_bodies, annotate_overrides, invalid_annotation_target, unnecessary_question_mark

part of 'label.dart';

// **************************************************************************
// FreezedGenerator
// **************************************************************************

// dart format off
T _$identity<T>(T value) => value;

/// @nodoc
mixin _$LabelEntity {

 String get id; String get name; String get color; String? get description; int get sortOrder; Map<String, dynamic>? get integrations; DateTime get createdAt; TaskProvider? get provider; int? get taskCount;
/// Create a copy of LabelEntity
/// with the given fields replaced by the non-null parameter values.
@JsonKey(includeFromJson: false, includeToJson: false)
@pragma('vm:prefer-inline')
$LabelEntityCopyWith<LabelEntity> get copyWith => _$LabelEntityCopyWithImpl<LabelEntity>(this as LabelEntity, _$identity);

  /// Serializes this LabelEntity to a JSON map.
  Map<String, dynamic> toJson();


@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is LabelEntity&&(identical(other.id, id) || other.id == id)&&(identical(other.name, name) || other.name == name)&&(identical(other.color, color) || other.color == color)&&(identical(other.description, description) || other.description == description)&&(identical(other.sortOrder, sortOrder) || other.sortOrder == sortOrder)&&const DeepCollectionEquality().equals(other.integrations, integrations)&&(identical(other.createdAt, createdAt) || other.createdAt == createdAt)&&(identical(other.provider, provider) || other.provider == provider)&&(identical(other.taskCount, taskCount) || other.taskCount == taskCount));
}

@JsonKey(includeFromJson: false, includeToJson: false)
@override
int get hashCode => Object.hash(runtimeType,id,name,color,description,sortOrder,const DeepCollectionEquality().hash(integrations),createdAt,provider,taskCount);

@override
String toString() {
  return 'LabelEntity(id: $id, name: $name, color: $color, description: $description, sortOrder: $sortOrder, integrations: $integrations, createdAt: $createdAt, provider: $provider, taskCount: $taskCount)';
}


}

/// @nodoc
abstract mixin class $LabelEntityCopyWith<$Res>  {
  factory $LabelEntityCopyWith(LabelEntity value, $Res Function(LabelEntity) _then) = _$LabelEntityCopyWithImpl;
@useResult
$Res call({
 String id, String name, String color, String? description, int sortOrder, Map<String, dynamic>? integrations, DateTime createdAt, TaskProvider? provider, int? taskCount
});




}
/// @nodoc
class _$LabelEntityCopyWithImpl<$Res>
    implements $LabelEntityCopyWith<$Res> {
  _$LabelEntityCopyWithImpl(this._self, this._then);

  final LabelEntity _self;
  final $Res Function(LabelEntity) _then;

/// Create a copy of LabelEntity
/// with the given fields replaced by the non-null parameter values.
@pragma('vm:prefer-inline') @override $Res call({Object? id = null,Object? name = null,Object? color = null,Object? description = freezed,Object? sortOrder = null,Object? integrations = freezed,Object? createdAt = null,Object? provider = freezed,Object? taskCount = freezed,}) {
  return _then(_self.copyWith(
id: null == id ? _self.id : id // ignore: cast_nullable_to_non_nullable
as String,name: null == name ? _self.name : name // ignore: cast_nullable_to_non_nullable
as String,color: null == color ? _self.color : color // ignore: cast_nullable_to_non_nullable
as String,description: freezed == description ? _self.description : description // ignore: cast_nullable_to_non_nullable
as String?,sortOrder: null == sortOrder ? _self.sortOrder : sortOrder // ignore: cast_nullable_to_non_nullable
as int,integrations: freezed == integrations ? _self.integrations : integrations // ignore: cast_nullable_to_non_nullable
as Map<String, dynamic>?,createdAt: null == createdAt ? _self.createdAt : createdAt // ignore: cast_nullable_to_non_nullable
as DateTime,provider: freezed == provider ? _self.provider : provider // ignore: cast_nullable_to_non_nullable
as TaskProvider?,taskCount: freezed == taskCount ? _self.taskCount : taskCount // ignore: cast_nullable_to_non_nullable
as int?,
  ));
}

}


/// Adds pattern-matching-related methods to [LabelEntity].
extension LabelEntityPatterns on LabelEntity {
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

@optionalTypeArgs TResult maybeMap<TResult extends Object?>(TResult Function( _LabelEntity value)?  $default,{required TResult orElse(),}){
final _that = this;
switch (_that) {
case _LabelEntity() when $default != null:
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

@optionalTypeArgs TResult map<TResult extends Object?>(TResult Function( _LabelEntity value)  $default,){
final _that = this;
switch (_that) {
case _LabelEntity():
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

@optionalTypeArgs TResult? mapOrNull<TResult extends Object?>(TResult? Function( _LabelEntity value)?  $default,){
final _that = this;
switch (_that) {
case _LabelEntity() when $default != null:
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

@optionalTypeArgs TResult maybeWhen<TResult extends Object?>(TResult Function( String id,  String name,  String color,  String? description,  int sortOrder,  Map<String, dynamic>? integrations,  DateTime createdAt,  TaskProvider? provider,  int? taskCount)?  $default,{required TResult orElse(),}) {final _that = this;
switch (_that) {
case _LabelEntity() when $default != null:
return $default(_that.id,_that.name,_that.color,_that.description,_that.sortOrder,_that.integrations,_that.createdAt,_that.provider,_that.taskCount);case _:
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

@optionalTypeArgs TResult when<TResult extends Object?>(TResult Function( String id,  String name,  String color,  String? description,  int sortOrder,  Map<String, dynamic>? integrations,  DateTime createdAt,  TaskProvider? provider,  int? taskCount)  $default,) {final _that = this;
switch (_that) {
case _LabelEntity():
return $default(_that.id,_that.name,_that.color,_that.description,_that.sortOrder,_that.integrations,_that.createdAt,_that.provider,_that.taskCount);}
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

@optionalTypeArgs TResult? whenOrNull<TResult extends Object?>(TResult? Function( String id,  String name,  String color,  String? description,  int sortOrder,  Map<String, dynamic>? integrations,  DateTime createdAt,  TaskProvider? provider,  int? taskCount)?  $default,) {final _that = this;
switch (_that) {
case _LabelEntity() when $default != null:
return $default(_that.id,_that.name,_that.color,_that.description,_that.sortOrder,_that.integrations,_that.createdAt,_that.provider,_that.taskCount);case _:
  return null;

}
}

}

/// @nodoc
@JsonSerializable()

class _LabelEntity implements LabelEntity {
  const _LabelEntity({required this.id, required this.name, this.color = '#808080', this.description, this.sortOrder = 0, final  Map<String, dynamic>? integrations, required this.createdAt, this.provider, this.taskCount}): _integrations = integrations;
  factory _LabelEntity.fromJson(Map<String, dynamic> json) => _$LabelEntityFromJson(json);

@override final  String id;
@override final  String name;
@override@JsonKey() final  String color;
@override final  String? description;
@override@JsonKey() final  int sortOrder;
 final  Map<String, dynamic>? _integrations;
@override Map<String, dynamic>? get integrations {
  final value = _integrations;
  if (value == null) return null;
  if (_integrations is EqualUnmodifiableMapView) return _integrations;
  // ignore: implicit_dynamic_type
  return EqualUnmodifiableMapView(value);
}

@override final  DateTime createdAt;
@override final  TaskProvider? provider;
@override final  int? taskCount;

/// Create a copy of LabelEntity
/// with the given fields replaced by the non-null parameter values.
@override @JsonKey(includeFromJson: false, includeToJson: false)
@pragma('vm:prefer-inline')
_$LabelEntityCopyWith<_LabelEntity> get copyWith => __$LabelEntityCopyWithImpl<_LabelEntity>(this, _$identity);

@override
Map<String, dynamic> toJson() {
  return _$LabelEntityToJson(this, );
}

@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is _LabelEntity&&(identical(other.id, id) || other.id == id)&&(identical(other.name, name) || other.name == name)&&(identical(other.color, color) || other.color == color)&&(identical(other.description, description) || other.description == description)&&(identical(other.sortOrder, sortOrder) || other.sortOrder == sortOrder)&&const DeepCollectionEquality().equals(other._integrations, _integrations)&&(identical(other.createdAt, createdAt) || other.createdAt == createdAt)&&(identical(other.provider, provider) || other.provider == provider)&&(identical(other.taskCount, taskCount) || other.taskCount == taskCount));
}

@JsonKey(includeFromJson: false, includeToJson: false)
@override
int get hashCode => Object.hash(runtimeType,id,name,color,description,sortOrder,const DeepCollectionEquality().hash(_integrations),createdAt,provider,taskCount);

@override
String toString() {
  return 'LabelEntity(id: $id, name: $name, color: $color, description: $description, sortOrder: $sortOrder, integrations: $integrations, createdAt: $createdAt, provider: $provider, taskCount: $taskCount)';
}


}

/// @nodoc
abstract mixin class _$LabelEntityCopyWith<$Res> implements $LabelEntityCopyWith<$Res> {
  factory _$LabelEntityCopyWith(_LabelEntity value, $Res Function(_LabelEntity) _then) = __$LabelEntityCopyWithImpl;
@override @useResult
$Res call({
 String id, String name, String color, String? description, int sortOrder, Map<String, dynamic>? integrations, DateTime createdAt, TaskProvider? provider, int? taskCount
});




}
/// @nodoc
class __$LabelEntityCopyWithImpl<$Res>
    implements _$LabelEntityCopyWith<$Res> {
  __$LabelEntityCopyWithImpl(this._self, this._then);

  final _LabelEntity _self;
  final $Res Function(_LabelEntity) _then;

/// Create a copy of LabelEntity
/// with the given fields replaced by the non-null parameter values.
@override @pragma('vm:prefer-inline') $Res call({Object? id = null,Object? name = null,Object? color = null,Object? description = freezed,Object? sortOrder = null,Object? integrations = freezed,Object? createdAt = null,Object? provider = freezed,Object? taskCount = freezed,}) {
  return _then(_LabelEntity(
id: null == id ? _self.id : id // ignore: cast_nullable_to_non_nullable
as String,name: null == name ? _self.name : name // ignore: cast_nullable_to_non_nullable
as String,color: null == color ? _self.color : color // ignore: cast_nullable_to_non_nullable
as String,description: freezed == description ? _self.description : description // ignore: cast_nullable_to_non_nullable
as String?,sortOrder: null == sortOrder ? _self.sortOrder : sortOrder // ignore: cast_nullable_to_non_nullable
as int,integrations: freezed == integrations ? _self._integrations : integrations // ignore: cast_nullable_to_non_nullable
as Map<String, dynamic>?,createdAt: null == createdAt ? _self.createdAt : createdAt // ignore: cast_nullable_to_non_nullable
as DateTime,provider: freezed == provider ? _self.provider : provider // ignore: cast_nullable_to_non_nullable
as TaskProvider?,taskCount: freezed == taskCount ? _self.taskCount : taskCount // ignore: cast_nullable_to_non_nullable
as int?,
  ));
}


}

// dart format on
