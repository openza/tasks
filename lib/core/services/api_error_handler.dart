import 'dart:async';
import 'dart:io';

import 'package:dio/dio.dart';

import '../utils/logger.dart';

/// API error types for categorizing errors
enum ApiErrorType {
  network,
  timeout,
  unauthorized,
  forbidden,
  notFound,
  rateLimit,
  serverError,
  unknown,
}

/// Structured API error
class ApiError {
  final ApiErrorType type;
  final String message;
  final String? details;
  final int? statusCode;
  final dynamic originalError;

  const ApiError({
    required this.type,
    required this.message,
    this.details,
    this.statusCode,
    this.originalError,
  });

  @override
  String toString() => 'ApiError($type): $message';
}

/// Stream controller for API errors (for global error handling)
class ApiErrorStream {
  static final StreamController<ApiError> _controller =
      StreamController<ApiError>.broadcast();

  static Stream<ApiError> get stream => _controller.stream;

  static void add(ApiError error) {
    _controller.add(error);
  }

  static void dispose() {
    _controller.close();
  }
}

/// API Error Handler - parses errors into user-friendly messages
class ApiErrorHandler {
  static ApiError handleDioError(DioException error, {String? context}) {
    final prefix = context != null ? '$context: ' : '';

    switch (error.type) {
      case DioExceptionType.connectionTimeout:
      case DioExceptionType.sendTimeout:
      case DioExceptionType.receiveTimeout:
        return ApiError(
          type: ApiErrorType.timeout,
          message: '${prefix}Connection timed out',
          details: 'Please check your internet connection and try again.',
          originalError: error,
        );

      case DioExceptionType.connectionError:
        return ApiError(
          type: ApiErrorType.network,
          message: '${prefix}No internet connection',
          details: 'Please check your network settings.',
          originalError: error,
        );

      case DioExceptionType.badResponse:
        return _handleHttpError(error.response?.statusCode, error, prefix);

      case DioExceptionType.cancel:
        return ApiError(
          type: ApiErrorType.unknown,
          message: '${prefix}Request cancelled',
          originalError: error,
        );

      default:
        // Check for socket exceptions
        if (error.error is SocketException) {
          return ApiError(
            type: ApiErrorType.network,
            message: '${prefix}Network error',
            details: 'Unable to reach the server. Please check your connection.',
            originalError: error,
          );
        }

        return ApiError(
          type: ApiErrorType.unknown,
          message: '${prefix}An unexpected error occurred',
          details: error.message,
          originalError: error,
        );
    }
  }

  static ApiError _handleHttpError(int? statusCode, DioException error, String prefix) {
    switch (statusCode) {
      case 400:
        return ApiError(
          type: ApiErrorType.unknown,
          message: '${prefix}Invalid request',
          details: _extractErrorMessage(error.response?.data),
          statusCode: statusCode,
          originalError: error,
        );

      case 401:
        return ApiError(
          type: ApiErrorType.unauthorized,
          message: '${prefix}Authentication required',
          details: 'Please sign in again to continue.',
          statusCode: statusCode,
          originalError: error,
        );

      case 403:
        return ApiError(
          type: ApiErrorType.forbidden,
          message: '${prefix}Access denied',
          details: 'You don\'t have permission to perform this action.',
          statusCode: statusCode,
          originalError: error,
        );

      case 404:
        return ApiError(
          type: ApiErrorType.notFound,
          message: '${prefix}Not found',
          details: 'The requested item could not be found.',
          statusCode: statusCode,
          originalError: error,
        );

      case 429:
        return ApiError(
          type: ApiErrorType.rateLimit,
          message: '${prefix}Too many requests',
          details: 'Please wait a moment and try again.',
          statusCode: statusCode,
          originalError: error,
        );

      case 500:
      case 502:
      case 503:
      case 504:
        return ApiError(
          type: ApiErrorType.serverError,
          message: '${prefix}Server error',
          details: 'The service is temporarily unavailable. Please try again later.',
          statusCode: statusCode,
          originalError: error,
        );

      default:
        return ApiError(
          type: ApiErrorType.unknown,
          message: '${prefix}Request failed',
          details: _extractErrorMessage(error.response?.data) ?? 'Status code: $statusCode',
          statusCode: statusCode,
          originalError: error,
        );
    }
  }

  static String? _extractErrorMessage(dynamic data) {
    if (data == null) return null;

    if (data is String) return data;

    if (data is Map) {
      // Common error message field names
      final message = data['message'] ??
          data['error'] ??
          data['error_description'] ??
          data['detail'] ??
          data['errors']?.toString();

      if (message != null) return message.toString();
    }

    return null;
  }

  /// Handle any error and convert to ApiError
  static ApiError handleError(dynamic error, {String? context}) {
    if (error is DioException) {
      return handleDioError(error, context: context);
    }

    if (error is SocketException) {
      return ApiError(
        type: ApiErrorType.network,
        message: context != null ? '$context: Network error' : 'Network error',
        details: 'Unable to connect to the server.',
        originalError: error,
      );
    }

    if (error is TimeoutException) {
      return ApiError(
        type: ApiErrorType.timeout,
        message: context != null ? '$context: Request timed out' : 'Request timed out',
        details: 'The operation took too long to complete.',
        originalError: error,
      );
    }

    return ApiError(
      type: ApiErrorType.unknown,
      message: context != null ? '$context: Error' : 'Error',
      details: error.toString(),
      originalError: error,
    );
  }

  /// Log and emit an API error
  static void reportError(ApiError error) {
    AppLogger.error('API Error: ${error.message}', error.originalError);
    ApiErrorStream.add(error);
  }
}
