import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:package_info_plus/package_info_plus.dart';

/// Provider for app package info (version, build number, etc.)
final appInfoProvider = FutureProvider<PackageInfo>((ref) async {
  return await PackageInfo.fromPlatform();
});

/// Convenience provider for just the version string
final appVersionProvider = Provider<String>((ref) {
  final asyncValue = ref.watch(appInfoProvider);
  return asyncValue.when(
    data: (info) => info.version,
    loading: () => '...',
    error: (e, s) => 'Unknown',
  );
});

/// Convenience provider for version with build number
final appFullVersionProvider = Provider<String>((ref) {
  final asyncValue = ref.watch(appInfoProvider);
  return asyncValue.when(
    data: (info) => '${info.version}+${info.buildNumber}',
    loading: () => '...',
    error: (e, s) => 'Unknown',
  );
});
