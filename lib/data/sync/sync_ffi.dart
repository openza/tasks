import 'dart:ffi';
import 'dart:io';

import 'package:ffi/ffi.dart';

/// FFI bindings for the Rust sync engine
class SyncFfi {
  late final DynamicLibrary _lib;
  bool _initialized = false;

  // Function pointers
  late final void Function(Pointer<Utf8>) _freeRustString;

  // Singleton
  static final SyncFfi _instance = SyncFfi._internal();
  factory SyncFfi() => _instance;
  SyncFfi._internal();

  /// Initialize the FFI library
  void initialize() {
    if (_initialized) return;

    String libraryPath;
    if (Platform.isLinux) {
      libraryPath = 'libopenza_sync.so';
    } else if (Platform.isMacOS) {
      libraryPath = 'libopenza_sync.dylib';
    } else if (Platform.isWindows) {
      libraryPath = 'openza_sync.dll';
    } else {
      throw UnsupportedError('Unsupported platform: ${Platform.operatingSystem}');
    }

    _lib = DynamicLibrary.open(libraryPath);

    // Load the free function
    _freeRustString = _lib.lookupFunction<
        Void Function(Pointer<Utf8>),
        void Function(Pointer<Utf8>)>('free_rust_string');

    _initialized = true;
  }

  /// Free a string allocated by Rust
  void _freeResult(Pointer<Utf8> ptr) {
    if (ptr != nullptr) {
      _freeRustString(ptr);
    }
  }

  /// Perform initial sync (clear and re-sync)
  String initialSync({
    required String dbPath,
    required String provider,
    required String tasksJson,
    required String projectsJson,
    required String labelsJson,
  }) {
    initialize();

    final func = _lib.lookupFunction<
        Pointer<Utf8> Function(
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
        ),
        Pointer<Utf8> Function(
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
        )>('initial_sync');

    final dbPathPtr = dbPath.toNativeUtf8();
    final providerPtr = provider.toNativeUtf8();
    final tasksPtr = tasksJson.toNativeUtf8();
    final projectsPtr = projectsJson.toNativeUtf8();
    final labelsPtr = labelsJson.toNativeUtf8();

    try {
      final resultPtr = func(dbPathPtr, providerPtr, tasksPtr, projectsPtr, labelsPtr);
      final result = resultPtr.toDartString();
      _freeResult(resultPtr);
      return result;
    } finally {
      calloc.free(dbPathPtr);
      calloc.free(providerPtr);
      calloc.free(tasksPtr);
      calloc.free(projectsPtr);
      calloc.free(labelsPtr);
    }
  }

  /// Perform incremental sync
  String incrementalSync({
    required String dbPath,
    required String provider,
    required String tasksJson,
    required String projectsJson,
    required String labelsJson,
    String? syncToken,
  }) {
    initialize();

    final func = _lib.lookupFunction<
        Pointer<Utf8> Function(
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
        ),
        Pointer<Utf8> Function(
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
          Pointer<Utf8>,
        )>('incremental_sync');

    final dbPathPtr = dbPath.toNativeUtf8();
    final providerPtr = provider.toNativeUtf8();
    final tasksPtr = tasksJson.toNativeUtf8();
    final projectsPtr = projectsJson.toNativeUtf8();
    final labelsPtr = labelsJson.toNativeUtf8();
    final tokenPtr = (syncToken ?? '').toNativeUtf8();

    try {
      final resultPtr = func(dbPathPtr, providerPtr, tasksPtr, projectsPtr, labelsPtr, tokenPtr);
      final result = resultPtr.toDartString();
      _freeResult(resultPtr);
      return result;
    } finally {
      calloc.free(dbPathPtr);
      calloc.free(providerPtr);
      calloc.free(tasksPtr);
      calloc.free(projectsPtr);
      calloc.free(labelsPtr);
      calloc.free(tokenPtr);
    }
  }

  /// Get pending completions
  String getPendingCompletions({
    required String dbPath,
    required String provider,
  }) {
    initialize();

    final func = _lib.lookupFunction<
        Pointer<Utf8> Function(Pointer<Utf8>, Pointer<Utf8>),
        Pointer<Utf8> Function(Pointer<Utf8>, Pointer<Utf8>)>('get_pending_completions');

    final dbPathPtr = dbPath.toNativeUtf8();
    final providerPtr = provider.toNativeUtf8();

    try {
      final resultPtr = func(dbPathPtr, providerPtr);
      final result = resultPtr.toDartString();
      _freeResult(resultPtr);
      return result;
    } finally {
      calloc.free(dbPathPtr);
      calloc.free(providerPtr);
    }
  }

