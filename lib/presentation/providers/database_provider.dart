import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../data/datasources/local/database/database.dart';

/// Provider for the local database
final databaseProvider = Provider<AppDatabase>((ref) {
  final db = AppDatabase();
  ref.onDispose(() => db.close());
  return db;
});
