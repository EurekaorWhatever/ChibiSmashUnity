using System.Collections;
using UnityEngine;
using static WeaponBrawl.WeaponAnimator;

namespace WeaponBrawl
{
    /// <summary>Hammer / mace / claymore. 5-hit combo. Skills: Grab, Spin. Ult: Smash (shockwave).</summary>
    public class HeavyWeapon : WeaponBehaviour
    {
        protected override IEnumerator AttackRoutine(int step)
        {
            anim.SetTrail(true);
            switch (step)
            {
                case 0: // overhead slam
                    yield return anim.PoseTo(P(165f, -0.15f, 0.1f, 1f, 12f), T(0.2f), Ease.OutQuad);
                    yield return anim.PoseTo(P(-45f, 0.15f, -0.1f, 1f, -14f), T(0.1f), Ease.InQuad);
                    Smack(1.2f, 0.6f, 1.6f, 1.4f, 1.0f, new Vector2(2f, 3f), 0.08f);
                    break;
                case 1: // rising uppercut
                    yield return anim.PoseTo(P(-150f, -0.1f, -0.1f, 1f, 6f), T(0.16f));
                    yield return anim.PoseTo(P(70f, 0.1f, 0.15f, 1f, -6f), T(0.1f), Ease.InQuad);
                    Smack(1.1f, 1.3f, 1.6f, 1.8f, 1.0f, new Vector2(1.5f, 7f), 0.05f);
                    break;
                case 2: // wide horizontal sweep
                    yield return anim.PoseTo(P(20f, -0.4f, 0f, 1f, 15f), T(0.16f));
                    Lunge(5f);
                    yield return anim.PoseTo(P(-10f, 0.4f, 0f, 1.15f, -15f), T(0.1f), Ease.OutExpo);
                    Smack(1.3f, 1.0f, 2.2f, 1.2f, 1.1f, new Vector2(5f, 1.5f), 0.06f);
                    Halt();
                    break;
                case 3: // backhand return swing
                    yield return anim.PoseTo(P(-60f, 0.2f, -0.1f), T(0.12f));
                    yield return anim.PoseTo(P(140f, -0.1f, 0.1f, 1f, 10f), T(0.12f), Ease.InQuad);
                    Smack(0.6f, 1.6f, 1.8f, 1.6f, 0.9f, new Vector2(2f, 4f), 0.05f);
                    break;
                default: // leaping ground-pound finisher
                    fighter.Body.SetVelocity(new Vector2(3f * Dir, 9f));
                    yield return anim.PoseTo(P(175f, -0.2f, 0.2f, 1f, 18f), T(0.25f), Ease.OutQuad);
                    yield return WaitForLanding(0.6f);
                    Halt();
                    yield return anim.PoseTo(P(-60f, 0.2f, -0.15f, 1f, -20f), T(0.06f), Ease.InQuad);
                    Smack(1.3f, 0.5f, 2.6f, 1.4f, 1.6f, new Vector2(6f, 5f), 0.18f);
                    Fx.Ring(Local(1.3f, 0.1f), Def.color, 2.5f, 0.3f);
                    break;
            }
            anim.SetTrail(false);
            yield return Wait(T(0.18f));
        }

        void Smack(float x, float y, float w, float h, float damageMul, Vector2 knockback, float shake)
        {
            if (HitBox(x, y, w, h, damageMul, knockback).Count > 0) Fx.Shake(shake, 0.12f);
            anim.Punch(0.08f);
        }

        protected override IEnumerator SkillRoutine(int index)
        {
            if (index == 0) yield return Grab();
            else yield return Spin();
        }

