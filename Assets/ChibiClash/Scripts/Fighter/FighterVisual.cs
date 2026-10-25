using UnityEngine;

namespace ChibiClash
{
    /// <summary>Procedural chibi body (big head, small body) with simple tweened animations.</summary>
    public class FighterVisual : MonoBehaviour
    {
        enum Anim { None, Swing, Thrust, Spin, Recoil, Slam }

        Transform root, body, head, weaponPivot;
        SpriteRenderer bodySr, headSr, shieldSr, auraSr, stunSr;
        SpriteRenderer[] all;
        Color[] baseColors;
        FighterClass cls;
        Anim anim;
        float animT, animDur;
        float restAngle;
        float flashT;
        int facing = 1;
        float bob;

        public Vector3 HandWorld => weaponPivot.position;

        public void Build(ClassDef def, Skin skin, int sortBase, WeaponDef weapon)
        {
            cls = def.type;
            root = new GameObject("Visual").transform;
            root.SetParent(transform, false);

            var dark = Color.Lerp(skin.body, Color.black, 0.45f);
            Art.Sprite(root, "Shadow", Art.Soft, new Color(0f, 0f, 0f, 0.35f), new Vector2(0f, -0.68f), new Vector2(1.1f, 0.3f), sortBase);
            Art.Sprite(root, "LegL", Art.Rounded, dark, new Vector2(-0.16f, -0.55f), new Vector2(0.2f, 0.26f), sortBase + 1);
            Art.Sprite(root, "LegR", Art.Rounded, dark, new Vector2(0.16f, -0.55f), new Vector2(0.2f, 0.26f), sortBase + 1);

            body = new GameObject("Body").transform;
            body.SetParent(root, false);
            bodySr = Art.Sprite(body, "Torso", Art.Rounded, skin.body, new Vector2(0f, -0.25f), new Vector2(0.62f, 0.55f), sortBase + 2);
            Art.Sprite(body, "Belt", Art.Square, skin.accent, new Vector2(0f, -0.38f), new Vector2(0.62f, 0.07f), sortBase + 3);

            head = new GameObject("Head").transform;
            head.SetParent(root, false);
            head.localPosition = new Vector2(0f, 0.3f);
            headSr = Art.Sprite(head, "Face", Art.Circle, new Color(1f, 0.88f, 0.78f), Vector2.zero, new Vector2(0.95f, 0.9f), sortBase + 4);
            Art.Sprite(head, "Hair", Art.Circle, skin.body, new Vector2(-0.06f, 0.18f), new Vector2(0.98f, 0.62f), sortBase + 5);
            Art.Sprite(head, "EyeL", Art.Circle, new Color(0.12f, 0.1f, 0.15f), new Vector2(0.08f, -0.04f), new Vector2(0.11f, 0.16f), sortBase + 6);
            Art.Sprite(head, "EyeR", Art.Circle, new Color(0.12f, 0.1f, 0.15f), new Vector2(0.3f, -0.04f), new Vector2(0.11f, 0.16f), sortBase + 6);
            Art.Sprite(head, "Shine", Art.Circle, Color.white, new Vector2(0.1f, 0f), new Vector2(0.04f, 0.05f), sortBase + 7);
            Art.Sprite(head, "Blush", Art.Soft, new Color(1f, 0.45f, 0.5f, 0.6f), new Vector2(0.36f, -0.17f), new Vector2(0.18f, 0.1f), sortBase + 6);
            BuildHat(skin, sortBase + 8);

            weaponPivot = new GameObject("Weapon").transform;
            weaponPivot.SetParent(root, false);
            weaponPivot.localPosition = new Vector2(0.28f, -0.2f);
            if (!BuildSpriteWeapon(def, weapon, sortBase + 9)) BuildWeapon(def, skin, sortBase + 9);   // painted sprite, else the code-drawn fallback
            Art.Sprite(root, "Hand", Art.Circle, new Color(1f, 0.88f, 0.78f), new Vector2(0.28f, -0.2f), new Vector2(0.17f, 0.17f), sortBase + 12);

            shieldSr = Art.Sprite(root, "Shield", Art.Circle, new Color(0.5f, 0.85f, 1f, 0.35f), new Vector2(0f, -0.05f), new Vector2(1.7f, 1.7f), sortBase + 14);
            shieldSr.enabled = false;
            auraSr = Art.Sprite(root, "Aura", Art.Soft, new Color(1f, 1f, 1f, 0.6f), new Vector2(0f, -0.05f), new Vector2(2.4f, 2.4f), sortBase - 1);
            auraSr.enabled = false;
            stunSr = Art.Sprite(root, "Stun", Art.Ring, new Color(1f, 0.95f, 0.3f, 0.9f), new Vector2(0f, 0.85f), new Vector2(0.6f, 0.2f), sortBase + 15);
            stunSr.enabled = false;

            all = root.GetComponentsInChildren<SpriteRenderer>(true);
            baseColors = new Color[all.Length];
            for (int i = 0; i < all.Length; i++) baseColors[i] = all[i].color;
        }

