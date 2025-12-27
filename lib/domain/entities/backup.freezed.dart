// GENERATED CODE - DO NOT MODIFY BY HAND
// coverage:ignore-file
// ignore_for_file: type=lint
// ignore_for_file: unused_element, deprecated_member_use, deprecated_member_use_from_same_package, use_function_type_syntax_for_parameters, unnecessary_const, avoid_init_to_null, invalid_override_different_default_values_named, prefer_expression_function_bodies, annotate_overrides, invalid_annotation_target, unnecessary_question_mark

part of 'backup.dart';

// **************************************************************************
// FreezedGenerator
// **************************************************************************

// dart format off
T _$identity<T>(T value) => value;

/// @nodoc
mixin _$BackupInfo {

 String get fileName; String get filePath; DateTime get createdAt; int get sizeInBytes;
/// Create a copy of BackupInfo
/// with the given fields replaced by the non-null parameter values.
@JsonKey(includeFromJson: false, includeToJson: false)
@pragma('vm:prefer-inline')
$BackupInfoCopyWith<BackupInfo> get copyWith => _$BackupInfoCopyWithImpl<BackupInfo>(this as BackupInfo, _$identity);

  /// Serializes this BackupInfo to a JSON map.
  Map<String, dynamic> toJson();


@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is BackupInfo&&(identical(other.fileName, fileName) || other.fileName == fileName)&&(identical(other.filePath, filePath) || other.filePath == filePath)&&(identical(other.createdAt, createdAt) || other.createdAt == createdAt)&&(identical(other.sizeInBytes, sizeInBytes) || other.sizeInBytes == sizeInBytes));
}

@JsonKey(includeFromJson: false, includeToJson: false)
@override
int get hashCode => Object.hash(runtimeType,fileName,filePath,createdAt,sizeInBytes);

@override
String toString() {
  return 'BackupInfo(fileName: $fileName, filePath: $filePath, createdAt: $createdAt, sizeInBytes: $sizeInBytes)';
}


}

/// @nodoc
abstract mixin class $BackupInfoCopyWith<$Res>  {
  factory $BackupInfoCopyWith(BackupInfo value, $Res Function(BackupInfo) _then) = _$BackupInfoCopyWithImpl;
@useResult
$Res call({
 String fileName, String filePath, DateTime createdAt, int sizeInBytes
});




}
/// @nodoc
class _$BackupInfoCopyWithImpl<$Res>
    implements $BackupInfoCopyWith<$Res> {
  _$BackupInfoCopyWithImpl(this._self, this._then);

  final BackupInfo _self;
  final $Res Function(BackupInfo) _then;

/// Create a copy of BackupInfo
/// with the given fields replaced by the non-null parameter values.
@pragma('vm:prefer-inline') @override $Res call({Object? fileName = null,Object? filePath = null,Object? createdAt = null,Object? sizeInBytes = null,}) {
  return _then(_self.copyWith(
fileName: null == fileName ? _self.fileName : fileName // ignore: cast_nullable_to_non_nullable
as String,filePath: null == filePath ? _self.filePath : filePath // ignore: cast_nullable_to_non_nullable
as String,createdAt: null == createdAt ? _self.createdAt : createdAt // ignore: cast_nullable_to_non_nullable
as DateTime,sizeInBytes: null == sizeInBytes ? _self.sizeInBytes : sizeInBytes // ignore: cast_nullable_to_non_nullable
as int,
  ));
}

}


