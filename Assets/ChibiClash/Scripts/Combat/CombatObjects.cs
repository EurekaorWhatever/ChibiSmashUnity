using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>Arrows, magic bolts, fireballs, sword waves, shockwaves and the thrown spear.</summary>
    public class Projectile : MonoBehaviour
    {
        public Fighter Owner;
        public Vector2 Vel;
        public HitSpec Hit;
        public float Radius = 0.3f;
        public float Life = 2f;
        public bool Homing, Pierce, Harmless;
        public bool Destructible = true;
        public float GrowRate;
        public float ReturnAfter = -1f;
        public float ReturnMult;
        public Color TrailColor = Color.clear;
        public Action<Projectile> OnExpire;
        public Action OnGone;

        bool hasHit, returning;
        float trailTimer;

        public static Projectile Spawn(Fighter owner, Vector2 pos, Vector2 vel, HitSpec spec, Color c, Vector2 size, float radius, float life, Sprite sprite)
        {
            var go = new GameObject("Projectile");
            go.transform.SetParent(owner.Match.transform, false);
            go.transform.position = pos;
            Art.Sprite(go.transform, "Glow", Art.Soft, new Color(c.r, c.g, c.b, 0.5f), Vector2.zero, size * 2.2f, 59);
            Art.Sprite(go.transform, "Core", sprite != null ? sprite : Art.Circle, c, Vector2.zero, size, 60);
            var p = go.AddComponent<Projectile>();
            p.Owner = owner;
            p.Vel = vel;
            p.Hit = spec;
            p.Radius = radius;
            p.Life = life;
            p.transform.right = vel.sqrMagnitude > 0.01f ? (Vector3)vel.normalized : Vector3.right;
            return p;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            Life -= dt;
            var target = Owner != null ? Owner.Opponent : null;
            Vector2 pos = transform.position;

            if (Homing && target != null)
            {
                float speed = Vel.magnitude;
                Vel = Vector2.Lerp(Vel, (target.Center - pos).normalized * speed, 3.5f * dt).normalized * speed;
            }

            if (ReturnAfter > 0f && Owner != null)
            {
                ReturnAfter -= dt;
                if (ReturnAfter <= 0f && !returning)
                {
                    returning = true;
                    hasHit = false;
                    Hit.mult = ReturnMult;
                }
            }
            if (returning && Owner != null)
            {
                Vel = (Owner.Center - pos).normalized * 18f;
                if (Vector2.Distance(Owner.Center, pos) < 0.5f) { Destroy(gameObject); return; }
                Life = Mathf.Max(Life, 0.5f);
            }

            pos += Vel * dt;
            transform.position = pos;
            if (Vel.sqrMagnitude > 0.01f && GrowRate <= 0f) transform.right = Vel.normalized;
            if (GrowRate > 0f) transform.localScale += new Vector3(0f, GrowRate * dt, 0f);

            if (TrailColor.a > 0f)
            {
                trailTimer -= dt;
                if (trailTimer <= 0f)
                {
                    trailTimer = 0.03f;
                    Fx.Burst(pos, TrailColor, 1, 0.3f, 0.18f, 0.25f);
                }
            }

            if (!Harmless && target != null && !hasHit)
            {
                if (target.DestroysProjectile(pos, this))
                {
                    Fx.Burst(pos, new Color(1f, 0.7f, 0.3f), 10, 3f, 0.25f);
                    Destroy(gameObject);
                    return;
                }
                float r = Radius * Mathf.Max(1f, transform.localScale.y);
                if (target.OverlapsCircle(pos, r))
                {
                    var spec = Hit;
                    spec.knockback.x = Mathf.Abs(spec.knockback.x) * (Vel.x >= 0f ? 1f : -1f);
                    target.ReceiveHit(Owner, spec);
                    hasHit = true;
                    if (!Pierce && ReturnAfter <= 0f && !returning) { Destroy(gameObject); return; }
                }
            }

            if (Life <= 0f || Mathf.Abs(pos.x) > 40f || pos.y < -30f || pos.y > 40f)
            {
                OnExpire?.Invoke(this);
                Destroy(gameObject);
            }
        }

        void OnDestroy() => OnGone?.Invoke();
    }

    /// <summary>Magic skill: small minions that walk to the opponent and bonk them.</summary>
    public class Minion : MonoBehaviour
    {
        Fighter owner;
        Rigidbody2D rb;
        float life = 6f;
        float attackCd = 0.4f;

        public static void Spawn(Fighter owner, Vector2 pos)
        {
            var go = new GameObject("Minion");
            go.transform.SetParent(owner.Match.transform, false);
            go.transform.position = pos;
            var c = owner.Skin.ultFx;
            Art.Sprite(go.transform, "Body", Art.Circle, Color.Lerp(c, Color.black, 0.2f), Vector2.zero, new Vector2(0.5f, 0.45f), 40);
            Art.Sprite(go.transform, "Eye", Art.Circle, Color.white, new Vector2(0.08f, 0.05f), new Vector2(0.14f, 0.14f), 41);
            Art.Sprite(go.transform, "Pupil", Art.Circle, Color.black, new Vector2(0.1f, 0.05f), new Vector2(0.07f, 0.07f), 42);
            var m = go.AddComponent<Minion>();
            m.owner = owner;
            m.rb = go.AddComponent<Rigidbody2D>();
            m.rb.gravityScale = 3f;
            m.rb.freezeRotation = true;
            var col = go.AddComponent<CircleCollider2D>();
            col.radius = 0.22f;
            Physics2D.IgnoreCollision(col, owner.Collider);
            if (owner.Opponent != null) Physics2D.IgnoreCollision(col, owner.Opponent.Collider);
            foreach (var other in owner.Match.GetComponentsInChildren<Minion>())
                if (other != m) Physics2D.IgnoreCollision(col, other.GetComponent<Collider2D>());
            Fx.Burst(pos, c, 10, 3f, 0.3f);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            life -= dt;
            attackCd -= dt;
            var target = owner != null ? owner.Opponent : null;
            if (target == null || life <= 0f || transform.position.y < -20f)
            {
                Fx.Burst(transform.position, owner != null ? owner.Skin.ultFx : Color.white, 8, 2f, 0.25f);
                Destroy(gameObject);
                return;
            }
            Vector2 d = target.Center - (Vector2)transform.position;
            float dir = Mathf.Sign(d.x);
            transform.localScale = new Vector3(dir, 1f, 1f);
            rb.linearVelocity = new Vector2(Mathf.Abs(d.x) > 0.4f ? dir * 3.5f : 0f, rb.linearVelocity.y);
            if (Mathf.Abs(d.x) < 0.7f && Mathf.Abs(d.y) < 0.9f && attackCd <= 0f)
            {
                attackCd = 1f;
                target.ReceiveHit(owner, HitSpec.Make(0.35f, new Vector2(dir * 2f, 2f), 0.15f));
                rb.linearVelocity = new Vector2(-dir * 2f, 5f);
            }
        }
    }

    /// <summary>Magic skill: a cloud that follows the opponent and strikes with thunder.</summary>
    public class ThunderCloud : MonoBehaviour
    {
        Fighter owner;
        float life = 4.6f;
        float strike = 0.8f;
        SpriteRenderer bolt;
        float boltTimer;

        public static void Spawn(Fighter owner)
        {
            if (owner.Opponent == null) return;
            var go = new GameObject("ThunderCloud");
            go.transform.SetParent(owner.Match.transform, false);
            go.transform.position = owner.Center + Vector2.up * 3f;
            var grey = new Color(0.35f, 0.35f, 0.45f);
            Art.Sprite(go.transform, "C1", Art.Circle, grey, new Vector2(-0.4f, 0f), new Vector2(0.8f, 0.6f), 45);
            Art.Sprite(go.transform, "C2", Art.Circle, grey, new Vector2(0.1f, 0.15f), new Vector2(0.9f, 0.75f), 45);
            Art.Sprite(go.transform, "C3", Art.Circle, grey, new Vector2(0.5f, -0.05f), new Vector2(0.7f, 0.55f), 45);
            var c = go.AddComponent<ThunderCloud>();
            c.owner = owner;
            c.bolt = Art.Sprite(go.transform, "Bolt", Art.Square, new Color(1f, 0.95f, 0.4f), new Vector2(0f, -1.6f), new Vector2(0.14f, 3.2f), 44);
            c.bolt.enabled = false;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            life -= dt;
            var target = owner != null ? owner.Opponent : null;
            if (target == null || life <= 0f) { Destroy(gameObject); return; }

            Vector2 goal = target.Center + Vector2.up * 3.2f;
            transform.position = Vector2.Lerp(transform.position, goal, 2.5f * dt);

            boltTimer -= dt;
            bolt.enabled = boltTimer > 0f;
            strike -= dt;
            if (strike <= 0f)
            {
                strike = 1.1f;
                boltTimer = 0.15f;
                Fx.Burst((Vector2)transform.position + Vector2.down * 3.2f, new Color(1f, 0.95f, 0.4f), 12, 4f, 0.25f);
                if (Mathf.Abs(target.Center.x - transform.position.x) < 0.75f)
                    target.ReceiveHit(owner, HitSpec.Make(0.8f, new Vector2(0f, -3f), 0.35f, true));
            }
        }
    }

    /// <summary>Magic ultimate: ring of fire that burns the opponent and blocks projectiles.</summary>
    public class FireRing : MonoBehaviour
    {
        Fighter owner;
        float tick;

        public static void Spawn(Fighter owner)
        {
            var go = new GameObject("FireRing");
            go.transform.SetParent(owner.transform, false);
            Art.Sprite(go.transform, "Glow", Art.Soft, new Color(1f, 0.4f, 0.05f, 0.35f), Vector2.zero, new Vector2(5f, 5f), 5);
            Art.Sprite(go.transform, "Ring", Art.Ring, new Color(1f, 0.55f, 0.1f, 0.9f), Vector2.zero, new Vector2(4f, 4f), 46);
            go.AddComponent<FireRing>().owner = owner;
        }

        void Update()
        {
            if (owner == null || Time.time >= owner.FireRingUntil) { Destroy(gameObject); return; }
            transform.Rotate(0f, 0f, 180f * Time.deltaTime);
            float s = 1f + Mathf.Sin(Time.time * 10f) * 0.04f;
            transform.localScale = new Vector3(s * owner.Facing, s, 1f);
            var ang = FxRandom.Value * Mathf.PI * 2f;
            Fx.Burst(owner.Center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 2f, new Color(1f, FxRandom.Range(0.3f, 0.8f), 0.1f), 1, 1.5f, 0.3f);

            tick -= Time.deltaTime;
            var target = owner.Opponent;
            if (tick <= 0f && target != null && Vector2.Distance(target.Center, owner.Center) < 2.1f)
            {
                tick = 0.5f;
                float dir = Mathf.Sign(target.Center.x - owner.Center.x);
                target.ReceiveHit(owner, HitSpec.Make(0.4f, new Vector2(dir * 4f, 3f), 0.2f));
            }
        }
    }

    /// <summary>Global particle bursts and floating combat text.</summary>
    public static class Fx
    {
        public class Popup
        {
            public Vector2 pos;
            public string text;
            public Color color;
            public float size, t;
        }

        public static readonly List<Popup> Popups = new List<Popup>();
        static ParticleSystem ps;

        public static void Init(Transform parent)
        {
            var go = new GameObject("FX");
            go.transform.SetParent(parent, false);
            ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.maxParticles = 4000;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0.35f;
            var emission = ps.emission;
            emission.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = Art.ParticleMat;
            r.sortingOrder = 80;
            ps.Play();
            Popups.Clear();
        }

        public static void Burst(Vector2 pos, Color c, int count, float speed, float size, float life = 0.45f)
        {
            if (ps == null) return;
            var ep = new ParticleSystem.EmitParams { position = pos, startColor = c, applyShapeToPosition = false };
            for (int i = 0; i < count; i++)
            {
                ep.velocity = FxRandom.InsideUnitCircle * speed;
                ep.startSize = size * FxRandom.Range(0.6f, 1.3f);
                ep.startLifetime = life * FxRandom.Range(0.7f, 1.3f);
                ps.Emit(ep, 1);
            }
        }

        public static void Text(Vector2 pos, string text, Color c, float size = 1f)
        {
            Popups.Add(new Popup { pos = pos, text = text, color = c, size = size });
        }

        public static void Tick(float dt)
        {
            for (int i = Popups.Count - 1; i >= 0; i--)
            {
                var p = Popups[i];
                p.t += dt;
                p.pos += Vector2.up * dt * 1.2f;
                if (p.t > 1.1f) Popups.RemoveAt(i);
            }
        }
    }
}
