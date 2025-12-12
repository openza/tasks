import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'package:openza_flutter/app/app.dart';

void main() {
  testWidgets('App renders without errors', (WidgetTester tester) async {
    // Build our app and trigger a frame.
    await tester.pumpWidget(
      const ProviderScope(
        child: OpenzaApp(),
      ),
    );

    // Verify that Openza title appears
    expect(find.text('Openza'), findsWidgets);
  });
}