        IEnumerator Grab()
        {
            yield return anim.PoseTo(P(0f, 0.35f, -0.1f, 1.1f, -10f), T(0.12f), Ease.OutExpo); // reach out
            var target = FirstEnemyIn(1.0f, 1.0f, 1.6f, 1.8f);
            if (target == null)
            {
                yield return Wait(0.3f);
                yield break;
            }

            Hold(target);
            Fx.Popup(target.Center + Vector2.up, "GRABBED", Def.color);

            // Lift overhead...
            yield return Parallel(
                anim.PoseTo(P(150f, -0.1f, 0.2f, 1f, 10f), 0.3f),
                MoveFighter(target, Local(0.2f, 2.6f), 0.3f, Ease.OutQuad));
            yield return Wait(0.12f);

            // ...and smash into the ground in front.
            Vector2 ground = Local(1.3f, Arena.GroundY);
            yield return Parallel(
                anim.PoseTo(P(-50f, 0.2f, -0.1f, 1f, -18f), 0.12f, Ease.InQuad),
                MoveFighter(target, ground, 0.12f, Ease.InQuad));
            ReleaseHeld();
            fighter.DealDamage(target, 2.2f, false, new Vector2(2f * Dir, 4f), 0.6f);
            Fx.Shake(0.25f, 0.2f);
            Fx.Ring(ground, Def.color, 3f, 0.35f);
            anim.Punch(0.15f);
            yield return Wait(0.25f);
        }

        IEnumerator Spin()
        {
            const float duration = 1.2f;
            anim.SetTrail(true);
            float angle = anim.Current.angle;
            float hitTimer = 0f;
            for (float t = 0f; t < duration; t += Time.deltaTime)
            {
                angle -= 1080f * Time.deltaTime;
                anim.SetPose(P(angle, 0f, 0f, 1.1f, Mathf.Sin(t * 40f) * 6f));
                fighter.Body.SetVelocity(new Vector2(2.5f * Dir, fighter.Body.GetVelocity().y)); // tornado drifts forward
                hitTimer -= Time.deltaTime;
                if (hitTimer <= 0f)
                {
                    hitTimer = 0.2f;
                    HitBox(0f, 1.0f, 3.0f, 1.8f, 0.35f, new Vector2(1.5f, 1.5f));
                }
                yield return null;
            }
            Halt();
            anim.NormalizeAngle();

            // Finishing blow
            yield return anim.PoseTo(P(160f, -0.1f, 0.1f, 1f, 12f), T(0.1f));
            yield return anim.PoseTo(P(-45f, 0.2f, -0.1f, 1f, -14f), T(0.08f), Ease.InQuad);
            Smack(1.3f, 0.7f, 2.0f, 1.6f, 1.4f, new Vector2(7f, 4f), 0.15f);
            yield return Wait(T(0.2f));
        }

        protected override IEnumerator UltimateRoutine()
        {
            Fx.Popup(fighter.Center + Vector2.up * 1.5f, "SMASH!", Def.color, 1.4f);
            fighter.Body.SetVelocity(new Vector2(0f, 15f));
            yield return anim.PoseTo(P(175f, -0.2f, 0.25f, 1.2f, 20f), 0.3f, Ease.OutQuad);
            for (float t = 0f; t < 0.6f && fighter.Body.GetVelocity().y > 0f; t += Time.deltaTime) yield return null;

            fighter.Body.SetVelocity(new Vector2(0f, -25f)); // dive
            yield return WaitForLanding(1f);
            yield return anim.PoseTo(P(-65f, 0.25f, -0.2f, 1.2f, -22f), 0.05f, Ease.InQuad);

            Vector2 impact = Local(1.3f, 0.1f);
            HitBox(1.3f, 0.8f, 3.5f, 2.0f, 2.5f, new Vector2(8f, 8f), 0.5f);
            Fx.Shake(0.45f, 0.35f);
            Fx.Ring(impact, Def.color, 6f, 0.45f);
            Fx.Burst(impact, Color.white, 2f, 0.3f);
            anim.Punch(0.2f, 0.2f);

            // Shockwaves travelling along the floor in both directions
            foreach (int side in new[] { -1, 1 })
            {
                var wave = Projectile.Spawn(fighter, new Vector2(impact.x, 0.45f), new Vector2(13f * side, 0f), 1.5f,
                    SpriteFactory.Circle, new Color(Def.color.r, Def.color.g, Def.color.b, 0.85f), new Vector2(1.2f, 0.9f));
                wave.pierce = true;
                wave.lifetime = 0.9f;
                wave.alignToVelocity = false;
                wave.knockback = new Vector2(5f, 9f);
                wave.stun = 0.5f;
                wave.onUpdate = p => p.Visual.transform.localScale = new Vector3(1.2f, 0.9f + Mathf.Sin(p.Age * 30f) * 0.15f, 1f);
            }
            yield return Wait(0.4f);
        }
    }
}
