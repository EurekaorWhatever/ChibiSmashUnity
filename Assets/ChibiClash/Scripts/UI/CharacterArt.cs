using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Menu / HUD art of the five characters (<see cref="CharacterRoster"/>), loaded from Resources by the art contract:
    /// Characters/&lt;Id&gt;_Portrait (bust) and Characters/&lt;Id&gt; (full body). Missing art is drawn as a coloured
    /// placeholder in the character's palette, and looked up again every few seconds so art imported while the game runs
    /// (it is produced concurrently) appears without a restart. Display only.
    /// </summary>
    public static class CharacterArt
    {
        const float RetrySeconds = 4f;

        static readonly Texture2D[] portrait = new Texture2D[5], body = new Texture2D[5];
        static readonly float[] portraitTried = { -99f, -99f, -99f, -99f, -99f }, bodyTried = { -99f, -99f, -99f, -99f, -99f };
        static readonly string[] initials = new string[5];

        static int Index(CharacterInfo c) => Mathf.Clamp((int)c.Class, 0, 4);

        /// <summary>Resources path of the bust portrait.</summary>
        public static string PortraitPath(CharacterInfo c) => "Characters/" + c.Id + "_Portrait";
        /// <summary>Resources path of the full-body cutout.</summary>
        public static string BodyPath(CharacterInfo c) => "Characters/" + c.Id;

        /// <summary>Bust portrait, or null while the art does not exist yet.</summary>
        public static Texture2D Portrait(CharacterInfo c)
        {
            int i = Index(c);
            if (portrait[i] == null && Time.unscaledTime - portraitTried[i] > RetrySeconds)
            {
                portraitTried[i] = Time.unscaledTime;
                portrait[i] = Resources.Load<Texture2D>(PortraitPath(c));
            }
            return portrait[i];
        }

        /// <summary>Full-body cutout, or null while the art does not exist yet.</summary>
        public static Texture2D Body(CharacterInfo c)
        {
            int i = Index(c);
            if (body[i] == null && Time.unscaledTime - bodyTried[i] > RetrySeconds)
            {
                bodyTried[i] = Time.unscaledTime;
                body[i] = Resources.Load<Texture2D>(BodyPath(c));
            }
            return body[i];
        }

        /// <summary>Loading screen: looks up both images of a character now (synchronously; small files).</summary>
        public static void Preload(CharacterInfo c)
        {
            Portrait(c);
            Body(c);
        }

        static string Initial(CharacterInfo c)
        {
            int i = Index(c);
            return initials[i] ?? (initials[i] = c.Name.Substring(0, 1).ToUpperInvariant());
        }

        /// <summary>Square portrait (cropped into <paramref name="r"/>), or the palette placeholder.</summary>
        public static void DrawPortrait(Rect r, CharacterInfo c, float alpha = 1f, bool flip = false)
        {
            var tex = Portrait(c);
            if (tex != null)
            {
                Gui.Tex(r, tex, new Color(1f, 1f, 1f, alpha), flip);
                return;
            }
            Placeholder(r, c, alpha, false);
        }

        /// <summary>
        /// Full-body art fitted into <paramref name="r"/> with the feet on its bottom edge. Falls back to the portrait,
        /// then to a palette silhouette.
        /// </summary>
        public static void DrawBody(Rect r, CharacterInfo c, float alpha = 1f, bool flip = false)
        {
            var tex = Body(c);
            if (tex == null) tex = Portrait(c);
            if (tex != null)
            {
                float aspect = tex.width / (float)Mathf.Max(1, tex.height);
                float h = r.height, w = h * aspect;
                if (w > r.width) { w = r.width; h = w / aspect; }
                Gui.Tex(new Rect(r.center.x - w * 0.5f, r.yMax - h, w, h), tex, new Color(1f, 1f, 1f, alpha), flip);
                return;
            }
            Placeholder(r, c, alpha, true);
        }

        /// <summary>Coloured stand-in drawn while the character's art is being produced.</summary>
        static void Placeholder(Rect r, CharacterInfo c, float alpha, bool fullBody)
        {
            var p = new Color(c.Primary.r, c.Primary.g, c.Primary.b, alpha);
            var a = new Color(c.Accent.r, c.Accent.g, c.Accent.b, alpha);
            if (!fullBody)
            {
                Gui.Rect(r, new Color(p.r * 0.35f, p.g * 0.35f, p.b * 0.35f, alpha));
                Gui.HalftoneFade(new Rect(r.x, r.y, r.width, r.height * 0.6f), new Color(a.r, a.g, a.b, 0.35f * alpha));
                float s = Mathf.Min(r.width, r.height);
                Gui.Circle(new Rect(r.center.x - s * 0.3f, r.y + s * 0.12f, s * 0.6f, s * 0.6f), p);
                Gui.Rect(new Rect(r.center.x - s * 0.42f, r.y + s * 0.68f, s * 0.84f, s * 0.5f), a, s * 0.2f);
                Gui.Display(new Rect(r.x, r.y + s * 0.12f, r.width, s * 0.6f), Initial(c), Mathf.RoundToInt(s * 0.38f),
                            new Color(1f, 1f, 1f, 0.9f * alpha), TextAnchor.MiddleCenter, 0.15f);
                return;
            }
            float u = Mathf.Min(r.width, r.height * 0.75f);
            float cx = r.center.x;
            Gui.Glow(new Rect(cx - u * 0.6f, r.yMax - u * 1.3f, u * 1.2f, u * 1.2f), new Color(c.Fx.r, c.Fx.g, c.Fx.b, 0.25f * alpha));
            var head = new Rect(cx - u * 0.2f, r.yMax - u * 1.2f, u * 0.4f, u * 0.4f);
            var torso = new Rect(cx - u * 0.26f, head.yMax - u * 0.04f, u * 0.52f, u * 0.5f);
            Gui.Rect(new Rect(cx - u * 0.2f, torso.yMax - u * 0.06f, u * 0.14f, r.yMax - torso.yMax + u * 0.06f), a, u * 0.06f);
            Gui.Rect(new Rect(cx + u * 0.06f, torso.yMax - u * 0.06f, u * 0.14f, r.yMax - torso.yMax + u * 0.06f), a, u * 0.06f);
            Gui.Rect(torso, p, u * 0.12f);
            Gui.Circle(head, new Color(1f, 0.88f, 0.78f, alpha));
            Gui.Circle(new Rect(head.x - u * 0.02f, head.y - u * 0.03f, head.width + u * 0.04f, head.height * 0.55f), p);
            Gui.Display(new Rect(torso.x, torso.y, torso.width, torso.height), Initial(c), Mathf.RoundToInt(u * 0.26f),
                        new Color(1f, 1f, 1f, 0.9f * alpha), TextAnchor.MiddleCenter, 0.15f);
            Gui.Text(new Rect(r.x, r.yMax - 22f, r.width, 20f), "ART COMING SOON", 11, new Color(1f, 1f, 1f, 0.45f * alpha), TextAnchor.MiddleCenter, true, false);
        }
    }
}