        void BuildHat(Skin skin, int order)
        {
            var a = skin.accent;
            switch (skin.hat)
            {
                case Hat.Crown:
                    Art.Sprite(head, "Crown", Art.Square, new Color(1f, 0.8f, 0.2f), new Vector2(0f, 0.5f), new Vector2(0.45f, 0.18f), order);
                    break;
                case Hat.Bandana:
                    Art.Sprite(head, "Bandana", Art.Square, a, new Vector2(0f, 0.25f), new Vector2(0.96f, 0.1f), order);
                    Art.Sprite(head, "Knot", Art.Square, a, new Vector2(-0.5f, 0.2f), new Vector2(0.2f, 0.08f), order);
                    break;
                case Hat.Horns:
                    var h1 = Art.Sprite(head, "HornL", Art.Rounded, a, new Vector2(-0.22f, 0.48f), new Vector2(0.12f, 0.3f), order);
                    h1.transform.localRotation = Quaternion.Euler(0, 0, 20);
                    var h2 = Art.Sprite(head, "HornR", Art.Rounded, a, new Vector2(0.26f, 0.48f), new Vector2(0.12f, 0.3f), order);
                    h2.transform.localRotation = Quaternion.Euler(0, 0, -20);
                    break;
                case Hat.Halo:
                    Art.Sprite(head, "Halo", Art.Ring, new Color(1f, 0.85f, 0.3f), new Vector2(0f, 0.62f), new Vector2(0.6f, 0.18f), order);
                    break;
                case Hat.Cat:
                    var e1 = Art.Sprite(head, "EarL", Art.Square, skin.body, new Vector2(-0.25f, 0.42f), new Vector2(0.2f, 0.2f), order);
                    e1.transform.localRotation = Quaternion.Euler(0, 0, 45);
                    var e2 = Art.Sprite(head, "EarR", Art.Square, skin.body, new Vector2(0.25f, 0.42f), new Vector2(0.2f, 0.2f), order);
                    e2.transform.localRotation = Quaternion.Euler(0, 0, 45);
                    break;
            }
        }

