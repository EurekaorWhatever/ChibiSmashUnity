using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Deterministic integer math helpers for simulation code (no float anywhere). Ratios are permille
    /// (1000 = 100%), distances are milli-units (1000 = one world unit). Pure and thread-safe.
    /// </summary>
    public static class ChibiFixed
    {
        /// <summary>One whole in permille.</summary>
        public const int Permille = 1000;

        /// <summary>v * permille / 1000, rounded half away from zero. Computed in 64 bits, truncated to int.</summary>
        /// <example><code>ChibiFixed.MulPermille(3, 500) == 2;  ChibiFixed.MulPermille(-3, 500) == -2;</code></example>
        public static int MulPermille(int v, int permille)
        {
            long p = (long)v * permille;
            return (int)(p >= 0 ? (p + Permille / 2) / Permille : -((-p + Permille / 2) / Permille));
        }

        /// <summary>a / b rounded half away from zero. Throws DivideByZeroException when b == 0.</summary>
        /// <example><code>ChibiFixed.DivRound(7, 2) == 4;  ChibiFixed.DivRound(-7, 2) == -4;</code></example>
        public static int DivRound(int a, int b)
        {
            if (b == 0) throw new DivideByZeroException();
            long la = a < 0 ? -(long)a : a;
            long lb = b < 0 ? -(long)b : b;
            long q = (2 * la + lb) / (2 * lb);
            return (int)(((a < 0) != (b < 0)) ? -q : q);
        }

        /// <summary>Clamp v into [min, max].</summary>
        /// <example><code>ChibiFixed.Clamp(12, 0, 10) == 10;</code></example>
        public static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        /// <summary>-1, 0 or +1.</summary>
        /// <example><code>ChibiFixed.Sign(-5) == -1;</code></example>
        public static int Sign(int v) => v > 0 ? 1 : (v < 0 ? -1 : 0);

        /// <summary>Absolute value. Abs(int.MinValue) wraps to int.MinValue (unchecked), as in two's complement.</summary>
        /// <example><code>ChibiFixed.Abs(-5) == 5;</code></example>
        public static int Abs(int v) => unchecked(v < 0 ? -v : v);

        /// <summary>Exact floor(sqrt(n)) for any n in [0, long.MaxValue]. Throws for negative n.</summary>
        /// <example><code>ChibiFixed.ISqrt(15) == 3;  ChibiFixed.ISqrt(16) == 4;</code></example>
        public static long ISqrt(long n)
        {
            if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), "ISqrt of a negative number");
            ulong x = (ulong)n, res = 0, bit = 1UL << 62;
            while (bit > x) bit >>= 2;
            while (bit != 0)
            {
                if (x >= res + bit) { x -= res + bit; res = (res >> 1) + bit; }
                else res >>= 1;
                bit >>= 2;
            }
            return (long)res;
        }

        /// <summary>dx*dx + dy*dy in 64 bits (never overflows for int inputs).</summary>
        /// <example><code>ChibiFixed.DistSq(3000, 4000) == 25000000L;</code></example>
        public static long DistSq(int dx, int dy) => (long)dx * dx + (long)dy * dy;

        /// <summary>
        /// True when the circle (cx, cy, radius r) overlaps the axis-aligned box [minX..maxX] x [minY..maxY].
        /// All values milli-units; touching counts as overlap. Expects minX &lt;= maxX and minY &lt;= maxY.
        /// </summary>
        /// <example><code>ChibiFixed.CircleOverlapsAabb(0, 0, 1000, 1000, -500, 2000, 500) == true;</code></example>
        public static bool CircleOverlapsAabb(int cx, int cy, int r, int minX, int minY, int maxX, int maxY)
        {
            long dx = (long)cx - Clamp(cx, minX, maxX);
            long dy = (long)cy - Clamp(cy, minY, maxY);
            return dx * dx + dy * dy <= (long)r * r;
        }
    }
}
