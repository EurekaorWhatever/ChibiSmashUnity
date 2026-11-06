using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// VISUAL-ONLY random source (particles, camera shake, sparks). Backed by its own System.Random seeded from
    /// Environment.TickCount, so it never perturbs UnityEngine.Random and is deliberately nondeterministic.
    /// NEVER use it in gameplay or simulation code: anything that decides the fight must draw from ChibiSimState.Rng.
    /// </summary>
    public static class FxRandom
    {
        static readonly System.Random Rng = new System.Random(System.Environment.TickCount);

        /// <summary>Uniform float in [0, 1).</summary>
        public static float Value => (float)Rng.NextDouble();

        /// <summary>Uniform float in [min, max).</summary>
        public static float Range(float min, float max) => min + (max - min) * Value;

        /// <summary>Uniform random point inside the unit circle.</summary>
        public static Vector2 InsideUnitCircle
        {
            get
            {
                float a = Value * Mathf.PI * 2f;
                float r = Mathf.Sqrt(Value);
                return new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            }
        }
    }
}