        /// <summary>Mounts the hand-painted weapon sprite so the grip sits in the fighter's hand and the business end points forward.</summary>
        bool BuildSpriteWeapon(ClassDef def, WeaponDef w, int order)
        {
            var spr = w?.Sprite;
            if (spr == null) return false;

            var go = new GameObject("WeaponSprite");
            go.transform.SetParent(weaponPivot, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = spr;
            sr.sharedMaterial = Art.SpriteMat;
            sr.sortingOrder = order;
            sr.flipX = w.flip;

            Vector2 size = spr.bounds.size;                         // world units at scale 1 (centred pivot)
            float s = w.length / Mathf.Max(size.x, size.y);
            var rot = Quaternion.Euler(0f, 0f, w.rotation);
            float gx = w.flip ? 1f - w.gripX : w.gripX;
            Vector2 grip = new Vector2((gx - 0.5f) * size.x, (w.gripY - 0.5f) * size.y);
            go.transform.localScale = new Vector3(s, s, 1f);
            go.transform.localRotation = rot;
            go.transform.localPosition = -(Vector3)(Vector2)(rot * (grip * s));   // put the grip on the pivot

            switch (def.type)
            {
                case FighterClass.Melee: restAngle = 35f; break;
                case FighterClass.Heavy: restAngle = 70f; break;
                case FighterClass.Ranged: restAngle = 0f; break;
                case FighterClass.Magic: restAngle = -10f; break;
                default: restAngle = 12f; break;
            }
            weaponPivot.localRotation = Quaternion.Euler(0, 0, restAngle);
            return true;
        }

        void BuildWeapon(ClassDef def, Skin skin, int order)
        {
            var metal = new Color(0.85f, 0.88f, 0.95f);
            var wood = new Color(0.55f, 0.36f, 0.2f);
            var t = weaponPivot;
            switch (def.type)
            {
                case FighterClass.Melee:
                    Art.Sprite(t, "Blade", Art.Rounded, metal, new Vector2(0.58f, 0f), new Vector2(1.0f, 0.09f), order);
                    Art.Sprite(t, "Guard", Art.Rounded, skin.accent, new Vector2(0.08f, 0f), new Vector2(0.07f, 0.3f), order + 1);
                    restAngle = 35f;
                    break;
                case FighterClass.Heavy:
                    Art.Sprite(t, "Handle", Art.Rounded, wood, new Vector2(0.45f, 0f), new Vector2(0.95f, 0.11f), order);
                    Art.Sprite(t, "Head", Art.Rounded, new Color(0.55f, 0.58f, 0.65f), new Vector2(0.92f, 0f), new Vector2(0.42f, 0.6f), order + 1);
                    Art.Sprite(t, "Band", Art.Square, skin.accent, new Vector2(0.92f, 0f), new Vector2(0.44f, 0.1f), order + 2);
                    restAngle = 70f;
                    break;
                case FighterClass.Ranged:
                    Art.Sprite(t, "Bow", Art.Ring, wood, new Vector2(0.12f, 0f), new Vector2(0.38f, 1.0f), order);
                    Art.Sprite(t, "String", Art.Square, Color.white, new Vector2(0.0f, 0f), new Vector2(0.02f, 0.9f), order + 1);
                    restAngle = 0f;
                    break;
                case FighterClass.Magic:
                    Art.Sprite(t, "Staff", Art.Rounded, wood, new Vector2(0.05f, 0.25f), new Vector2(0.09f, 1.1f), order);
                    Art.Sprite(t, "OrbGlow", Art.Soft, skin.ultFx, new Vector2(0.05f, 0.85f), new Vector2(0.7f, 0.7f), order + 1);
                    Art.Sprite(t, "Orb", Art.Circle, Color.Lerp(skin.ultFx, Color.white, 0.4f), new Vector2(0.05f, 0.85f), new Vector2(0.26f, 0.26f), order + 2);
                    restAngle = -10f;
                    break;
                case FighterClass.Polearm:
                    Art.Sprite(t, "Shaft", Art.Rounded, wood, new Vector2(0.55f, 0f), new Vector2(1.7f, 0.08f), order);
                    var tip = Art.Sprite(t, "Tip", Art.Square, metal, new Vector2(1.45f, 0f), new Vector2(0.2f, 0.2f), order + 1);
                    tip.transform.localRotation = Quaternion.Euler(0, 0, 45);
                    Art.Sprite(t, "Tassel", Art.Rounded, skin.accent, new Vector2(1.25f, -0.08f), new Vector2(0.08f, 0.2f), order + 2);
                    restAngle = 12f;
                    break;
            }
            weaponPivot.localRotation = Quaternion.Euler(0, 0, restAngle);
        }

        public void SetWeaponVisible(bool on)
        {
            foreach (var sr in weaponPivot.GetComponentsInChildren<SpriteRenderer>()) sr.enabled = on;
        }

        public void SetFacing(int f)
        {
            facing = f;
            root.localScale = new Vector3(f, 1f, 1f);
        }

        public void PlayNormal(bool heavy, int idx, float duration)
        {
            if (cls == FighterClass.Ranged || cls == FighterClass.Magic) Play(Anim.Recoil, duration);
            else if (cls == FighterClass.Polearm) Play(heavy ? Anim.Swing : Anim.Thrust, duration);
            else if (cls == FighterClass.Melee) Play(heavy || idx % 2 == 1 ? Anim.Swing : Anim.Thrust, duration);
            else Play(heavy ? Anim.Slam : Anim.Swing, duration);
        }

        public void PlaySwing(float d) => Play(Anim.Swing, d);
        public void PlayThrust(float d) => Play(Anim.Thrust, d);
        public void PlaySpin(float d) => Play(Anim.Spin, d);
        public void PlaySlam(float d) => Play(Anim.Slam, d);
        public void PlayRecoil(float d) => Play(Anim.Recoil, d);

        void Play(Anim a, float d)
        {
            anim = a;
            animT = 0f;
            animDur = Mathf.Max(0.05f, d);
        }

        public void Flash() => flashT = 0.1f;

        public void SetShield(bool on, bool parry, float guard = 1f)
        {
            shieldSr.enabled = on;
            if (!on) return;
            // the bubble shrinks and turns red as the guard meter drains
            var c = parry ? new Color(1f, 1f, 1f, 0.55f) : Color.Lerp(new Color(1f, 0.3f, 0.3f, 0.45f), new Color(0.5f, 0.85f, 1f, 0.35f), guard);
            shieldSr.color = c;
            float s = 1.7f * (0.7f + 0.3f * Mathf.Clamp01(guard));
            shieldSr.transform.localScale = new Vector3(s, s, 1f);
        }

        public void SetAura(bool on, Color c)
        {
            auraSr.enabled = on;
            if (on) auraSr.color = new Color(c.r, c.g, c.b, 0.45f + 0.15f * Mathf.Sin(Time.time * 12f));
        }

        public void SetStunned(bool on) => stunSr.enabled = on;

        public void Tick(Vector2 velocity, bool grounded, bool blocking)
        {
            float dt = Time.deltaTime;
            bob += dt * (Mathf.Abs(velocity.x) > 0.5f && grounded ? 18f : 4f);
            float squashY = grounded ? 1f + Mathf.Sin(bob) * 0.03f : 1f + Mathf.Clamp(velocity.y * 0.012f, -0.08f, 0.1f);
            body.localScale = new Vector3(2f - squashY, squashY, 1f);
            head.localPosition = new Vector2(0f, 0.3f + Mathf.Sin(bob) * 0.025f);
            root.localRotation = Quaternion.Euler(0, 0, blocking ? 0f : -Mathf.Clamp(velocity.x * facing, -10f, 10f) * 0.6f);
            if (anim != Anim.Spin) root.localScale = new Vector3(facing, 1f, 1f);

            float angle = restAngle;
            Vector2 offset = new Vector2(0.28f, -0.2f);
            if (anim != Anim.None)
            {
                animT += dt;
                float t = Mathf.Clamp01(animT / animDur);
                float e = 1f - (1f - t) * (1f - t);
                switch (anim)
                {
                    case Anim.Swing: angle = Mathf.Lerp(110f, -60f, e); break;
                    case Anim.Slam: angle = Mathf.Lerp(150f, -80f, e * e); break;
                    case Anim.Thrust:
                        angle = 0f;
                        offset.x += Mathf.Sin(t * Mathf.PI) * 0.55f;
                        break;
                    case Anim.Recoil:
                        offset.x -= Mathf.Sin(t * Mathf.PI) * 0.15f;
                        break;
                    case Anim.Spin:
                        root.localScale = new Vector3(Mathf.Cos(t * Mathf.PI * 8f) * facing, 1f, 1f);
                        angle = 0f;
                        break;
                }
                if (t >= 1f) anim = Anim.None;
            }
            weaponPivot.localPosition = offset;
            weaponPivot.localRotation = Quaternion.Euler(0, 0, angle);

            if (flashT > 0f)
            {
                flashT -= dt;
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != shieldSr && all[i] != auraSr && all[i] != stunSr)
                        all[i].color = flashT > 0f ? new Color(1f, 1f, 1f, baseColors[i].a) : baseColors[i];
            }
        }
    }
}
