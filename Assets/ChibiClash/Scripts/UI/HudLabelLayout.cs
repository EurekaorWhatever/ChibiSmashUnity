using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Pure layout math for the HUD's world-anchored labels (name tags, move callouts, damage popups): keeps a label on
    /// screen, out of the HUD plates and off the labels placed before it. No GUI calls and no allocation; <see cref="Hud"/>
    /// feeds it the plate rects it draws, in <see cref="Gui"/>'s virtual canvas units, so the result scales with the
    /// resolution exactly like the rest of the HUD. Art review pass 2, item 4
    /// (design/art/reviews/2026-11-12-art-review-pass2.md).
    /// </summary>
    public static class HudLabelLayout
    {
        const float Eps = 1e-3f;

        /// <summary>
        /// Screen extent of a plate drawn with <c>Gui.Skew(r, k)</c> and a drop shadow offset by
        /// <paramref name="shadow"/> to the lower right: the shear leans each side out by |k| * height / 2.
        /// </summary>
        public static Rect SkewedPlate(Rect r, float k, float shadow)
        {
            float lean = Mathf.Abs(k) * r.height * 0.5f;
            return Rect.MinMaxRect(r.xMin - lean, r.yMin, r.xMax + lean + shadow, r.yMax + shadow);
        }

        /// <summary>
        /// Bottom edge of the top HUD band: the largest yMax among the first <paramref name="count"/> plates whose centre
        /// is in the top half of a <paramref name="screenH"/>-tall screen. 0 when there is none.
        /// </summary>
        public static float BandBottom(Rect[] plates, int count, float screenH)
        {
            float bottom = 0f;
            for (int i = 0; i < count; i++)
                if (plates[i].center.y < screenH * 0.5f && plates[i].yMax > bottom) bottom = plates[i].yMax;
            return bottom;
        }

        /// <summary>
        /// The area labels may occupy: the screen inset by <paramref name="margin"/> on every side, starting
        /// <paramref name="gap"/> below the top HUD band (see <see cref="BandBottom"/>).
        /// </summary>
        public static Rect SafeArea(float screenW, float screenH, float margin, float bandBottom, float gap)
        {
            float top = Mathf.Max(margin, bandBottom + gap);
            return Rect.MinMaxRect(margin, top, Mathf.Max(margin, screenW - margin), Mathf.Max(top, screenH - margin));
        }

        /// <summary>
        /// Places <paramref name="label"/> inside <paramref name="area"/> and at least <paramref name="gap"/> away from the
        /// first <paramref name="count"/> <paramref name="blockers"/> (plates and labels placed earlier), moving it as
        /// little as possible. Horizontally it is only clamped to the area (it stays over its fighter); vertically it
        /// takes the nearest free spot. With <paramref name="preferUp"/> the nearest free spot at or above the wanted
        /// one wins when there is one (callouts stack over the name tag); otherwise, and for ties, the nearest wins,
        /// upward first. If no spot is free, the label is returned clamped to the area.
        /// </summary>
        public static Rect Place(Rect label, Rect area, Rect[] blockers, int count, float gap, bool preferUp)
        {
            float w = label.width, h = label.height;
            float x = w >= area.width ? area.center.x - w * 0.5f : Mathf.Clamp(label.x, area.xMin, area.xMax - w);
            float y0 = h >= area.height ? area.yMin : Mathf.Clamp(label.y, area.yMin, area.yMax - h);
            if (Free(x, y0, w, h, area, blockers, count, gap)) return new Rect(x, y0, w, h);

            bool found = false, bestAbove = false;
            float best = y0, bestDist = float.MaxValue;
            for (int k = 0; k < count * 2; k++)
            {
                var b = blockers[k >> 1];
                if (!OverlapsX(b, x, w, gap)) continue;
                float y = (k & 1) == 0 ? b.yMin - gap - h : b.yMax + gap;
                if (!Free(x, y, w, h, area, blockers, count, gap)) continue;
                bool above = y <= y0 + Eps;
                float dist = Mathf.Abs(y - y0);
                bool better;
                if (!found) better = true;
                else if (preferUp && above != bestAbove) better = above;
                else if (Mathf.Abs(dist - bestDist) > Eps) better = dist < bestDist;
                else better = y < best;   // tie: upward
                if (!better) continue;
                found = true;
                best = y;
                bestDist = dist;
                bestAbove = above;
            }
            return new Rect(x, best, w, h);
        }

        /// <summary>
        /// <see cref="Place"/>, then appends the placed rect to <paramref name="blockers"/> (when there is room) so labels
        /// placed after it keep off it too. Returns the placed rect.
        /// </summary>
        public static Rect PlaceAndBlock(Rect label, Rect area, Rect[] blockers, ref int count, float gap, bool preferUp)
        {
            var placed = Place(label, area, blockers, count, gap, preferUp);
            if (count < blockers.Length) blockers[count++] = placed;
            return placed;
        }

        /// <summary>
        /// Frame-rate independent ease of a label's drawn position toward its placed spot: moves the fraction
        /// 1 - e^(-<paramref name="rate"/> * <paramref name="dt"/>) of the way, so it never overshoots and never snaps;
        /// <paramref name="dt"/> &lt;= 0 (or <paramref name="rate"/> &lt;= 0) keeps it where it is.
        /// </summary>
        public static Vector2 Follow(Vector2 current, Vector2 target, float dt, float rate)
        {
            if (dt <= 0f || rate <= 0f) return current;
            return Vector2.LerpUnclamped(current, target, 1f - Mathf.Exp(-rate * dt));
        }

        /// <summary>
        /// Where a name tag's pointer goes so it points at <paramref name="target"/>: under the tag pointing down when the
        /// target is below the tag's centre, else over it pointing up. It follows the target's x, kept within the tag.
        /// </summary>
        public static Rect Pointer(Rect tag, Vector2 target, float w, float h, float gap, out bool down)
        {
            down = target.y >= tag.center.y;
            float half = w * 0.5f;
            float cx = tag.width > w ? Mathf.Clamp(target.x, tag.xMin + half, tag.xMax - half) : tag.center.x;
            return new Rect(cx - half, down ? tag.yMax + gap : tag.yMin - gap - h, w, h);
        }

        /// <summary>Axis-aligned bounds of a w x h box rotated by <paramref name="degrees"/> about its centre.</summary>
        public static Vector2 RotatedSize(float w, float h, float degrees)
        {
            float a = degrees * Mathf.Deg2Rad;
            float c = Mathf.Abs(Mathf.Cos(a)), s = Mathf.Abs(Mathf.Sin(a));
            return new Vector2(w * c + h * s, w * s + h * c);
        }

        static bool OverlapsX(Rect b, float x, float w, float gap) => b.xMax + gap > x + Eps && b.xMin - gap < x + w - Eps;

        static bool Free(float x, float y, float w, float h, Rect area, Rect[] blockers, int count, float gap)
        {
            if (h < area.height && (y < area.yMin - Eps || y + h > area.yMax + Eps)) return false;
            for (int i = 0; i < count; i++)
            {
                var b = blockers[i];
                if (OverlapsX(b, x, w, gap) && b.yMax + gap > y + Eps && b.yMin - gap < y + h - Eps) return false;
            }
            return true;
        }
    }
}
