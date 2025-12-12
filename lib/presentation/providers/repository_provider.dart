import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../data/repositories/task_repository.dart';
import '../../data/datasources/remote/auth/oauth_service.dart';
import 'database_provider.dart';
import 'task_provider.dart';

/// Provider for OAuth service
final oAuthServiceProvider = Provider<OAuthService>((ref) {
  return OAuthService();
});

/// Provider for task repository
final taskRepositoryProvider = FutureProvider<TaskRepository>((ref) async {
  final database = ref.watch(databaseProvider);
  final todoistApi = await ref.watch(todoistApiProvider.future);
  final msToDoApi = await ref.watch(msToDoApiProvider.future);

  return TaskRepository(
    database: database,
    todoistApi: todoistApi,
    msToDoApi: msToDoApi,
  );
});
