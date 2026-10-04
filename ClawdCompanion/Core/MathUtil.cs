using System;

namespace ClawdCompanion.Core
{
    /// <summary>Tiny 2D vector in screen space (DIPs, y points down).</summary>
    public struct V2
    {
        public double X;
        public double Y;

        public V2(double x, double y) { X = x; Y = y; }

        public static readonly V2 Zero = new V2(0, 0);

        public static V2 operator +(V2 a, V2 b) { return new V2(a.X + b.X, a.Y + b.Y); }
        public static V2 operator -(V2 a, V2 b) { return new V2(a.X - b.X, a.Y - b.Y); }
        public static V2 operator -(V2 a) { return new V2(-a.X, -a.Y); }
        public static V2 operator *(V2 a, double s) { return new V2(a.X * s, a.Y * s); }
        public static V2 operator *(double s, V2 a) { return new V2(a.X * s, a.Y * s); }
        public static V2 operator /(V2 a, double s) { return new V2(a.X / s, a.Y / s); }

        public double Length { get { return Math.Sqrt(X * X + Y * Y); } }

        public V2 Normalized()
        {
            double l = Length;
            return l > 1e-9 ? new V2(X / l, Y / l) : new V2(0, 0);
        }

        /// <summary>Rotates by angle (radians). In y-down screen space a positive angle turns clockwise.</summary>
        public V2 Rotate(double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return new V2(X * c - Y * s, X * s + Y * c);
        }

        public static V2 Lerp(V2 a, V2 b, double t) { return new V2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t); }
    }

    public static class M
    {
        public static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static double Clamp01(double v) { return Clamp(v, 0, 1); }
        public static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

        /// <summary>Frame-rate independent exponential smoothing.</summary>
        public static double Damp(double current, double target, double rate, double dt)
        {
            return target + (current - target) * Math.Exp(-rate * dt);
        }

        public static V2 Damp(V2 current, V2 target, double rate, double dt)
        {
            double f = Math.Exp(-rate * dt);
            return new V2(target.X + (current.X - target.X) * f, target.Y + (current.Y - target.Y) * f);
        }

        /// <summary>Wraps an angle into [-PI, PI].</summary>
        public static double WrapAngle(double a) { return Math.IEEERemainder(a, 2 * Math.PI); }

        public static double SmoothStep(double t) { t = Clamp01(t); return t * t * (3 - 2 * t); }
        public static double Deg(double rad) { return rad * (180.0 / Math.PI); }
        public static double Rad(double deg) { return deg * (Math.PI / 180.0); }
    }
}
