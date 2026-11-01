using UnityEngine;
using UnityEngine.UI;

namespace WeaponBrawl
{
    /// <summary>
    /// uGUI shape for the hard-angled look: parallelograms and trapezoids instead of rectangles, with an optional
    /// border, hollow mode (cursor frames) and fill amount (health bars, cooldowns).
    /// </summary>
    public class SkewPanel : MaskableGraphic
    {
        public enum Fill { Full, FromLeft, FromRight, FromBottom }

        float skew, bottomInset, border, amount = 1f;
        Color borderColor = Color.clear;
        bool hollow;
        Fill fill = Fill.Full;

        /// <summary>Pixels the top edge shifts right (negative = left).</summary>
        public float Skew { get => skew; set { skew = value; SetVerticesDirty(); } }

        /// <summary>Pixels the bottom edge shrinks on each side, for trapezoids.</summary>
        public float BottomInset { get => bottomInset; set { bottomInset = value; SetVerticesDirty(); } }

        public float BorderWidth { get => border; set { border = value; SetVerticesDirty(); } }
        public Color BorderColor { get => borderColor; set { borderColor = value; SetVerticesDirty(); } }
        public bool Hollow { get => hollow; set { hollow = value; SetVerticesDirty(); } }
        public Fill FillMode { get => fill; set { fill = value; SetVerticesDirty(); } }

        public float FillAmount
        {
            get => amount;
            set
            {
                float v = Mathf.Clamp01(value);
                if (Mathf.Approximately(v, amount)) return;
                amount = v;
                SetVerticesDirty();
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var r = GetPixelAdjustedRect();
            float right = Mathf.Max(0f, skew), left = Mathf.Max(0f, -skew);

            var bl = new Vector2(r.xMin + left + bottomInset, r.yMin);
            var br = new Vector2(r.xMax - right - bottomInset, r.yMin);
            var tl = new Vector2(r.xMin + right, r.yMax);
            var tr = new Vector2(r.xMax - left, r.yMax);

            switch (fill)
            {
                case Fill.FromLeft:
                    br = Vector2.Lerp(bl, br, amount);
                    tr = Vector2.Lerp(tl, tr, amount);
                    break;
                case Fill.FromRight:
                    bl = Vector2.Lerp(br, bl, amount);
                    tl = Vector2.Lerp(tr, tl, amount);
                    break;
                case Fill.FromBottom:
                    tl = Vector2.Lerp(bl, tl, amount);
                    tr = Vector2.Lerp(br, tr, amount);
                    break;
            }
            if (fill != Fill.Full && amount <= 0f) return;

            if (border <= 0f)
            {
                if (!hollow) Quad(vh, bl, br, tr, tl, color);
                return;
            }

            var ibl = bl + new Vector2(border, border);
            var ibr = br + new Vector2(-border, border);
            var itr = tr + new Vector2(-border, -border);
            var itl = tl + new Vector2(border, -border);

            var edge = borderColor;
            edge.a *= color.a;
            Quad(vh, bl, br, ibr, ibl, edge);
            Quad(vh, br, tr, itr, ibr, edge);
            Quad(vh, tr, tl, itl, itr, edge);
            Quad(vh, tl, bl, ibl, itl, edge);
            if (!hollow) Quad(vh, ibl, ibr, itr, itl, color);
        }

        static void Quad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color col)
        {
            int i = vh.currentVertCount;
            vh.AddVert(a, col, Vector2.zero);
            vh.AddVert(b, col, Vector2.zero);
            vh.AddVert(c, col, Vector2.zero);
            vh.AddVert(d, col, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i, i + 2, i + 3);
        }
    }
}
