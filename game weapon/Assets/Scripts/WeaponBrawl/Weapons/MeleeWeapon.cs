using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using static WeaponBrawl.WeaponAnimator;

namespace WeaponBrawl
{
    /// <summary>Sword / rapier. 4-hit combo. Skills: Dash and Thrust, Parry, Thrust Barrage. Ult: Sword Aura.</summary>
    public class MeleeWeapon : WeaponBehaviour
    {
        const float AuraDuration = 10f;
        const float ParryWindow = 0.4f;
        static readonly Color AuraColor = new Color(0.45f, 0.95f, 1f);
        static readonly Color ParryColor = new Color(1f, 0.95f, 0.55f);

        float auraEndsAt;
        bool parrySucceeded;
        Fighter parriedAttacker;

        bool AuraActive => Time.time < auraEndsAt;

        public override void Init(Fighter owner, WeaponAnimator animator)
        {
            base.Init(owner, animator);
            owner.Parried += (attacker, _) =>
            {
                parrySucceeded = true;
                parriedAttacker = attacker;
            };
        }

        protected override IEnumerator AttackRoutine(int step)
        {
            anim.SetTrail(true);
            switch (step)
            {
                case 0: // rising guard -> downward diagonal slash
                    yield return anim.PoseTo(P(115f, -0.1f, 0.1f, 1f, 5f), T(0.10f), Ease.OutQuad);
                    yield return anim.PoseTo(P(-70f, 0.1f, 0f, 1f, -8f), T(0.08f), Ease.InQuad);
                    Strike(0.9f, 1.0f, 1.5f, 1.6f, 1.0f, new Vector2(2f, 1f));
                    break;
                case 1: // upward slash
                    yield return anim.PoseTo(P(-95f, 0f, -0.05f), T(0.07f));
                    yield return anim.PoseTo(P(105f, 0.1f, 0.1f, 1f, 6f), T(0.08f), Ease.InQuad);
                    Strike(0.9f, 1.3f, 1.5f, 1.8f, 1.0f, new Vector2(1.5f, 4f));
                    break;
                case 2: // horizontal sweep with a step forward
                    yield return anim.PoseTo(P(30f, -0.25f, 0f, 1f, 8f), T(0.08f));
                    Lunge(7f);
                    yield return anim.PoseTo(P(-15f, 0.3f, 0f, 1.1f, -10f), T(0.07f), Ease.OutExpo);
                    Strike(1.0f, 1.0f, 1.8f, 1.0f, 1.1f, new Vector2(3f, 1f));
                    Halt();
                    break;
                default: // thrust finisher
                    yield return anim.PoseTo(P(0f, -0.4f, 0f, 1f, 6f), T(0.12f), Ease.OutQuad);
                    yield return anim.PoseTo(P(0f, 0.5f, 0f, 1.35f, -12f), T(0.06f), Ease.OutExpo);
                    Strike(1.4f, 1.0f, 2.0f, 0.7f, 1.4f, new Vector2(6f, 2f));
                    Fx.Shake(0.08f, 0.1f);
                    break;
            }
            anim.SetTrail(false);
            yield return Wait(T(0.1f));
        }

        /// <summary>A normal-attack hit. While Sword Aura is active it also fires a wave.</summary>
        void Strike(float x, float y, float w, float h, float damageMul, Vector2 knockback)
        {
            HitBox(x, y, w, h, damageMul, knockback);
            if (AuraActive) FireWave();
        }

        void FireWave()
        {
            var wave = Projectile.Spawn(fighter, Local(1.0f, 1.0f), new Vector2(14f * Dir, 0f), 0.6f,
                SpriteFactory.Circle, AuraColor, new Vector2(0.35f, 1.6f));
            wave.pierce = true;
            wave.lifetime = 0.7f;
            wave.knockback = new Vector2(2f, 1f);
        }

        protected override IEnumerator SkillRoutine(int index)
        {
            switch (index)
            {
                case 0: yield return DashAndThrust(); break;
                case 1: yield return Parry(); break;
                default: yield return ThrustBarrage(); break;
            }
        }

