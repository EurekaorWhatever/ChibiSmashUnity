using UnityEngine;

namespace WeaponBrawl
{
    /// <summary>Builds a complete fighter (physics, placeholder body, weapon rig, weapon class, controller) from code.</summary>
    public static class FighterFactory
    {
        static readonly Color PlayerBody = new Color(0.86f, 0.89f, 0.96f);
        static readonly Color OpponentBody = new Color(0.95f, 0.76f, 0.76f);
        static readonly Color Skin = new Color(1f, 0.86f, 0.72f);
        static PhysicsMaterial2D frictionless;

        public static Fighter Create(WeaponType type, int passiveIndex, Vector2 position, bool isPlayer)
        {
            var def = WeaponDatabase.Get(type);
            var go = new GameObject(isPlayer ? "Player" : "Opponent");
            go.transform.position = position;

            var rb = go.AddComponent<Rigidbody2D>();
            rb.gravityScale = 3f;
            rb.freezeRotation = true;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            if (frictionless == null) frictionless = new PhysicsMaterial2D("Fighter") { friction = 0f, bounciness = 0f };
            rb.sharedMaterial = frictionless;

            var col = go.AddComponent<BoxCollider2D>();
            col.size = Fighter.HurtboxSize;
            col.offset = new Vector2(0f, Fighter.HurtboxSize.y * 0.5f);

            // Body (rotates for leaning; the weapon pivot is a child so it leans too)
            var body = new GameObject("Body").transform;
            body.SetParent(go.transform, false);
            var torso = SpriteFactory.Part(body, "Torso", SpriteFactory.Square, isPlayer ? PlayerBody : OpponentBody, new Vector2(0f, 0.7f), new Vector2(0.75f, 1.2f), 2);
            SpriteFactory.Part(body, "Belt", SpriteFactory.Square, def.color, new Vector2(0f, 0.5f), new Vector2(0.77f, 0.1f), 3);
            SpriteFactory.Part(body, "Head", SpriteFactory.Circle, Skin, new Vector2(0f, 1.62f), Vector2.one * 0.62f, 3);
            SpriteFactory.Part(body, "Headband", SpriteFactory.Square, def.color, new Vector2(0f, 1.74f), new Vector2(0.6f, 0.1f), 4);
            SpriteFactory.Part(body, "Eye", SpriteFactory.Square, new Color(0.1f, 0.1f, 0.15f), new Vector2(0.15f, 1.62f), new Vector2(0.08f, 0.12f), 5);

            var pivot = new GameObject("WeaponPivot").transform;
            pivot.SetParent(body, false);
            pivot.localPosition = new Vector2(0.18f, 1.0f);
            var parts = WeaponVisuals.Build(type, pivot, def.color);

            var fighter = go.AddComponent<Fighter>();
            fighter.BodyRenderer = torso;

            var anim = go.AddComponent<WeaponAnimator>();
            anim.body = body;
            anim.pivot = pivot;
            anim.weapon = parts.root;
            anim.tip = parts.tip;
            anim.accent = parts.accent;
            anim.Setup(WeaponVisuals.RestPose(type), def.color, WeaponVisuals.AccentModeFor(type));

            WeaponBehaviour weapon;
            switch (type)
            {
                case WeaponType.Melee: weapon = go.AddComponent<MeleeWeapon>(); break;
                case WeaponType.Heavy: weapon = go.AddComponent<HeavyWeapon>(); break;
                case WeaponType.Ranged: weapon = go.AddComponent<RangedWeapon>(); break;
                case WeaponType.Magic: weapon = go.AddComponent<MagicWeapon>(); break;
                default: weapon = go.AddComponent<PolearmWeapon>(); break;
            }

            fighter.Init(isPlayer ? "P1" : "CPU", def, passiveIndex, weapon, anim);
            weapon.Init(fighter, anim);

            if (isPlayer) go.AddComponent<PlayerController>();
            else go.AddComponent<SimpleAI>();
            return fighter;
        }
    }
}
