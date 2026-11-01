using System;
using System.Collections.Generic;
using UnityEngine;

namespace WeaponBrawl
{
    /// <summary>Generic projectile: arrows, waves, fireballs, thrown spear, shockwaves.</summary>
    public class Projectile : MonoBehaviour
    {
        public static readonly List<Projectile> Active = new List<Projectile>();

        public Fighter owner;
        public Vector2 velocity;
        public float damageMul = 1f;
        public float radius = 0.3f;
        public float lifetime = 2f;
        public bool pierce;
        public bool destroyOnGround;
        public bool alignToVelocity = true;
        public float gravity;
        public Vector2 knockback = new Vector2(2f, 1f);
        public float stun;
        public Fighter homingTarget;
        public float homingTurnRate = 180f;
        public Action<Projectile> onUpdate;
        public Action<Projectile> onExpire;

        public float Age { get; private set; }
        public SpriteRenderer Visual { get; private set; }

        readonly HashSet<Fighter> alreadyHit = new HashSet<Fighter>();
        float baseAlpha = 1f;
        bool expired;

        /// <param name="sprite">Null for no default visual (add your own children).</param>
        public static Projectile Spawn(Fighter owner, Vector2 position, Vector2 velocity, float damageMul, Sprite sprite, Color color, Vector2 size)
        {
            var go = new GameObject("Projectile");
            go.transform.position = position;
            var p = go.AddComponent<Projectile>();
            p.owner = owner;
            p.velocity = velocity;
            p.damageMul = damageMul;
            p.radius = Mathf.Max(size.x, size.y) * 0.4f;
            if (sprite != null)
            {
                p.Visual = SpriteFactory.Part(go.transform, "Visual", sprite, color, Vector2.zero, size, 20);
                p.baseAlpha = color.a;
            }
            p.UpdateRotation();
            return p;
        }

        void OnEnable() => Active.Add(this);
        void OnDisable() => Active.Remove(this);

        void Update()
        {
            if (expired) return;
            if (owner == null)
            {
                Destroy(gameObject);
                return;
            }

            float dt = Time.deltaTime;
            Age += dt;
            if (homingTarget != null && !homingTarget.IsDead)
            {
                Vector2 desired = (homingTarget.Center - (Vector2)transform.position).normalized * velocity.magnitude;
                velocity = Vector3.RotateTowards(velocity, desired, homingTurnRate * Mathf.Deg2Rad * dt, 0f);
            }
            velocity.y -= gravity * dt;

            onUpdate?.Invoke(this);
            if (expired) return;

            transform.position += (Vector3)(velocity * dt);
            UpdateRotation();

            if (Visual != null && lifetime - Age < 0.15f)
            {
                var c = Visual.color;
                c.a = baseAlpha * Mathf.Clamp01((lifetime - Age) / 0.15f);
                Visual.color = c;
            }

            CheckHits();
            if (expired) return;

            Vector2 pos = transform.position;
            if (Age >= lifetime || Mathf.Abs(pos.x) > Arena.HalfWidth + 4f || (destroyOnGround && pos.y <= Arena.GroundY))
                Expire();
        }

        void UpdateRotation()
        {
            if (alignToVelocity && velocity.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(velocity.y, velocity.x) * Mathf.Rad2Deg);
        }

        void CheckHits()
        {
            var box = new Bounds(transform.position, new Vector3(radius * 2f, radius * 2f, 10f));
            foreach (var f in Fighter.All.ToArray())
            {
                if (f == owner || f.IsDead || alreadyHit.Contains(f) || !box.Intersects(f.Bounds)) continue;
                alreadyHit.Add(f);

                if (f.BlocksProjectiles)
                {
                    Fx.Burst(transform.position, Color.white, 0.7f);
                    Fx.Popup(f.Center + Vector2.up, "BLOCKED", new Color(0.6f, 0.9f, 1f));
                    Expire();
                    return;
                }

                float dir = Mathf.Abs(velocity.x) > 0.01f ? Mathf.Sign(velocity.x) : Mathf.Sign(f.transform.position.x - transform.position.x);
                owner.DealDamage(f, damageMul, true, new Vector2(knockback.x * dir, knockback.y), stun);
                Fx.Burst(transform.position, Visual != null ? Visual.color : Color.white, 0.6f, 0.2f);
                if (!pierce)
                {
                    Expire();
                    return;
                }
            }
        }

        /// <summary>Allows hitting the same targets again (e.g. a returning spear).</summary>
        public void ResetHits() => alreadyHit.Clear();

        public void Expire()
        {
            if (expired) return;
            expired = true;
            onExpire?.Invoke(this);
            Destroy(gameObject);
        }
    }
}
