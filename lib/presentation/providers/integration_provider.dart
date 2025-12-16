import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../domain/entities/integration.dart';
import 'database_provider.dart';

/// Provider for all integrations from database
final integrationsProvider = FutureProvider<List<IntegrationEntity>>((ref) async {
  ref.keepAlive();
  final db = ref.watch(databaseProvider);
  final rows = await db.getAllIntegrations();

  return rows.map((r) => IntegrationEntity.fromDb(
    id: r.id,
    name: r.name,
    displayName: r.displayName,
    color: r.color,
    icon: r.icon,
    logoPath: r.logoPath,
    isActive: r.isActive,
    isConfigured: r.isConfigured,
    lastSyncAt: r.lastSyncAt,
    syncToken: r.syncToken,
    createdAt: r.createdAt,
  )).toList();
});

/// Provider for configured integrations
final configuredIntegrationsProvider = FutureProvider<List<IntegrationEntity>>((ref) async {
  final integrations = await ref.watch(integrationsProvider.future);
  return integrations.where((i) => i.isConfigured).toList();
});

/// Provider for active integrations
final activeIntegrationsProvider = FutureProvider<List<IntegrationEntity>>((ref) async {
  final integrations = await ref.watch(integrationsProvider.future);
  return integrations.where((i) => i.isActive).toList();
});

/// Provider for integration metadata map (by ID)
final integrationMetadataProvider = Provider<Map<String, IntegrationEntity>>((ref) {
  final integrations = ref.watch(integrationsProvider).value ?? [];
  return {for (var i in integrations) i.id: i};
});

/// Provider for a specific integration by ID
final integrationByIdProvider = Provider.family<IntegrationEntity?, String>((ref, id) {
  final integrations = ref.watch(integrationMetadataProvider);
  return integrations[id];
});

/// Provider for external (non-native) integrations
final externalIntegrationsProvider = FutureProvider<List<IntegrationEntity>>((ref) async {
  final integrations = await ref.watch(integrationsProvider.future);
  return integrations.where((i) => i.isExternal).toList();
});
