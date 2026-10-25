using System.Collections;
using UnityEngine;

namespace WeaponBrawl
{
    /// <summary>Small summoned creature that hops toward the nearest enemy and nibbles it.</summary>
    public class Minion : MonoBehaviour
    {
        const float Speed = 4.5f, Lifetime = 8f, HitInterval = 0.7f, DamageMul = 0.25f, Size = 0.45f;

        Fighter owner;
        float expiresAt, nextHit, hopPhase;

        public static Minion Spawn(Fighter owner, Vector2 position, Color color)
        {
            var go = new GameObject("Minion");
            go.transform.position = position;
            SpriteFactory.Part(go.transform, "Body", SpriteFactory.Square, color, Vector2.zero, Vector2.one * Size, 8);
            SpriteFactory.Part(go.transform, "Eye", SpriteFactory.Square, Color.white, new Vector2(0.1f, 0.06f), new Vector2(0.1f, 0.12f), 9);
            var minion = go.AddComponent<Minion>();
            minion.owner = owner;
            minion.expiresAt = Time.time + Lifetime;
            minion.hopPhase = Random.value * Mathf.PI;
            return minion;
        }

        void Update()
        {
            if (owner == null || owner.IsDead || Time.time >= expiresAt)
            {
                Fx.Burst(transform.position, Color.white, 0.8f);
                Destroy(gameObject);
                return;
            }
            var target = owner.NearestEnemy();
            if (target == null) return;

            Vector2 pos = transform.position;
            float dx = target.transform.position.x - pos.x;
            if (Mathf.Abs(dx) > 0.5f) pos.x += Mathf.Sign(dx) * Speed * Time.deltaTime;
            hopPhase += Time.deltaTime * 12f;
            pos.y = Arena.GroundY + Size * 0.5f + Mathf.Abs(Mathf.Sin(hopPhase)) * 0.25f;
            transform.position = pos;
            transform.localScale = new Vector3(Mathf.Sign(dx), 1f, 1f);

            if (Mathf.Abs(dx) < 0.7f && Time.time >= nextHit)
            {
                nextHit = Time.time + HitInterval;
                owner.DealDamage(target, DamageMul, false, new Vector2(Mathf.Sign(dx) * 1.5f, 1f));
            }
        }
    }

    /// <summary>Cloud that follows the opponent and periodically strikes with thunder.</summary>
    public class ThunderCloud : MonoBehaviour
    {
        public const float Height = 4.2f;
        const float Duration = 6f, Interval = 1.2f, Telegraph = 0.3f, DamageMul = 0.8f, StrikeWidth = 0.7f;
        static readonly Color Lightning = new Color(1f, 0.95f, 0.5f);

        Fighter owner, target;
        float endsAt, nextStrike;
        SpriteRenderer[] puffs;

        public static ThunderCloud Spawn(Fighter owner, Fighter target, Vector2 position)
        {
            var go = new GameObject("ThunderCloud");
            go.transform.position = target != null ? new Vector2(target.transform.position.x, Height) : position;
            var grey = new Color(0.35f, 0.37f, 0.45f, 0.95f);
            var cloud = go.AddComponent<ThunderCloud>();
            cloud.puffs = new[]
            {
                SpriteFactory.Part(go.transform, "Puff", SpriteFactory.Circle, grey, new Vector2(-0.5f, 0f), Vector2.one * 0.9f, 40),
                SpriteFactory.Part(go.transform, "Puff", SpriteFactory.Circle, grey, new Vector2(0.1f, 0.2f), Vector2.one * 1.1f, 41),
                SpriteFactory.Part(go.transform, "Puff", SpriteFactory.Circle, grey, new Vector2(0.6f, -0.05f), Vector2.one * 0.8f, 40),
            };
            cloud.owner = owner;
            cloud.target = target;
            cloud.endsAt = Time.time + Duration;
            cloud.nextStrike = Time.time + 0.6f;
            return cloud;
        }

