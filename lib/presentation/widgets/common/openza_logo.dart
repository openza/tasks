import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';

/// Openza logo widget - lightning bolt in concentric circles
class OpenzaLogo extends StatelessWidget {
  final double size;
  final bool showText;
  final double? textSpacing;

  const OpenzaLogo({
    super.key,
    this.size = 32,
    this.showText = false,
    this.textSpacing,
  });

  @override
  Widget build(BuildContext context) {
    if (showText) {
      return Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          _buildIcon(),
          SizedBox(width: textSpacing ?? 12),
          Text(
            'Openza Tasks',
            style: Theme.of(context).textTheme.titleLarge?.copyWith(
                  fontWeight: FontWeight.w700,
                  color: const Color(0xFF1e293b),
                ),
          ),
        ],
      );
    }
    return _buildIcon();
  }

  Widget _buildIcon() {
    // Try to load SVG first, fallback to custom painted version
    return SizedBox(
      width: size,
      height: size,
      child: SvgPicture.asset(
        'assets/icons/icon.svg',
        width: size,
        height: size,
        placeholderBuilder: (context) => _OpenzaIconPainter(size: size),
      ),
    );
  }
}

/// Custom painted version of the Openza icon (fallback)
class _OpenzaIconPainter extends StatelessWidget {
  final double size;

  const _OpenzaIconPainter({required this.size});

  @override
  Widget build(BuildContext context) {
    return CustomPaint(
      size: Size(size, size),
      painter: _OpenzaLogoPainter(),
    );
  }
}

class _OpenzaLogoPainter extends CustomPainter {
  @override
  void paint(Canvas canvas, Size size) {
    final center = Offset(size.width / 2, size.height / 2);
    final outerRadius = size.width / 2 * 0.833; // 20/24 ratio from SVG
    final middleRadius = size.width / 2 * 0.667; // 16/24 ratio
    final innerRadius = size.width / 2 * 0.5; // 12/24 ratio

    // Outer circle - Charcoal (#1e293b)
    final outerPaint = Paint()
      ..color = const Color(0xFF1e293b)
      ..style = PaintingStyle.fill;
    canvas.drawCircle(center, outerRadius, outerPaint);

    // Middle ring - White
    final middlePaint = Paint()
      ..color = Colors.white
      ..style = PaintingStyle.fill;
    canvas.drawCircle(center, middleRadius, middlePaint);

    // Inner circle - Slate (#475569)
    final innerPaint = Paint()
      ..color = const Color(0xFF475569)
      ..style = PaintingStyle.fill;
    canvas.drawCircle(center, innerRadius, innerPaint);

    // Lightning bolt - White
    // Original SVG path: M 26 18 L 20 25 L 24 25 L 22 30 L 28 23 L 24 23 Z
    // Scale from 48x48 to actual size
    final scale = size.width / 48;
    final boltPath = Path()
      ..moveTo(26 * scale, 18 * scale)
      ..lineTo(20 * scale, 25 * scale)
      ..lineTo(24 * scale, 25 * scale)
      ..lineTo(22 * scale, 30 * scale)
      ..lineTo(28 * scale, 23 * scale)
      ..lineTo(24 * scale, 23 * scale)
      ..close();

    final boltPaint = Paint()
      ..color = Colors.white
      ..style = PaintingStyle.fill;
    canvas.drawPath(boltPath, boltPaint);
  }

  @override
  bool shouldRepaint(covariant CustomPainter oldDelegate) => false;
}

/// Openza icon only (without text)
class OpenzaIcon extends StatelessWidget {
  final double size;

  const OpenzaIcon({super.key, this.size = 32});

  @override
  Widget build(BuildContext context) {
    return OpenzaLogo(size: size, showText: false);
  }
}