        IEnumerator DashAndThrust()
        {
            yield return anim.PoseTo(P(0f, -0.45f, 0f, 1f, 10f), T(0.12f), Ease.OutQuad);
            anim.SetTrail(true);
            yield return anim.PoseTo(P(0f, 0.45f, 0f, 1.4f, -15f), 0.04f, Ease.OutExpo);
            var hit = new HashSet<Fighter>();
            for (float t = 0f; t < 0.2f; t += Time.deltaTime)
            {
                fighter.Body.SetVelocity(new Vector2(20f * Dir, 0f));
                HitBox(1.1f, 1.0f, 1.8f, 1.0f, 1.6f, new Vector2(5f, 2f), 0f, hit);
                yield return null;
            }
            Halt();
            yield return Wait(T(0.15f));
        }

        IEnumerator Parry()
        {
            parrySucceeded = false;
            parriedAttacker = null;
            yield return anim.PoseTo(P(80f, 0.35f, 0.05f, 1f, 4f), 0.05f, Ease.OutExpo); // blade raised in front
            fighter.IsParrying = true;
            anim.Tint(ParryColor);
            Fx.Ring(anim.tip.position, ParryColor, 1.2f, 0.2f);
            for (float t = 0f; t < ParryWindow && !parrySucceeded; t += Time.deltaTime) yield return null;
            fighter.IsParrying = false;
            anim.Tint(AuraActive ? AuraColor : (Color?)null);

            if (!parrySucceeded)
            {
                // Whiffed parry: short, punishable recovery.
                yield return anim.PoseTo(P(60f, 0.2f, 0f), 0.1f);
                yield return Wait(0.25f);
                yield break;
            }

            // Riposte
            if (parriedAttacker != null) fighter.FaceTowards(parriedAttacker);
            anim.SetTrail(true);
            yield return anim.PoseTo(P(120f, -0.05f, 0.1f, 1f, 6f), 0.06f);
            yield return anim.PoseTo(P(-60f, 0.2f, 0f, 1.15f, -12f), 0.06f, Ease.InQuad);
            HitBox(1.0f, 1.0f, 1.8f, 1.6f, 0.8f, new Vector2(4f, 3f));
            yield return Wait(0.15f);
        }

        IEnumerator ThrustBarrage()
        {
            anim.SetTrail(true);
            const int thrusts = 8;
            for (int i = 0; i < thrusts; i++)
            {
                bool last = i == thrusts - 1;
                float y = (i % 2 == 0 ? 0.12f : -0.12f) + Random.Range(-0.06f, 0.06f);
                yield return anim.PoseTo(P(Random.Range(-6f, 6f), -0.25f, y, 1f, 4f), T(0.04f));
                yield return anim.PoseTo(P(0f, 0.45f, y, 1.3f, -6f), T(0.04f), Ease.OutExpo);
                HitBox(1.3f, 1.0f + y, 1.9f, 0.8f, 0.35f, last ? new Vector2(6f, 2f) : new Vector2(0.6f, 0.3f));
            }
            yield return Wait(T(0.15f));
        }

        protected override IEnumerator UltimateRoutine()
        {
            yield return anim.PoseTo(P(90f, 0f, 0.25f, 1.1f), 0.25f, Ease.OutBack);
            Fx.Burst(anim.tip.position, AuraColor, 2.5f, 0.4f);
            Fx.Ring(fighter.Center, AuraColor, 4f, 0.5f);
            Fx.Popup(fighter.Center + Vector2.up * 1.5f, "SWORD AURA", AuraColor, 1.3f);
            auraEndsAt = Time.time + AuraDuration;
            anim.Tint(AuraColor);
            anim.SetTrailColor(AuraColor);
            StartCoroutine(AuraTimer());
            yield return Wait(0.2f);
        }

        IEnumerator AuraTimer()
        {
            while (AuraActive) yield return null;
            anim.Tint(null);
            anim.SetTrailColor(Def.color);
        }

        protected override void OnInterrupted() => anim.Tint(AuraActive ? AuraColor : (Color?)null);
    }
}