        void Update()
        {
            if (owner == null || Time.time >= endsAt)
            {
                Destroy(gameObject);
                return;
            }
            if (target != null && !target.IsDead)
            {
                var goal = new Vector3(target.transform.position.x, Height, 0f);
                transform.position = Vector3.Lerp(transform.position, goal, 4f * Time.deltaTime);
            }
            if (Time.time >= nextStrike)
            {
                nextStrike = Time.time + Interval;
                StartCoroutine(Strike());
            }
        }

        IEnumerator Strike()
        {
            foreach (var p in puffs) p.color = new Color(0.75f, 0.75f, 0.45f, 0.95f); // telegraph
            yield return new WaitForSeconds(Telegraph);
            foreach (var p in puffs) p.color = new Color(0.35f, 0.37f, 0.45f, 0.95f);

            float x = transform.position.x;
            Fx.Bolt(new Vector2(x, Height - 0.3f), new Vector2(x, Arena.GroundY), Lightning);
            Fx.Burst(new Vector2(x, Arena.GroundY + 0.2f), Lightning, 1.2f, 0.2f);
            if (owner != null && target != null && !target.IsDead && Mathf.Abs(target.transform.position.x - x) < StrikeWidth)
                owner.DealDamage(target, DamageMul, false, new Vector2(0f, 2f), 0.3f);
        }
    }

    /// <summary>Magic ultimate: ring of fire around the caster that burns enemies and blocks projectiles.</summary>
    public class FireRing : MonoBehaviour
    {
        const float Radius = 2.1f, TickInterval = 0.4f, DamageMul = 0.3f;
        static readonly Color Ember = new Color(1f, 0.7f, 0.25f);

        Fighter owner;
        float endsAt, nextTick;
        Transform outer, inner;

        public static FireRing Spawn(Fighter owner, float duration, Color color)
        {
            var go = new GameObject("FireRing");
            go.transform.SetParent(owner.transform, false);
            go.transform.localPosition = new Vector3(0f, Fighter.HurtboxSize.y * 0.5f, 0f);
            var ring = go.AddComponent<FireRing>();
            ring.owner = owner;
            ring.endsAt = Time.time + duration;
            ring.outer = SpriteFactory.Part(go.transform, "Outer", SpriteFactory.Ring, color, Vector2.zero, Vector2.one * Radius * 2f, 30).transform;
            ring.inner = SpriteFactory.Part(go.transform, "Inner", SpriteFactory.Ring, new Color(1f, 0.85f, 0.3f, 0.8f), Vector2.zero, Vector2.one * Radius * 1.8f, 31).transform;
            owner.SetProjectileBlock(duration);
            return ring;
        }

        void Update()
        {
            if (owner == null || owner.IsDead || Time.time >= endsAt)
            {
                Fx.Ring(transform.position, Ember, Radius * 2.5f, 0.3f);
                Destroy(gameObject);
                return;
            }

            outer.Rotate(0f, 0f, 180f * Time.deltaTime);
            inner.Rotate(0f, 0f, -260f * Time.deltaTime);
            inner.localScale = Vector3.one * Radius * (1.8f + Mathf.Sin(Time.time * 10f) * 0.06f);
            if (Random.value < 0.5f)
                Fx.Burst(transform.position + (Vector3)(Random.insideUnitCircle.normalized * Radius), Ember, 0.35f, 0.3f);

            // Burn enemy projectiles that enter the ring.
            foreach (var p in Projectile.Active.ToArray())
            {
                if (p.owner == owner || Vector2.Distance(p.transform.position, transform.position) > Radius) continue;
                Fx.Burst(p.transform.position, Ember, 0.6f);
                p.Expire();
            }

            if (Time.time < nextTick) return;
            nextTick = Time.time + TickInterval;
            foreach (var f in Fighter.All.ToArray())
            {
                if (f == owner || f.IsDead || Vector2.Distance(f.Center, transform.position) > Radius + 0.4f) continue;
                owner.DealDamage(f, DamageMul, false, new Vector2(Mathf.Sign(f.transform.position.x - owner.transform.position.x) * 3f, 2f));
            }
        }
    }
}