  /// Mark completion as synced
  bool markCompletionSynced({
    required String dbPath,
    required String completionId,
  }) {
    initialize();

    final func = _lib.lookupFunction<
        Bool Function(Pointer<Utf8>, Pointer<Utf8>),
        bool Function(Pointer<Utf8>, Pointer<Utf8>)>('mark_completion_synced');

    final dbPathPtr = dbPath.toNativeUtf8();
    final completionIdPtr = completionId.toNativeUtf8();

    try {
      return func(dbPathPtr, completionIdPtr);
    } finally {
      calloc.free(dbPathPtr);
      calloc.free(completionIdPtr);
    }
  }

  /// Queue a completion for sync
  bool queueCompletion({
    required String dbPath,
    required String taskId,
    required String provider,
    required String providerTaskId,
    required bool completed,
  }) {
    initialize();

    final func = _lib.lookupFunction<
        Bool Function(Pointer<Utf8>, Pointer<Utf8>, Pointer<Utf8>, Pointer<Utf8>, Bool),
        bool Function(Pointer<Utf8>, Pointer<Utf8>, Pointer<Utf8>, Pointer<Utf8>, bool)>('queue_completion');

    final dbPathPtr = dbPath.toNativeUtf8();
    final taskIdPtr = taskId.toNativeUtf8();
    final providerPtr = provider.toNativeUtf8();
    final providerTaskIdPtr = providerTaskId.toNativeUtf8();

    try {
      return func(dbPathPtr, taskIdPtr, providerPtr, providerTaskIdPtr, completed);
    } finally {
      calloc.free(dbPathPtr);
      calloc.free(taskIdPtr);
      calloc.free(providerPtr);
      calloc.free(providerTaskIdPtr);
    }
  }

  /// Clear provider data
  String clearProviderData({
    required String dbPath,
    required String provider,
  }) {
    initialize();

    final func = _lib.lookupFunction<
        Pointer<Utf8> Function(Pointer<Utf8>, Pointer<Utf8>),
        Pointer<Utf8> Function(Pointer<Utf8>, Pointer<Utf8>)>('clear_provider_data');

    final dbPathPtr = dbPath.toNativeUtf8();
    final providerPtr = provider.toNativeUtf8();

    try {
      final resultPtr = func(dbPathPtr, providerPtr);
      final result = resultPtr.toDartString();
      _freeResult(resultPtr);
      return result;
    } finally {
      calloc.free(dbPathPtr);
      calloc.free(providerPtr);
    }
  }

  /// Update sync token
  bool updateSyncToken({
    required String dbPath,
    required String provider,
    required String syncToken,
  }) {
    initialize();

    final func = _lib.lookupFunction<
        Bool Function(Pointer<Utf8>, Pointer<Utf8>, Pointer<Utf8>),
        bool Function(Pointer<Utf8>, Pointer<Utf8>, Pointer<Utf8>)>('update_sync_token');

    final dbPathPtr = dbPath.toNativeUtf8();
    final providerPtr = provider.toNativeUtf8();
    final tokenPtr = syncToken.toNativeUtf8();

    try {
      return func(dbPathPtr, providerPtr, tokenPtr);
    } finally {
      calloc.free(dbPathPtr);
      calloc.free(providerPtr);
      calloc.free(tokenPtr);
    }
  }

  /// Get sync token (returns null if not found)
  String? getSyncToken({
    required String dbPath,
    required String provider,
  }) {
    initialize();

    final func = _lib.lookupFunction<
        Pointer<Utf8> Function(Pointer<Utf8>, Pointer<Utf8>),
        Pointer<Utf8> Function(Pointer<Utf8>, Pointer<Utf8>)>('get_sync_token');

    final dbPathPtr = dbPath.toNativeUtf8();
    final providerPtr = provider.toNativeUtf8();

    try {
      final resultPtr = func(dbPathPtr, providerPtr);
      if (resultPtr == nullptr) {
        return null;
      }
      final result = resultPtr.toDartString();
      _freeResult(resultPtr);
      return result;
    } finally {
      calloc.free(dbPathPtr);
      calloc.free(providerPtr);
    }
  }
}
