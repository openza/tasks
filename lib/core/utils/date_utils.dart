import 'package:intl/intl.dart';

/// Date utility functions matching the Electron app's dateUtils.ts
class AppDateUtils {
  AppDateUtils._();

  static final DateFormat _dateFormat = DateFormat('yyyy-MM-dd');
  static final DateFormat _dateTimeFormat = DateFormat('yyyy-MM-dd HH:mm:ss');
  static final DateFormat _displayDateFormat = DateFormat('MMM d, yyyy');
  static final DateFormat _displayTimeFormat = DateFormat('h:mm a');
  static final DateFormat _displayDateTimeFormat = DateFormat('MMM d, yyyy h:mm a');

  /// Check if a date is today
  static bool isToday(DateTime? date) {
    if (date == null) return false;
    final now = DateTime.now();
    return date.year == now.year &&
        date.month == now.month &&
        date.day == now.day;
  }

  /// Check if a date is tomorrow
  static bool isTomorrow(DateTime? date) {
    if (date == null) return false;
    final tomorrow = DateTime.now().add(const Duration(days: 1));
    return date.year == tomorrow.year &&
        date.month == tomorrow.month &&
        date.day == tomorrow.day;
  }

  /// Check if a date is yesterday
  static bool isYesterday(DateTime? date) {
    if (date == null) return false;
    final yesterday = DateTime.now().subtract(const Duration(days: 1));
    return date.year == yesterday.year &&
        date.month == yesterday.month &&
        date.day == yesterday.day;
  }

  /// Check if a date is overdue (before today)
  static bool isOverdue(DateTime? date) {
    if (date == null) return false;
    final today = DateTime.now();
    final todayStart = DateTime(today.year, today.month, today.day);
    return date.isBefore(todayStart);
  }

  /// Check if a date is within the next N days
  static bool isWithinDays(DateTime? date, int days) {
    if (date == null) return false;
    final now = DateTime.now();
    final futureDate = now.add(Duration(days: days));
    return date.isAfter(now) && date.isBefore(futureDate);
  }

  /// Get relative date string (Today, Tomorrow, Yesterday, or formatted date)
  static String getRelativeDateString(DateTime? date) {
    if (date == null) return '';
    if (isToday(date)) return 'Today';
    if (isTomorrow(date)) return 'Tomorrow';
    if (isYesterday(date)) return 'Yesterday';
    return _displayDateFormat.format(date);
  }

  /// Get days overdue
  static int getDaysOverdue(DateTime? date) {
    if (date == null || !isOverdue(date)) return 0;
    final today = DateTime.now();
    final todayStart = DateTime(today.year, today.month, today.day);
    return todayStart.difference(date).inDays;
  }

  /// Parse ISO date string
  static DateTime? parseDate(String? dateString) {
    if (dateString == null || dateString.isEmpty) return null;
    try {
      return DateTime.parse(dateString);
    } catch (_) {
      return null;
    }
  }

  /// Format date to ISO string
  static String? formatToIso(DateTime? date) {
    if (date == null) return null;
    return _dateFormat.format(date);
  }

  /// Format date for display
  static String formatForDisplay(DateTime? date) {
    if (date == null) return '';
    return _displayDateFormat.format(date);
  }

  /// Format time for display
  static String formatTimeForDisplay(DateTime? date) {
    if (date == null) return '';
    return _displayTimeFormat.format(date);
  }

  /// Format date and time for display
  static String formatDateTimeForDisplay(DateTime? date) {
    if (date == null) return '';
    return _displayDateTimeFormat.format(date);
  }

  /// Get start of day
  static DateTime startOfDay(DateTime date) {
    return DateTime(date.year, date.month, date.day);
  }

  /// Get end of day
  static DateTime endOfDay(DateTime date) {
    return DateTime(date.year, date.month, date.day, 23, 59, 59);
  }

  /// Get start of today
  static DateTime get todayStart => startOfDay(DateTime.now());

  /// Get end of today
  static DateTime get todayEnd => endOfDay(DateTime.now());
}
