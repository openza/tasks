import 'dart:async';

import 'package:flutter/material.dart';
import 'package:toastification/toastification.dart';

import '../../../core/services/api_error_handler.dart';

/// Widget that listens to API errors and shows toast notifications
class ApiErrorListener extends StatefulWidget {
  final Widget child;

  const ApiErrorListener({super.key, required this.child});

  @override
  State<ApiErrorListener> createState() => _ApiErrorListenerState();
}

class _ApiErrorListenerState extends State<ApiErrorListener> {
  StreamSubscription<ApiError>? _subscription;

  @override
  void initState() {
    super.initState();
    _subscription = ApiErrorStream.stream.listen(_handleError);
  }

  @override
  void dispose() {
    _subscription?.cancel();
    super.dispose();
  }

  void _handleError(ApiError error) {
    if (!mounted) return;

    ToastificationType type;
    IconData icon;

    switch (error.type) {
      case ApiErrorType.network:
        type = ToastificationType.warning;
        icon = Icons.wifi_off;
        break;
      case ApiErrorType.timeout:
        type = ToastificationType.warning;
        icon = Icons.timer_off;
        break;
      case ApiErrorType.unauthorized:
        type = ToastificationType.error;
        icon = Icons.lock_outline;
        break;
      case ApiErrorType.forbidden:
        type = ToastificationType.error;
        icon = Icons.block;
        break;
      case ApiErrorType.rateLimit:
        type = ToastificationType.warning;
        icon = Icons.speed;
        break;
      case ApiErrorType.serverError:
        type = ToastificationType.error;
        icon = Icons.cloud_off;
        break;
      default:
        type = ToastificationType.error;
        icon = Icons.error_outline;
    }

    toastification.show(
      context: context,
      type: type,
      style: ToastificationStyle.fillColored,
      title: Text(error.message),
      description: error.details != null ? Text(error.details!) : null,
      autoCloseDuration: const Duration(seconds: 5),
      icon: Icon(icon),
      showProgressBar: true,
    );
  }

  @override
  Widget build(BuildContext context) {
    return widget.child;
  }
}
