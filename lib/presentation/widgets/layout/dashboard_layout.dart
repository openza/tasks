import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../common/api_error_listener.dart';
import 'nav_rail.dart';
import 'projects_pane.dart';

/// Dashboard layout with 4-pane structure:
/// NavRail (160px) | ProjectsPane (260px) | Main Content (flexible) | Task Details (when open)
///
/// This layout treats projects as first-class citizens following GTD principles.
class DashboardLayout extends ConsumerWidget {
  final Widget child;

  const DashboardLayout({super.key, required this.child});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return ApiErrorListener(
      child: Scaffold(
        body: Row(
          children: [
            // Navigation Rail (160px)
            const NavRail(),

            // Projects Pane (260px)
            const ProjectsPane(),

            // Main Content (flexible)
            Expanded(
              child: child,
            ),
          ],
        ),
      ),
    );
  }
}
