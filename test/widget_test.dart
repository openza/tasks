import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'package:openza_tasks/app/app.dart';

void main() {
  testWidgets('App renders without errors', (WidgetTester tester) async {
    // Build our app and trigger a frame.
    await tester.pumpWidget(
      const ProviderScope(
        child: OpenzaApp(),
      ),
    );

    // App should render a MaterialApp (either splash or main app)
    // The initial state shows splash with CircularProgressIndicator
    // while unifiedDataProvider loads
    expect(find.byType(MaterialApp), findsOneWidget);

    // Pump a few frames to allow initial render
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 100));

    // Should still have MaterialApp rendered (either state is valid)
    expect(find.byType(MaterialApp), findsOneWidget);
  });
}
