using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Fighter body with tweened and frame-by-frame animation, keyed by <see cref="CharacterInfo.Id"/> (CharacterRoster).
    /// Three tiers, best available wins per clip:
    ///   1. sprite sheets Resources/Animations/&lt;Id&gt;/&lt;Clip&gt;.png (SpriteAnimLibrary) - the drawing already holds the weapon;
    ///   2. the full-body drawing Resources/Characters/&lt;Id&gt;.png with the weapon sprite swung from the hand;
    ///   3. the procedural chibi body (no art at all).
    /// Public API is shared by the offline Fighter and the netcode view (ChibiSimView). Per-frame work is allocation-free.
    /// </summary>
    public class FighterVisual : MonoBehaviour
    {
        enum Anim { None, Swing, Thrust, Spin, Recoil, Slam }

        Transform root, body, head, weaponPivot;
        SpriteRenderer bodySr, headSr, shieldSr, auraSr, stunSr;
        SpriteRenderer[] all, weaponSrs;
        Color[] baseColors;
        FighterClass cls;
        Anim anim;
        float animT, animDur;
        float restAngle;
        float flashT;
        int facing = 1;
        float bob;
        Vector2 handRest = new Vector2(0.28f, -0.2f);
        float alpha = 1f;
        bool weaponShown = true;

        /// <summary>Body scale multiplier (e.g. a transformation).</summary>
        public float SizeMul = 1f;

        /// <summary>Height of a drawn body in world units; feet sit at y = -0.72.</summary>
        public const float ArtHeight = 1.85f;

        /// <summary>Hand / weapon pivot in world space (projectile and effect origin).</summary>
        public Vector3 HandWorld => weaponPivot.position;

        /// <summary>CharacterRoster id this visual draws (asset key).</summary>
        public string CharacterId { get; private set; }

        /// <summary>True when the character's full-body drawing was found (tier 1 or 2).</summary>
        public bool HasArt => hasArt;

        /// <summary>The renderer that shows the body (drawing / sheet frame, or the procedural torso): afterimage source.</summary>
        public SpriteRenderer BodyRenderer => artSr != null ? artSr : bodySr;

        /// <summary>
        /// Builds the visual. <paramref name="characterId"/> overrides the roster id (default: the character that plays
        /// <paramref name="def"/>'s class), so a future roster with several characters per class needs no other change.
        /// </summary>
        public void Build(ClassDef def, Skin skin, int sortBase, WeaponDef weapon, string characterId = null)
        {
            cls = def.type;
            CharacterId = !string.IsNullOrEmpty(characterId) ? characterId : CharacterRoster.Of(def.type).Id;
            root = new GameObject("Visual").transform;
            root.SetParent(transform, false);

            var art = SpriteAnimLibrary.Body(CharacterId);
            if (art != null)
            {
                BuildArtBody(art, skin, sortBase);
                hasArt = true;
                head = new GameObject("Head").transform;
                head.SetParent(root, false);
                handRest = new Vector2(0.2f, 0.03f);
                weaponPivot = new GameObject("Weapon").transform;
                weaponPivot.SetParent(root, false);
                weaponPivot.localPosition = handRest;
                if (!BuildHeroWeapon(sortBase + 9) && !BuildSpriteWeapon(def, weapon, sortBase + 9)) BuildWeapon(def, skin, sortBase + 9);
                FinishBuild(sortBase);
                // drawn bodies are taller than the chibi: lift the shield / aura / stun ring to match
                shieldSr.transform.localPosition = new Vector2(0f, 0.2f);
                shieldSr.transform.localScale = new Vector3(1.8f, 2.1f, 1f);
                auraSr.transform.localPosition = new Vector2(0f, 0.2f);
                auraSr.transform.localScale = new Vector3(2.4f, 2.8f, 1f);
                stunSr.transform.localPosition = new Vector2(0f, 1.3f);
                return;
            }

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
            weaponPivot.localPosition = handRest;
            if (!BuildSpriteWeapon(def, weapon, sortBase + 9)) BuildWeapon(def, skin, sortBase + 9);   // painted sprite, else the code-drawn fallback
            Art.Sprite(root, "Hand", Art.Circle, new Color(1f, 0.88f, 0.78f), new Vector2(0.28f, -0.2f), new Vector2(0.17f, 0.17f), sortBase + 12);

            FinishBuild(sortBase);
        }

        /// <summary>The drawing, scaled to ArtHeight with the feet on the ground; non-default skins tint it lightly.</summary>
        void BuildArtBody(Sprite art, Skin skin, int sortBase)
        {
            Art.Sprite(root, "Shadow", Art.Soft, new Color(0f, 0f, 0f, 0.35f), new Vector2(0f, -0.7f), new Vector2(1.1f, 0.3f), sortBase);
            body = new GameObject("Body").transform;
            body.SetParent(root, false);
            body.localPosition = new Vector2(0f, -0.72f);           // squash pivots at the feet
            var go = new GameObject("Art");
            go.transform.SetParent(body, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = art;
            sr.sharedMaterial = Art.SpriteMat;
            sr.sortingOrder = sortBase + 2;
            bool plain = skin == null || skin.id.EndsWith("_default");
            sr.color = plain ? Color.white : Color.Lerp(Color.white, skin.body, 0.3f);
            // drawings are framed with the feet at 1010/1024 of the image and the figure ~970 px tall (tools/art)
            float s = ArtHeight / (art.bounds.size.y * 970f / 1024f);
            go.transform.localScale = new Vector3(s, s, 1f);
            go.transform.localPosition = new Vector2(0f, art.bounds.size.y * s * (0.5f - 14f / 1024f));
            bodySr = headSr = artSr = sr;
            artBase = art;
            artBaseScale = go.transform.localScale;
            artBasePos = go.transform.localPosition;
        }

        void FinishBuild(int sortBase)
        {
            bodyBase = body.localPosition;
            weaponSrs = weaponPivot.GetComponentsInChildren<SpriteRenderer>(true);
            // swing smear: a soft streak along the weapon shown during the strike / follow-through frames
            trailSr = Art.Sprite(weaponPivot, "Trail", Art.Soft, new Color(1f, 1f, 1f, 0f), new Vector2(0.7f, 0f), new Vector2(1.6f, 0.45f), sortBase + 8);
            trailSr.enabled = false;
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

        static float ClassRest(FighterClass c)
        {
            switch (c)
            {
                case FighterClass.Melee: return 35f;
                case FighterClass.Heavy: return 70f;
                case FighterClass.Ranged: return 0f;
                case FighterClass.Magic: return -10f;
                default: return 12f;
            }
        }

        /// <summary>
        /// Signature weapon cut from the character sheet (Resources/Weapons/Hero_&lt;Id&gt;.png, drawn upright: business
        /// end up). Grip / length per character mirror HEROES in tools/art/gen_cutout_anims.py.
        /// </summary>
        bool BuildHeroWeapon(int order)
        {
            float gx, gy, length, rest;
            switch (CharacterId)
            {
                case "Kaia": gx = 0.60f; gy = 0.12f; length = 0.95f; rest = 35f; break;
                case "Bruna": gx = 0.39f; gy = 0.25f; length = 1.45f; rest = 70f; break;
                case "Ren": gx = 0.83f; gy = 0.50f; length = 1.3f; rest = 80f; break;
                case "Lyra": gx = 0.27f; gy = 0.35f; length = 0.7f; rest = 80f; break;
                case "Taro": gx = 0.50f; gy = 0.30f; length = 2.1f; rest = 12f; break;
                default: return false;
            }
            var spr = Resources.Load<Sprite>("Weapons/Hero_" + CharacterId);
            if (spr == null) return false;
            var go = new GameObject("WeaponSprite");
            go.transform.SetParent(weaponPivot, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = spr;
            sr.sharedMaterial = Art.SpriteMat;
            sr.sortingOrder = order;
            Vector2 size = spr.bounds.size;
            float s = length / Mathf.Max(size.x, size.y);
            var rot = Quaternion.Euler(0f, 0f, -90f);                   // upright art -> business end along +x
            Vector2 grip = new Vector2((gx - 0.5f) * size.x, (gy - 0.5f) * size.y);
            go.transform.localScale = new Vector3(s, s, 1f);
            go.transform.localRotation = rot;
            go.transform.localPosition = -(Vector3)(Vector2)(rot * (grip * s));
            restAngle = rest;
            weaponPivot.localRotation = Quaternion.Euler(0, 0, restAngle);
            return true;
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

            restAngle = ClassRest(def.type);
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
                    break;
                case FighterClass.Heavy:
                    Art.Sprite(t, "Handle", Art.Rounded, wood, new Vector2(0.45f, 0f), new Vector2(0.95f, 0.11f), order);
                    Art.Sprite(t, "Head", Art.Rounded, new Color(0.55f, 0.58f, 0.65f), new Vector2(0.92f, 0f), new Vector2(0.42f, 0.6f), order + 1);
                    Art.Sprite(t, "Band", Art.Square, skin.accent, new Vector2(0.92f, 0f), new Vector2(0.44f, 0.1f), order + 2);
                    break;
                case FighterClass.Ranged:
                    Art.Sprite(t, "Bow", Art.Ring, wood, new Vector2(0.12f, 0f), new Vector2(0.38f, 1.0f), order);
                    Art.Sprite(t, "String", Art.Square, Color.white, new Vector2(0.0f, 0f), new Vector2(0.02f, 0.9f), order + 1);
                    break;
                case FighterClass.Magic:
                    Art.Sprite(t, "Staff", Art.Rounded, wood, new Vector2(0.05f, 0.25f), new Vector2(0.09f, 1.1f), order);
                    Art.Sprite(t, "OrbGlow", Art.Soft, skin.ultFx, new Vector2(0.05f, 0.85f), new Vector2(0.7f, 0.7f), order + 1);
                    Art.Sprite(t, "Orb", Art.Circle, Color.Lerp(skin.ultFx, Color.white, 0.4f), new Vector2(0.05f, 0.85f), new Vector2(0.26f, 0.26f), order + 2);
                    break;
                case FighterClass.Polearm:
                    Art.Sprite(t, "Shaft", Art.Rounded, wood, new Vector2(0.55f, 0f), new Vector2(1.7f, 0.08f), order);
                    var tip = Art.Sprite(t, "Tip", Art.Square, metal, new Vector2(1.45f, 0f), new Vector2(0.2f, 0.2f), order + 1);
                    tip.transform.localRotation = Quaternion.Euler(0, 0, 45);
                    Art.Sprite(t, "Tassel", Art.Rounded, skin.accent, new Vector2(1.25f, -0.08f), new Vector2(0.08f, 0.2f), order + 2);
                    break;
            }
            restAngle = ClassRest(def.type);
            weaponPivot.localRotation = Quaternion.Euler(0, 0, restAngle);
        }

        /// <summary>Shows / hides the hand-held weapon (thrown spear). Sheets draw their own weapon and ignore this.</summary>
        public void SetWeaponVisible(bool on)
        {
            weaponShown = on;
            for (int i = 0; i < weaponSrs.Length; i++) weaponSrs[i].enabled = on;
        }

        public void SetFacing(int f)
        {
            facing = f;
            root.localScale = new Vector3(f * SizeMul, SizeMul, 1f);
        }

        /// <summary>Body opacity (stealth skills); 1 = normal.</summary>
        public void SetAlpha(float a) => alpha = Mathf.Clamp01(a);

        /// <summary>Tint over the body colours (transformations); white = none.</summary>
        public void SetTint(Color c)
        {
            if (bodySr != null) bodySr.color = c;
            for (int i = 0; i < all.Length; i++) if (all[i] == bodySr) baseColors[i] = c;
        }

        // ---------------------------------------------------------------- triggering moves

        /// <summary>Light (idx = combo step) or heavy normal attack lasting <paramref name="duration"/> seconds.</summary>
        public void PlayNormal(bool heavy, int idx, float duration)
        {
            Anim a;
            if (cls == FighterClass.Ranged || cls == FighterClass.Magic) a = Anim.Recoil;
            else if (cls == FighterClass.Polearm) a = heavy ? Anim.Swing : Anim.Thrust;
            else if (cls == FighterClass.Melee) a = heavy || idx % 2 == 1 ? Anim.Swing : Anim.Thrust;
            else a = heavy ? Anim.Slam : Anim.Swing;
            Play(a, duration);
            AutoClip(NormalClip(heavy, idx), duration);
        }

        /// <summary>Sheet clip used for a normal attack (falls back to the procedural tween when the sheet is missing).</summary>
        string NormalClip(bool heavy, int idx)
        {
            switch (cls)
            {
                case FighterClass.Melee: return heavy ? "Swing" : (idx % 3 == 0 ? "Jab" : idx % 3 == 1 ? "Swing" : "Upswing");
                case FighterClass.Heavy: return heavy ? "Slam" : (idx % 2 == 0 ? "Swing" : "Upswing");
                case FighterClass.Ranged: return "Shoot";
                case FighterClass.Magic: return "Cast";
                default: return heavy ? "Swing" : (idx % 2 == 0 ? "Thrust" : "LowSweep");
            }
        }

        public void PlaySwing(float d) { Play(Anim.Swing, d); AutoClip("Swing", d); }
        public void PlayThrust(float d) { Play(Anim.Thrust, d); AutoClip("Thrust", d); }
        public void PlaySpin(float d) { Play(Anim.Spin, d); AutoClip("Spin", d); }
        public void PlaySlam(float d) { Play(Anim.Slam, d); AutoClip("Slam", d); }

        /// <summary>Clip picked by a Play* call: skipped while a locked clip (<see cref="PlayClipLocked"/>) is showing.</summary>
        void AutoClip(string clip, float d)
        {
            if (lockT <= 0f) PlayClip(clip, d);
        }

        /// <summary>
        /// Like <see cref="PlayClip"/>, but the generic Play* calls (PlaySwing, PlayThrust...) cannot replace it until it
        /// ends: a signature clip (Skill1 / Skill2 / Ultimate / KO) survives the per-frame tweens of a program move.
        /// <see cref="Hurt"/> and <see cref="PlayClip"/> still interrupt it.
        /// </summary>
        public void PlayClipLocked(string clip, float duration)
        {
            PlayClip(clip, duration);
            var c = hasArt ? SpriteAnimLibrary.Get(CharacterId, clip) : null;
            lockT = duration > 0f ? duration : c != null ? c.Length : 0f;
        }

        /// <summary>Release snap (shots / casts). Melee classes use it as a flinch, so their sheet shows Hit.</summary>
        public void PlayRecoil(float d)
        {
            Play(Anim.Recoil, d);
            AutoClip(cls == FighterClass.Ranged ? "Shoot" : cls == FighterClass.Magic ? "Cast" : "Hit", d);
        }

        void Play(Anim a, float d)
        {
            anim = a;
            animT = 0f;
            animDur = Mathf.Max(0.05f, d);
        }

        /// <summary>
        /// Plays sheet clip <paramref name="clip"/> once (any name in <see cref="SpriteAnimLibrary.Clips"/>), stretched to
        /// <paramref name="duration"/> seconds so it lines up with the move; duration &lt;= 0 plays it at its natural 24 fps.
        /// Non-looping clips hold their last frame until the duration ends. No-op visually when the sheet is missing
        /// (the procedural tween, if any, still runs). Allocation-free; pass string literals / cached names.
        /// </summary>
        public void PlayClip(string clip, float duration)
        {
            oneShot = clip;
            oneShotT = 0f;
            oneShotDur = duration;
            lockT = 0f;
            phS = phA = phR = 0f;
        }

        /// <summary>
        /// Plays a moveset move: its sheet-pose clip Move_&lt;<paramref name="moveId"/>&gt; when the character has one, else
        /// the generic <paramref name="animKindFallback"/> clip (Swing, Thrust...), else (no sheet at all) the procedural
        /// cutout tween. A sheet-pose clip with phase data is time-remapped so its strike frame lands on the move's first
        /// active frame (startup / active / recovery in seconds, i.e. sim ticks / TickRate); other clips are stretched
        /// over the whole move like <see cref="PlayClip"/>. Allocation-free after the first call per move id.
        /// </summary>
        public void PlayMoveClip(string moveId, string animKindFallback, float startupSec, float activeSec, float recoverySec)
        {
            string name = SpriteAnimLibrary.MoveClipName(moveId);
            var posed = name != null && hasArt ? SpriteAnimLibrary.Get(CharacterId, name) : null;
            float total = Mathf.Max(0f, startupSec) + Mathf.Max(0f, activeSec) + Mathf.Max(0f, recoverySec);
            PlayClip(posed != null ? name : animKindFallback, total);
            if (posed != null && posed.HasPhases && total > 0f)
            {
                phS = Mathf.Max(0f, startupSec);
                phA = Mathf.Max(0f, activeSec);
                phR = Mathf.Max(0f, recoverySec);
            }
        }

        // phase lengths of the playing sheet-pose move (0 = stretch linearly); reset by PlayClip
        float phS, phA, phR;

        /// <summary>Hand-drawn move to play once at its natural speed (Skill1 / Skill2 / Ultimate / Hit).</summary>
        public void PlayMove(string clip) => PlayClip(clip, 0f);

        /// <summary>Took a hit: recoil squash (procedural) or the Hit sheet.</summary>
        public void Hurt()
        {
            hurtT = 0.3f;
            lockT = 0f;
            PlayClip("Hit", 0f);
        }

        /// <summary>Cancels the current one-shot clip (e.g. a move was interrupted).</summary>
        public void StopClip()
        {
            oneShot = null;
            lockT = 0f;
        }

        public void SetVictory(bool on) => victory = on;

        public void Flash() => flashT = 0.1f;

        public void SetShield(bool on, bool parry, float guard = 1f)
        {
            shieldSr.enabled = on;
            if (!on) return;
            // the bubble shrinks and turns red as the guard meter drains
            var c = parry ? new Color(1f, 1f, 1f, 0.55f) : Color.Lerp(new Color(1f, 0.3f, 0.3f, 0.45f), new Color(0.5f, 0.85f, 1f, 0.35f), guard);
            shieldSr.color = c;
            float s = 1.7f * (0.7f + 0.3f * Mathf.Clamp01(guard));
            if (hasArt) shieldSr.transform.localScale = new Vector3(s * 1.06f, s * 1.24f, 1f);
            else shieldSr.transform.localScale = new Vector3(s, s, 1f);
        }

        public void SetAura(bool on, Color c)
        {
            auraSr.enabled = on;
            if (on) auraSr.color = new Color(c.r, c.g, c.b, 0.45f + 0.15f * Mathf.Sin(Time.time * 12f));
        }

        public void SetStunned(bool on) => stunSr.enabled = on;

        // ---------------------------------------------------------------- animation state

        bool hasArt;
        SpriteRenderer artSr, trailSr;
        Sprite artBase;
        Vector3 artBaseScale, artBasePos, bodyBase;
        string oneShot;
        float oneShotT, oneShotDur, lockT;
        string clipName;
        float clipT;
        bool victory, wasGrounded = true;
        float landT, hurtT, breatheT;

        static float EaseOut(float t) { t = Mathf.Clamp01(t); return 1f - (1f - t) * (1f - t); }
        static float EaseIn(float t) { t = Mathf.Clamp01(t); return t * t * t; }
        static float EaseInOut(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

        // attack phases: anticipation -> strike -> follow-through -> recovery
        const float PhA = 0.24f, PhS = 0.42f, PhF = 0.62f;

        /// <summary>Weapon angle through the four phases: wind back past 'from', snap to 'to', overshoot, settle to rest.</summary>
        float Arc(float t, float from, float to, float overshoot)
        {
            float wind = from + Mathf.Sign(from - to) * 22f;
            if (t < PhA) return Mathf.Lerp(restAngle, wind, EaseOut(t / PhA));
            if (t < PhS) return Mathf.Lerp(wind, to, EaseIn((t - PhA) / (PhS - PhA)));
            if (t < PhF) return Mathf.Lerp(to, to - Mathf.Sign(from - to) * overshoot, EaseOut((t - PhS) / (PhF - PhS)));
            return Mathf.Lerp(to - Mathf.Sign(from - to) * overshoot, restAngle, EaseInOut((t - PhF) / (1f - PhF)));
        }

        /// <summary>Body lean (degrees, + = leaning back) and squash for the current attack phase.</summary>
        static void AttackBody(float t, out float lean, out float sx, out float sy)
        {
            if (t < PhA) { float k = EaseOut(t / PhA); lean = 7f * k; sx = 1f + 0.05f * k; sy = 1f - 0.07f * k; return; }
            if (t < PhS) { float k = (t - PhA) / (PhS - PhA); lean = Mathf.Lerp(7f, -12f, k); sx = Mathf.Lerp(1.05f, 0.94f, k); sy = Mathf.Lerp(0.93f, 1.08f, k); return; }
            if (t < PhF) { float k = (t - PhS) / (PhF - PhS); lean = Mathf.Lerp(-12f, -6f, k); sx = Mathf.Lerp(0.94f, 1f, k); sy = Mathf.Lerp(1.08f, 1f, k); return; }
            float r = EaseInOut((t - PhF) / (1f - PhF));
            lean = Mathf.Lerp(-6f, 0f, r);
            sx = sy = 1f;
        }

        /// <summary>One-shot clip finished? (natural length, or the requested duration when one was given).</summary>
        bool OneShotDone(SpriteClip clip) => oneShotDur > 0f ? oneShotT >= oneShotDur : oneShotT >= clip.Length;

        public void Tick(Vector2 velocity, bool grounded, bool blocking)
        {
            float dt = Time.deltaTime;
            float now = Time.time;
            bool moving = grounded && Mathf.Abs(velocity.x) > 0.6f;
            if (grounded && !wasGrounded) landT = 0.18f;
            wasGrounded = grounded;
            landT -= dt;
            hurtT -= dt;
            breatheT += dt;

            // ---- pick the clip: victory > one-shot move > guard > airborne > run > idle
            if (oneShot != null) oneShotT += dt;
            if (lockT > 0f) lockT -= dt;
            string loco = !grounded ? "Jump" : moving ? "Run" : "Idle";
            if (blocking && grounded && hasArt && SpriteAnimLibrary.Get(CharacterId, "Guard") != null) loco = "Guard";
            string want = victory ? "Victory" : oneShot ?? loco;
            var clip = hasArt ? SpriteAnimLibrary.Get(CharacterId, want) : null;
            if (oneShot != null && want == oneShot && (clip == null || OneShotDone(clip)))
            {
                // finished, or no sheet for it (keep the procedural tween running for its duration instead)
                if (clip != null || oneShotT > Mathf.Max(0.5f, oneShotDur)) oneShot = null;
                want = victory ? "Victory" : loco;
                clip = hasArt ? SpriteAnimLibrary.Get(CharacterId, want) : null;
            }
            if (!ReferenceEquals(want, clipName) && want != clipName)
            {
                clipName = want;
                clipT = 0f;
            }
            clipT += dt;
            bool sheet = clip != null;

            if (sheet)
            {
                // frame-by-frame art: the drawing already contains the weapon, squash and follow-through
                bool isOneShot = oneShot != null && clipName == oneShot;
                if (isOneShot && phS + phA + phR > 0f && clip.HasPhases)
                    artSr.sprite = clip.FramePhased(oneShotT, phS, phA, phR);   // strike frame on the first active frame
                else
                    artSr.sprite = isOneShot && oneShotDur > 0f ? clip.FrameNormalized(oneShotT / oneShotDur)
                                 : clip.Frame(isOneShot ? oneShotT : clipT);
                float s = ArtHeight / SpriteAnimLibrary.FigureFill;
                artSr.transform.localScale = new Vector3(s, s, 1f);
                artSr.transform.localPosition = Vector3.zero;
                if (weaponPivot.gameObject.activeSelf) weaponPivot.gameObject.SetActive(false);
                body.localScale = Vector3.one;
                body.localPosition = bodyBase;
                root.localRotation = Quaternion.identity;
            }
            else
            {
                if (hasArt && artSr.sprite != artBase)
                {
                    artSr.sprite = artBase;
                    artSr.transform.localScale = artBaseScale;
                    artSr.transform.localPosition = artBasePos;
                }
                if (!weaponPivot.gameObject.activeSelf) weaponPivot.gameObject.SetActive(true);
            }

            // ---- procedural body: breathing, run bounce, jump stretch, landing squash, hit recoil, victory hop
            float sx = 1f, sy = 1f, lean = 0f, yOff = 0f;
            if (victory)
            {
                float h = Mathf.Abs(Mathf.Sin(now * 6f));
                yOff = h * 0.22f;
                sy = 1f + 0.07f * (h - 0.5f);
                sx = 1f - 0.04f * (h - 0.5f);
            }
            else if (!grounded)
            {
                float k = Mathf.Clamp(velocity.y * 0.012f, -0.1f, 0.12f);
                sy = 1f + k;
                sx = 1f - k * 0.6f;
                lean = -Mathf.Clamp(velocity.x * facing, -12f, 12f) * 0.8f;
            }
            else if (moving)
            {
                bob += dt * 18f;
                float step = Mathf.Abs(Mathf.Sin(bob));
                yOff = step * 0.07f;
                sy = 1f + 0.06f * (step - 0.5f);
                sx = 1f - 0.04f * (step - 0.5f);
                lean = -8f * Mathf.Sign(velocity.x * facing);
            }
            else
            {
                bob += dt * 4f;
                float b = Mathf.Sin(breatheT * 2.6f);
                sy = 1f + 0.018f * b;
                sx = 1f - 0.01f * b;
            }
            if (landT > 0f)
            {
                float k = landT / 0.18f;
                sy *= 1f - 0.16f * k;
                sx *= 1f + 0.12f * k;
            }
            if (hurtT > 0f)
            {
                float k = hurtT / 0.3f;
                sx *= 1f + 0.12f * k;
                sy *= 1f - 0.1f * k;
                lean += 16f * k;
            }

            // ---- weapon: anticipation -> strike -> follow-through -> recovery
            float angle = victory ? 80f : restAngle;
            Vector2 offset = handRest;
            float trail = 0f;
            float spinT = -1f;
            if (anim != Anim.None)
            {
                animT += dt;
                float t = Mathf.Clamp01(animT / animDur);
                AttackBody(t, out float al, out float ax, out float ay);
                lean += al;
                sx *= ax;
                sy *= ay;
                bool striking = t >= PhA && t < PhF;
                switch (anim)
                {
                    case Anim.Swing:
                        angle = Arc(t, 110f, -60f, 18f);
                        trail = striking ? 1f : 0f;
                        break;
                    case Anim.Slam:
                        angle = Arc(t, 150f, -80f, 10f);
                        trail = striking ? 1f : 0f;
                        if (t >= PhS && t < PhF) sy *= 0.9f;
                        break;
                    case Anim.Thrust:
                        angle = 0f;
                        if (t < PhA) offset.x -= 0.22f * EaseOut(t / PhA);
                        else if (t < PhS) offset.x += Mathf.Lerp(-0.22f, 0.6f, EaseIn((t - PhA) / (PhS - PhA)));
                        else if (t < PhF) offset.x += Mathf.Lerp(0.6f, 0.7f, EaseOut((t - PhS) / (PhF - PhS)));
                        else offset.x += Mathf.Lerp(0.7f, 0f, EaseInOut((t - PhF) / (1f - PhF)));
                        trail = striking ? 0.7f : 0f;
                        break;
                    case Anim.Recoil:
                        // draw / charge, release snap, kick back, settle
                        if (t < PhA) { offset.x -= 0.1f * EaseOut(t / PhA); offset.y += 0.05f * EaseOut(t / PhA); }
                        else if (t < PhS) offset.x += Mathf.Lerp(-0.1f, 0.08f, EaseIn((t - PhA) / (PhS - PhA)));
                        else offset.x += Mathf.Lerp(0.08f, 0f, EaseInOut((t - PhS) / (1f - PhS))) - Mathf.Sin((t - PhS) / (1f - PhS) * Mathf.PI) * 0.12f;
                        break;
                    case Anim.Spin:
                        angle = 0f;
                        trail = 0.6f;
                        spinT = t;
                        break;
                }
                if (t >= 1f) anim = Anim.None;
            }

            if (!sheet)
            {
                body.localScale = new Vector3(sx, sy, 1f);
                body.localPosition = bodyBase + new Vector3(0f, yOff, 0f);
                head.localPosition = new Vector2(0f, 0.3f + Mathf.Sin(bob) * 0.025f);
                root.localRotation = Quaternion.Euler(0, 0, blocking ? 0f : lean * facing);   // + = lean back
            }
            if (spinT >= 0f && !sheet)
                root.localScale = new Vector3(Mathf.Cos(spinT * Mathf.PI * 8f) * facing * SizeMul, SizeMul, 1f);
            else root.localScale = new Vector3(facing * SizeMul, SizeMul, 1f);

            weaponPivot.localPosition = offset;
            weaponPivot.localRotation = Quaternion.Euler(0, 0, angle);
            if (trailSr != null)
            {
                trailSr.enabled = trail > 0f && !sheet && weaponShown;
                if (trailSr.enabled) trailSr.color = new Color(1f, 1f, 1f, 0.55f * trail);
            }

            // ---- colours: stealth alpha, hit flash
            if (alpha < 0.999f || flashT <= 0f)
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != shieldSr && all[i] != auraSr && all[i] != stunSr && all[i] != trailSr)
                    {
                        var bc = baseColors[i];
                        all[i].color = new Color(bc.r, bc.g, bc.b, bc.a * alpha);
                    }
            if (flashT > 0f)
            {
                flashT -= dt;
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != shieldSr && all[i] != auraSr && all[i] != stunSr && all[i] != trailSr)
                        all[i].color = flashT > 0f ? new Color(1f, 1f, 1f, baseColors[i].a) : baseColors[i];
            }
        }

        /// <summary>KO: a copy of the fighter spins off toward the blast zone with star sparkles (the real fighter respawns).</summary>
        public void SpawnKoGhost(Vector2 from, Vector2 velocity)
        {
            var go = new GameObject("KO Ghost");
            go.transform.SetParent(transform.parent, false);
            go.transform.position = from;
            Sprite spr = null;
            Vector3 scale = Vector3.one;
            var ko = hasArt ? SpriteAnimLibrary.Get(CharacterId, "KO") : null;
            if (hasArt)
            {
                spr = ko != null ? ko.frames[0] : artBase;
                float s = ko != null ? ArtHeight / SpriteAnimLibrary.FigureFill : artBaseScale.x;
                scale = new Vector3(s * facing, s, 1f);
            }
            else if (bodySr != null)
            {
                spr = bodySr.sprite;
                scale = bodySr.transform.lossyScale;
            }
            var ghost = go.AddComponent<KoGhost>();
            ghost.Init(spr, scale, velocity, ko);
        }
    }

    /// <summary>The flying, spinning KO copy plus star sparkles; destroys itself.</summary>
    public class KoGhost : MonoBehaviour
    {
        SpriteRenderer sr;
        SpriteClip clip;
        Vector2 vel;
        float t;
        readonly SpriteRenderer[] stars = new SpriteRenderer[5];

        public void Init(Sprite spr, Vector3 scale, Vector2 velocity, SpriteClip ko)
        {
            clip = ko;
            vel = velocity.sqrMagnitude > 1f ? velocity.normalized * 16f : Vector2.up * 16f;
            var body = new GameObject("Body");
            body.transform.SetParent(transform, false);
            sr = body.AddComponent<SpriteRenderer>();
            sr.sprite = spr;
            sr.sharedMaterial = Art.SpriteMat;
            sr.sortingOrder = 70;
            body.transform.localScale = scale;
            for (int i = 0; i < stars.Length; i++)
            {
                stars[i] = Art.Sprite(transform, "Star", Art.Square, i % 2 == 0 ? new Color(1f, 0.9f, 0.4f) : Color.white, Vector2.zero, new Vector2(0.18f, 0.18f), 71);
                stars[i].transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
            }
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            t += dt;
            transform.position += (Vector3)(vel * dt);
            if (clip == null) sr.transform.localRotation = Quaternion.Euler(0f, 0f, t * 900f);
            else sr.sprite = clip.Frame(t);
            float k = Mathf.Clamp01(1f - t / 0.9f);
            sr.color = new Color(1f, 1f, 1f, k);
            for (int i = 0; i < stars.Length; i++)
            {
                float a = t * 9f + i * Mathf.PI * 2f / stars.Length;
                stars[i].transform.localPosition = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (0.6f + t);
                var c = stars[i].color;
                stars[i].color = new Color(c.r, c.g, c.b, k);
            }
            if (t >= 0.9f) Destroy(gameObject);
        }
    }
}