/// Adds pattern-matching-related methods to [BackupInfo].
extension BackupInfoPatterns on BackupInfo {
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

@optionalTypeArgs TResult maybeMap<TResult extends Object?>(TResult Function( _BackupInfo value)?  $default,{required TResult orElse(),}){
final _that = this;
switch (_that) {
case _BackupInfo() when $default != null:
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

@optionalTypeArgs TResult map<TResult extends Object?>(TResult Function( _BackupInfo value)  $default,){
final _that = this;
switch (_that) {
case _BackupInfo():
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

@optionalTypeArgs TResult? mapOrNull<TResult extends Object?>(TResult? Function( _BackupInfo value)?  $default,){
final _that = this;
switch (_that) {
case _BackupInfo() when $default != null:
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

@optionalTypeArgs TResult maybeWhen<TResult extends Object?>(TResult Function( String fileName,  String filePath,  DateTime createdAt,  int sizeInBytes)?  $default,{required TResult orElse(),}) {final _that = this;
switch (_that) {
case _BackupInfo() when $default != null:
return $default(_that.fileName,_that.filePath,_that.createdAt,_that.sizeInBytes);case _:
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

@optionalTypeArgs TResult when<TResult extends Object?>(TResult Function( String fileName,  String filePath,  DateTime createdAt,  int sizeInBytes)  $default,) {final _that = this;
switch (_that) {
case _BackupInfo():
return $default(_that.fileName,_that.filePath,_that.createdAt,_that.sizeInBytes);}
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

@optionalTypeArgs TResult? whenOrNull<TResult extends Object?>(TResult? Function( String fileName,  String filePath,  DateTime createdAt,  int sizeInBytes)?  $default,) {final _that = this;
switch (_that) {
case _BackupInfo() when $default != null:
return $default(_that.fileName,_that.filePath,_that.createdAt,_that.sizeInBytes);case _:
  return null;

}
}

}

/// @nodoc
@JsonSerializable()

class _BackupInfo extends BackupInfo {
  const _BackupInfo({required this.fileName, required this.filePath, required this.createdAt, required this.sizeInBytes}): super._();
  factory _BackupInfo.fromJson(Map<String, dynamic> json) => _$BackupInfoFromJson(json);

@override final  String fileName;
@override final  String filePath;
@override final  DateTime createdAt;
@override final  int sizeInBytes;

/// Create a copy of BackupInfo
/// with the given fields replaced by the non-null parameter values.
@override @JsonKey(includeFromJson: false, includeToJson: false)
@pragma('vm:prefer-inline')
_$BackupInfoCopyWith<_BackupInfo> get copyWith => __$BackupInfoCopyWithImpl<_BackupInfo>(this, _$identity);

@override
Map<String, dynamic> toJson() {
  return _$BackupInfoToJson(this, );
}

@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is _BackupInfo&&(identical(other.fileName, fileName) || other.fileName == fileName)&&(identical(other.filePath, filePath) || other.filePath == filePath)&&(identical(other.createdAt, createdAt) || other.createdAt == createdAt)&&(identical(other.sizeInBytes, sizeInBytes) || other.sizeInBytes == sizeInBytes));
}

@JsonKey(includeFromJson: false, includeToJson: false)
@override
int get hashCode => Object.hash(runtimeType,fileName,filePath,createdAt,sizeInBytes);

@override
String toString() {
  return 'BackupInfo(fileName: $fileName, filePath: $filePath, createdAt: $createdAt, sizeInBytes: $sizeInBytes)';
}


}

/// @nodoc
abstract mixin class _$BackupInfoCopyWith<$Res> implements $BackupInfoCopyWith<$Res> {
  factory _$BackupInfoCopyWith(_BackupInfo value, $Res Function(_BackupInfo) _then) = __$BackupInfoCopyWithImpl;
@override @useResult
$Res call({
 String fileName, String filePath, DateTime createdAt, int sizeInBytes
});




}
/// @nodoc
class __$BackupInfoCopyWithImpl<$Res>
    implements _$BackupInfoCopyWith<$Res> {
  __$BackupInfoCopyWithImpl(this._self, this._then);

  final _BackupInfo _self;
  final $Res Function(_BackupInfo) _then;

/// Create a copy of BackupInfo
/// with the given fields replaced by the non-null parameter values.
@override @pragma('vm:prefer-inline') $Res call({Object? fileName = null,Object? filePath = null,Object? createdAt = null,Object? sizeInBytes = null,}) {
  return _then(_BackupInfo(
fileName: null == fileName ? _self.fileName : fileName // ignore: cast_nullable_to_non_nullable
as String,filePath: null == filePath ? _self.filePath : filePath // ignore: cast_nullable_to_non_nullable
as String,createdAt: null == createdAt ? _self.createdAt : createdAt // ignore: cast_nullable_to_non_nullable
as DateTime,sizeInBytes: null == sizeInBytes ? _self.sizeInBytes : sizeInBytes // ignore: cast_nullable_to_non_nullable
as int,
  ));
}


}

/// @nodoc
mixin _$BackupResult {





@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is BackupResult);
}


@override
int get hashCode => runtimeType.hashCode;

@override
String toString() {
  return 'BackupResult()';
}


}

/// @nodoc
class $BackupResultCopyWith<$Res>  {
$BackupResultCopyWith(BackupResult _, $Res Function(BackupResult) __);
}


/// Adds pattern-matching-related methods to [BackupResult].
extension BackupResultPatterns on BackupResult {
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

@optionalTypeArgs TResult maybeMap<TResult extends Object?>({TResult Function( BackupResultSuccess value)?  success,TResult Function( BackupResultError value)?  error,required TResult orElse(),}){
final _that = this;
switch (_that) {
case BackupResultSuccess() when success != null:
return success(_that);case BackupResultError() when error != null:
return error(_that);case _:
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

@optionalTypeArgs TResult map<TResult extends Object?>({required TResult Function( BackupResultSuccess value)  success,required TResult Function( BackupResultError value)  error,}){
final _that = this;
switch (_that) {
case BackupResultSuccess():
return success(_that);case BackupResultError():
return error(_that);}
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

@optionalTypeArgs TResult? mapOrNull<TResult extends Object?>({TResult? Function( BackupResultSuccess value)?  success,TResult? Function( BackupResultError value)?  error,}){
final _that = this;
switch (_that) {
case BackupResultSuccess() when success != null:
return success(_that);case BackupResultError() when error != null:
return error(_that);case _:
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

@optionalTypeArgs TResult maybeWhen<TResult extends Object?>({TResult Function( BackupInfo backup)?  success,TResult Function( String message)?  error,required TResult orElse(),}) {final _that = this;
switch (_that) {
case BackupResultSuccess() when success != null:
return success(_that.backup);case BackupResultError() when error != null:
return error(_that.message);case _:
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

@optionalTypeArgs TResult when<TResult extends Object?>({required TResult Function( BackupInfo backup)  success,required TResult Function( String message)  error,}) {final _that = this;
switch (_that) {
case BackupResultSuccess():
return success(_that.backup);case BackupResultError():
return error(_that.message);}
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

@optionalTypeArgs TResult? whenOrNull<TResult extends Object?>({TResult? Function( BackupInfo backup)?  success,TResult? Function( String message)?  error,}) {final _that = this;
switch (_that) {
case BackupResultSuccess() when success != null:
return success(_that.backup);case BackupResultError() when error != null:
return error(_that.message);case _:
  return null;

}
}

}

/// @nodoc


class BackupResultSuccess implements BackupResult {
  const BackupResultSuccess(this.backup);
  

 final  BackupInfo backup;

/// Create a copy of BackupResult
/// with the given fields replaced by the non-null parameter values.
@JsonKey(includeFromJson: false, includeToJson: false)
@pragma('vm:prefer-inline')
$BackupResultSuccessCopyWith<BackupResultSuccess> get copyWith => _$BackupResultSuccessCopyWithImpl<BackupResultSuccess>(this, _$identity);



@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is BackupResultSuccess&&(identical(other.backup, backup) || other.backup == backup));
}


@override
int get hashCode => Object.hash(runtimeType,backup);

@override
String toString() {
  return 'BackupResult.success(backup: $backup)';
}


}

/// @nodoc
abstract mixin class $BackupResultSuccessCopyWith<$Res> implements $BackupResultCopyWith<$Res> {
  factory $BackupResultSuccessCopyWith(BackupResultSuccess value, $Res Function(BackupResultSuccess) _then) = _$BackupResultSuccessCopyWithImpl;
@useResult
$Res call({
 BackupInfo backup
});


$BackupInfoCopyWith<$Res> get backup;

}
/// @nodoc
class _$BackupResultSuccessCopyWithImpl<$Res>
    implements $BackupResultSuccessCopyWith<$Res> {
  _$BackupResultSuccessCopyWithImpl(this._self, this._then);

  final BackupResultSuccess _self;
  final $Res Function(BackupResultSuccess) _then;

/// Create a copy of BackupResult
/// with the given fields replaced by the non-null parameter values.
@pragma('vm:prefer-inline') $Res call({Object? backup = null,}) {
  return _then(BackupResultSuccess(
null == backup ? _self.backup : backup // ignore: cast_nullable_to_non_nullable
as BackupInfo,
  ));
}

/// Create a copy of BackupResult
/// with the given fields replaced by the non-null parameter values.
@override
@pragma('vm:prefer-inline')
$BackupInfoCopyWith<$Res> get backup {
  
  return $BackupInfoCopyWith<$Res>(_self.backup, (value) {
    return _then(_self.copyWith(backup: value));
  });
}
}

/// @nodoc


class BackupResultError implements BackupResult {
  const BackupResultError(this.message);
  

 final  String message;

/// Create a copy of BackupResult
/// with the given fields replaced by the non-null parameter values.
@JsonKey(includeFromJson: false, includeToJson: false)
@pragma('vm:prefer-inline')
$BackupResultErrorCopyWith<BackupResultError> get copyWith => _$BackupResultErrorCopyWithImpl<BackupResultError>(this, _$identity);



@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is BackupResultError&&(identical(other.message, message) || other.message == message));
}


@override
int get hashCode => Object.hash(runtimeType,message);

@override
String toString() {
  return 'BackupResult.error(message: $message)';
}


}

/// @nodoc
abstract mixin class $BackupResultErrorCopyWith<$Res> implements $BackupResultCopyWith<$Res> {
  factory $BackupResultErrorCopyWith(BackupResultError value, $Res Function(BackupResultError) _then) = _$BackupResultErrorCopyWithImpl;
@useResult
$Res call({
 String message
});




}
/// @nodoc
class _$BackupResultErrorCopyWithImpl<$Res>
    implements $BackupResultErrorCopyWith<$Res> {
  _$BackupResultErrorCopyWithImpl(this._self, this._then);

  final BackupResultError _self;
  final $Res Function(BackupResultError) _then;

/// Create a copy of BackupResult
/// with the given fields replaced by the non-null parameter values.
@pragma('vm:prefer-inline') $Res call({Object? message = null,}) {
  return _then(BackupResultError(
null == message ? _self.message : message // ignore: cast_nullable_to_non_nullable
as String,
  ));
}


}

/// @nodoc
mixin _$RestoreResult {





@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is RestoreResult);
}


@override
int get hashCode => runtimeType.hashCode;

@override
String toString() {
  return 'RestoreResult()';
}


}

/// @nodoc
class $RestoreResultCopyWith<$Res>  {
$RestoreResultCopyWith(RestoreResult _, $Res Function(RestoreResult) __);
}


/// Adds pattern-matching-related methods to [RestoreResult].
extension RestoreResultPatterns on RestoreResult {
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

@optionalTypeArgs TResult maybeMap<TResult extends Object?>({TResult Function( RestoreResultSuccess value)?  success,TResult Function( RestoreResultError value)?  error,required TResult orElse(),}){
final _that = this;
switch (_that) {
case RestoreResultSuccess() when success != null:
return success(_that);case RestoreResultError() when error != null:
return error(_that);case _:
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

@optionalTypeArgs TResult map<TResult extends Object?>({required TResult Function( RestoreResultSuccess value)  success,required TResult Function( RestoreResultError value)  error,}){
final _that = this;
switch (_that) {
case RestoreResultSuccess():
return success(_that);case RestoreResultError():
return error(_that);}
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

@optionalTypeArgs TResult? mapOrNull<TResult extends Object?>({TResult? Function( RestoreResultSuccess value)?  success,TResult? Function( RestoreResultError value)?  error,}){
final _that = this;
switch (_that) {
case RestoreResultSuccess() when success != null:
return success(_that);case RestoreResultError() when error != null:
return error(_that);case _:
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

@optionalTypeArgs TResult maybeWhen<TResult extends Object?>({TResult Function()?  success,TResult Function( String message)?  error,required TResult orElse(),}) {final _that = this;
switch (_that) {
case RestoreResultSuccess() when success != null:
return success();case RestoreResultError() when error != null:
return error(_that.message);case _:
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

@optionalTypeArgs TResult when<TResult extends Object?>({required TResult Function()  success,required TResult Function( String message)  error,}) {final _that = this;
switch (_that) {
case RestoreResultSuccess():
return success();case RestoreResultError():
return error(_that.message);}
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

@optionalTypeArgs TResult? whenOrNull<TResult extends Object?>({TResult? Function()?  success,TResult? Function( String message)?  error,}) {final _that = this;
switch (_that) {
case RestoreResultSuccess() when success != null:
return success();case RestoreResultError() when error != null:
return error(_that.message);case _:
  return null;

}
}

}

/// @nodoc


class RestoreResultSuccess implements RestoreResult {
  const RestoreResultSuccess();
  






@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is RestoreResultSuccess);
}


@override
int get hashCode => runtimeType.hashCode;

@override
String toString() {
  return 'RestoreResult.success()';
}


}




/// @nodoc


class RestoreResultError implements RestoreResult {
  const RestoreResultError(this.message);
  

 final  String message;

/// Create a copy of RestoreResult
/// with the given fields replaced by the non-null parameter values.
@JsonKey(includeFromJson: false, includeToJson: false)
@pragma('vm:prefer-inline')
$RestoreResultErrorCopyWith<RestoreResultError> get copyWith => _$RestoreResultErrorCopyWithImpl<RestoreResultError>(this, _$identity);



@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is RestoreResultError&&(identical(other.message, message) || other.message == message));
}


@override
int get hashCode => Object.hash(runtimeType,message);

@override
String toString() {
  return 'RestoreResult.error(message: $message)';
}


}

/// @nodoc
abstract mixin class $RestoreResultErrorCopyWith<$Res> implements $RestoreResultCopyWith<$Res> {
  factory $RestoreResultErrorCopyWith(RestoreResultError value, $Res Function(RestoreResultError) _then) = _$RestoreResultErrorCopyWithImpl;
@useResult
$Res call({
 String message
});




}
/// @nodoc
class _$RestoreResultErrorCopyWithImpl<$Res>
    implements $RestoreResultErrorCopyWith<$Res> {
  _$RestoreResultErrorCopyWithImpl(this._self, this._then);

  final RestoreResultError _self;
  final $Res Function(RestoreResultError) _then;

/// Create a copy of RestoreResult
/// with the given fields replaced by the non-null parameter values.
@pragma('vm:prefer-inline') $Res call({Object? message = null,}) {
  return _then(RestoreResultError(
null == message ? _self.message : message // ignore: cast_nullable_to_non_nullable
as String,
  ));
}


}

/// @nodoc
mixin _$BackupState {

 BackupStatus get status; DateTime? get lastBackupTime; String? get error; List<BackupInfo> get availableBackups; bool get autoBackupEnabled; BackupFrequency get frequency;
/// Create a copy of BackupState
/// with the given fields replaced by the non-null parameter values.
@JsonKey(includeFromJson: false, includeToJson: false)
@pragma('vm:prefer-inline')
$BackupStateCopyWith<BackupState> get copyWith => _$BackupStateCopyWithImpl<BackupState>(this as BackupState, _$identity);



@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is BackupState&&(identical(other.status, status) || other.status == status)&&(identical(other.lastBackupTime, lastBackupTime) || other.lastBackupTime == lastBackupTime)&&(identical(other.error, error) || other.error == error)&&const DeepCollectionEquality().equals(other.availableBackups, availableBackups)&&(identical(other.autoBackupEnabled, autoBackupEnabled) || other.autoBackupEnabled == autoBackupEnabled)&&(identical(other.frequency, frequency) || other.frequency == frequency));
}


@override
int get hashCode => Object.hash(runtimeType,status,lastBackupTime,error,const DeepCollectionEquality().hash(availableBackups),autoBackupEnabled,frequency);

@override
String toString() {
  return 'BackupState(status: $status, lastBackupTime: $lastBackupTime, error: $error, availableBackups: $availableBackups, autoBackupEnabled: $autoBackupEnabled, frequency: $frequency)';
}


}

/// @nodoc
abstract mixin class $BackupStateCopyWith<$Res>  {
  factory $BackupStateCopyWith(BackupState value, $Res Function(BackupState) _then) = _$BackupStateCopyWithImpl;
@useResult
$Res call({
 BackupStatus status, DateTime? lastBackupTime, String? error, List<BackupInfo> availableBackups, bool autoBackupEnabled, BackupFrequency frequency
});




}
/// @nodoc
class _$BackupStateCopyWithImpl<$Res>
    implements $BackupStateCopyWith<$Res> {
  _$BackupStateCopyWithImpl(this._self, this._then);

  final BackupState _self;
  final $Res Function(BackupState) _then;

/// Create a copy of BackupState
/// with the given fields replaced by the non-null parameter values.
@pragma('vm:prefer-inline') @override $Res call({Object? status = null,Object? lastBackupTime = freezed,Object? error = freezed,Object? availableBackups = null,Object? autoBackupEnabled = null,Object? frequency = null,}) {
  return _then(_self.copyWith(
status: null == status ? _self.status : status // ignore: cast_nullable_to_non_nullable
as BackupStatus,lastBackupTime: freezed == lastBackupTime ? _self.lastBackupTime : lastBackupTime // ignore: cast_nullable_to_non_nullable
as DateTime?,error: freezed == error ? _self.error : error // ignore: cast_nullable_to_non_nullable
as String?,availableBackups: null == availableBackups ? _self.availableBackups : availableBackups // ignore: cast_nullable_to_non_nullable
as List<BackupInfo>,autoBackupEnabled: null == autoBackupEnabled ? _self.autoBackupEnabled : autoBackupEnabled // ignore: cast_nullable_to_non_nullable
as bool,frequency: null == frequency ? _self.frequency : frequency // ignore: cast_nullable_to_non_nullable
as BackupFrequency,
  ));
}

}


/// Adds pattern-matching-related methods to [BackupState].
extension BackupStatePatterns on BackupState {
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

@optionalTypeArgs TResult maybeMap<TResult extends Object?>(TResult Function( _BackupState value)?  $default,{required TResult orElse(),}){
final _that = this;
switch (_that) {
case _BackupState() when $default != null:
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

@optionalTypeArgs TResult map<TResult extends Object?>(TResult Function( _BackupState value)  $default,){
final _that = this;
switch (_that) {
case _BackupState():
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

@optionalTypeArgs TResult? mapOrNull<TResult extends Object?>(TResult? Function( _BackupState value)?  $default,){
final _that = this;
switch (_that) {
case _BackupState() when $default != null:
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

@optionalTypeArgs TResult maybeWhen<TResult extends Object?>(TResult Function( BackupStatus status,  DateTime? lastBackupTime,  String? error,  List<BackupInfo> availableBackups,  bool autoBackupEnabled,  BackupFrequency frequency)?  $default,{required TResult orElse(),}) {final _that = this;
switch (_that) {
case _BackupState() when $default != null:
return $default(_that.status,_that.lastBackupTime,_that.error,_that.availableBackups,_that.autoBackupEnabled,_that.frequency);case _:
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

@optionalTypeArgs TResult when<TResult extends Object?>(TResult Function( BackupStatus status,  DateTime? lastBackupTime,  String? error,  List<BackupInfo> availableBackups,  bool autoBackupEnabled,  BackupFrequency frequency)  $default,) {final _that = this;
switch (_that) {
case _BackupState():
return $default(_that.status,_that.lastBackupTime,_that.error,_that.availableBackups,_that.autoBackupEnabled,_that.frequency);}
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

@optionalTypeArgs TResult? whenOrNull<TResult extends Object?>(TResult? Function( BackupStatus status,  DateTime? lastBackupTime,  String? error,  List<BackupInfo> availableBackups,  bool autoBackupEnabled,  BackupFrequency frequency)?  $default,) {final _that = this;
switch (_that) {
case _BackupState() when $default != null:
return $default(_that.status,_that.lastBackupTime,_that.error,_that.availableBackups,_that.autoBackupEnabled,_that.frequency);case _:
  return null;

}
}

}

/// @nodoc


class _BackupState implements BackupState {
  const _BackupState({this.status = BackupStatus.idle, this.lastBackupTime, this.error, final  List<BackupInfo> availableBackups = const [], this.autoBackupEnabled = true, this.frequency = BackupFrequency.daily}): _availableBackups = availableBackups;
  

@override@JsonKey() final  BackupStatus status;
@override final  DateTime? lastBackupTime;
@override final  String? error;
 final  List<BackupInfo> _availableBackups;
@override@JsonKey() List<BackupInfo> get availableBackups {
  if (_availableBackups is EqualUnmodifiableListView) return _availableBackups;
  // ignore: implicit_dynamic_type
  return EqualUnmodifiableListView(_availableBackups);
}

@override@JsonKey() final  bool autoBackupEnabled;
@override@JsonKey() final  BackupFrequency frequency;

/// Create a copy of BackupState
/// with the given fields replaced by the non-null parameter values.
@override @JsonKey(includeFromJson: false, includeToJson: false)
@pragma('vm:prefer-inline')
_$BackupStateCopyWith<_BackupState> get copyWith => __$BackupStateCopyWithImpl<_BackupState>(this, _$identity);



@override
bool operator ==(Object other) {
  return identical(this, other) || (other.runtimeType == runtimeType&&other is _BackupState&&(identical(other.status, status) || other.status == status)&&(identical(other.lastBackupTime, lastBackupTime) || other.lastBackupTime == lastBackupTime)&&(identical(other.error, error) || other.error == error)&&const DeepCollectionEquality().equals(other._availableBackups, _availableBackups)&&(identical(other.autoBackupEnabled, autoBackupEnabled) || other.autoBackupEnabled == autoBackupEnabled)&&(identical(other.frequency, frequency) || other.frequency == frequency));
}


@override
int get hashCode => Object.hash(runtimeType,status,lastBackupTime,error,const DeepCollectionEquality().hash(_availableBackups),autoBackupEnabled,frequency);

@override
String toString() {
  return 'BackupState(status: $status, lastBackupTime: $lastBackupTime, error: $error, availableBackups: $availableBackups, autoBackupEnabled: $autoBackupEnabled, frequency: $frequency)';
}


}

/// @nodoc
abstract mixin class _$BackupStateCopyWith<$Res> implements $BackupStateCopyWith<$Res> {
  factory _$BackupStateCopyWith(_BackupState value, $Res Function(_BackupState) _then) = __$BackupStateCopyWithImpl;
@override @useResult
$Res call({
 BackupStatus status, DateTime? lastBackupTime, String? error, List<BackupInfo> availableBackups, bool autoBackupEnabled, BackupFrequency frequency
});




}
/// @nodoc
class __$BackupStateCopyWithImpl<$Res>
    implements _$BackupStateCopyWith<$Res> {
  __$BackupStateCopyWithImpl(this._self, this._then);

  final _BackupState _self;
  final $Res Function(_BackupState) _then;

/// Create a copy of BackupState
/// with the given fields replaced by the non-null parameter values.
@override @pragma('vm:prefer-inline') $Res call({Object? status = null,Object? lastBackupTime = freezed,Object? error = freezed,Object? availableBackups = null,Object? autoBackupEnabled = null,Object? frequency = null,}) {
  return _then(_BackupState(
status: null == status ? _self.status : status // ignore: cast_nullable_to_non_nullable
as BackupStatus,lastBackupTime: freezed == lastBackupTime ? _self.lastBackupTime : lastBackupTime // ignore: cast_nullable_to_non_nullable
as DateTime?,error: freezed == error ? _self.error : error // ignore: cast_nullable_to_non_nullable
as String?,availableBackups: null == availableBackups ? _self._availableBackups : availableBackups // ignore: cast_nullable_to_non_nullable
as List<BackupInfo>,autoBackupEnabled: null == autoBackupEnabled ? _self.autoBackupEnabled : autoBackupEnabled // ignore: cast_nullable_to_non_nullable
as bool,frequency: null == frequency ? _self.frequency : frequency // ignore: cast_nullable_to_non_nullable
as BackupFrequency,
  ));
}


}

// dart format on
