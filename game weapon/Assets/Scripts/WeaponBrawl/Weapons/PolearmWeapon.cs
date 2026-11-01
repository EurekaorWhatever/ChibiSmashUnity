using System.Collections;
using UnityEngine;
using static WeaponBrawl.WeaponAnimator;

namespace WeaponBrawl
{
    /// <summary>Spear / lance / halberd. 4-hit combo. Skills: Leap Strike, Spin Guard, Spear Throw. Ult: Polearm Kick.</summary>
    public class PolearmWeapon : WeaponBehaviour
    {
        bool spearAway;
        Transform impaledSpear;

        public override bool CanAct => base.CanAct && !spearAway;

        protected override IEnumerator AttackRoutine(int step)
        {
            anim.SetTrail(true);
            switch (step)
            {
                case 0: // quick jab
                    yield return anim.PoseTo(P(0f, -0.35f, 0f, 1f, 5f), T(0.08f));
                    yield return anim.PoseTo(P(0f, 0.35f, 0f, 1.2f, -6f), T(0.06f), Ease.OutExpo);
                    HitBox(1.9f, 1.05f, 2.4f, 0.7f, 0.9f, new Vector2(2f, 0.8f));
                    break;
                case 1: // high jab
                    yield return anim.PoseTo(P(12f, -0.35f, 0.1f, 1f, 5f), T(0.08f));
                    yield return anim.PoseTo(P(12f, 0.35f, 0.1f, 1.2f, -6f), T(0.06f), Ease.OutExpo);
                    HitBox(1.8f, 1.45f, 2.4f, 0.8f, 0.9f, new Vector2(2f, 1.5f));
                    break;
                case 2: // overhead sweep
                    yield return anim.PoseTo(P(130f, -0.2f, 0.15f, 1f, 10f), T(0.12f), Ease.OutQuad);
                    yield return anim.PoseTo(P(-25f, 0.2f, 0f, 1.1f, -10f), T(0.09f), Ease.InQuad);
                    HitBox(1.7f, 0.8f, 2.6f, 1.6f, 1.1f, new Vector2(2.5f, 3f));
                    break;
                default: // lunging thrust
                    yield return anim.PoseTo(P(0f, -0.5f, 0f, 1f, 10f), T(0.14f), Ease.OutQuad);
                    Lunge(8f);
                    yield return anim.PoseTo(P(0f, 0.5f, 0f, 1.45f, -14f), T(0.06f), Ease.OutExpo);
                    HitBox(2.2f, 1.0f, 2.8f, 0.8f, 1.4f, new Vector2(6f, 2f));
                    yield return Wait(0.08f);
                    Halt();
                    break;
            }
            anim.SetTrail(false);
            yield return Wait(T(0.12f));
        }

        protected override IEnumerator SkillRoutine(int index)
        {
            switch (index)
            {
                case 0: yield return LeapStrike(); break;
                case 1: yield return SpinGuard(); break;
                default: yield return SpearThrow(); break;
            }
        }

        IEnumerator LeapStrike()
        {
            yield return anim.PoseTo(P(20f, 0f, 0f, 1.6f), T(0.12f), Ease.OutBack); // extend the weapon
            fighter.Body.SetVelocity(new Vector2(5f * Dir, 12f));
            yield return anim.PoseTo(P(135f, -0.2f, 0.2f, 1.6f, 15f), 0.28f, Ease.OutQuad);
            for (float t = 0f; t < 0.5f && fighter.Body.GetVelocity().y > 0f; t += Time.deltaTime) yield return null; // apex

            anim.SetTrail(true);
            fighter.Body.SetVelocity(new Vector2(2f * Dir, -18f));
            yield return anim.PoseTo(P(-55f, 0.25f, -0.1f, 1.6f, -20f), 0.1f, Ease.InQuad);
            yield return WaitForLanding(0.6f);
            Halt();
            HitBox(2.0f, 0.6f, 3.2f, 1.6f, 1.8f, new Vector2(4f, 6f), 0.3f);
            Fx.Shake(0.2f, 0.15f);
            Fx.Ring(Local(2.2f, 0.1f), Def.color, 3f, 0.3f);
            anim.Punch(0.15f);
            yield return Wait(0.2f);
        }

        IEnumerator SpinGuard()
        {
            const float duration = 1.2f;
            fighter.SetProjectileBlock(duration);
            Fx.Popup(fighter.Center + Vector2.up * 1.3f, "GUARD", Def.color);
            anim.SetTrail(true);
            float angle = anim.Current.angle;
            for (float t = 0f; t < duration; t += Time.deltaTime)
            {
                angle -= 1440f * Time.deltaTime;
                anim.SetPose(P(angle, 0.35f, 0.1f, 0.9f));
                yield return null;
            }
            anim.NormalizeAngle();
        }

        IEnumerator SpearThrow()
        {
            yield return anim.PoseTo(P(15f, -0.45f, 0.15f, 1f, 12f), T(0.15f), Ease.OutQuad);
            yield return anim.PoseTo(P(5f, 0.35f, 0.1f, 1f, -10f), 0.05f, Ease.OutExpo);
            anim.SetWeaponVisible(false);
            spearAway = true;

            var spear = Projectile.Spawn(fighter, Local(0.8f, 1.15f), new Vector2(18f * Dir, 0f), 1.2f, null, Color.white, new Vector2(2f, 0.3f));
            WeaponVisuals.BuildSpear(spear.transform, Def.color, 20, fighter.Variant);
            spear.radius = 0.4f;
            spear.pierce = true;
            spear.lifetime = 3f;
            spear.knockback = new Vector2(2f, 1f);

            bool returning = false;
            spear.onUpdate = p =>
            {
                if (!returning && p.Age > 0.4f)
                {
                    returning = true; // call it back: can hit again on the way home
                    p.ResetHits();
                    p.damageMul = 0.8f;
                }
                if (!returning) return;
                Vector2 toOwner = fighter.Center - (Vector2)p.transform.position;
                p.velocity = toOwner.normalized * 22f;
                if (toOwner.magnitude < 0.6f) p.Expire();
            };
            spear.onExpire = _ =>
            {
                spearAway = false;
                anim.SetWeaponVisible(true);
                Fx.Burst(anim.tip.position, Def.color, 0.8f);
            };
            yield return Wait(0.15f);
        }

        protected override IEnumerator UltimateRoutine()
        {
            var target = fighter.NearestEnemy(7f);
            if (target != null)
            {
                fighter.FaceTowards(target);
                var dest = new Vector2(target.transform.position.x - 1.6f * Dir, fighter.transform.position.y);
                yield return Parallel(
                    anim.PoseTo(P(0f, -0.4f, 0f, 1f, 12f), 0.15f),
                    MoveFighter(fighter, dest, 0.15f));
            }

            // Stab into the chest
            anim.SetTrail(true);
            yield return anim.PoseTo(P(0f, 0.4f, 0f, 1.3f, -12f), 0.05f, Ease.OutExpo);
            target = FirstEnemyIn(1.8f, 1.0f, 2.4f, 1.0f);
            if (target == null)
            {
                yield return Wait(0.3f);
                yield break;
            }

            Hold(target);
            fighter.DealDamage(target, 1.5f, false, Vector2.zero);
            Fx.Popup(target.Center + Vector2.up, "IMPALED", Def.color, 1.2f);
            Fx.Shake(0.15f, 0.12f);

            // The spear stays stuck in the opponent while we jump.
            anim.SetWeaponVisible(false);
            impaledSpear = new GameObject("ImpaledSpear").transform;
            impaledSpear.position = target.Center - new Vector2(1.35f * Dir, 0f);
            impaledSpear.localScale = new Vector3(Dir, 1f, 1f);
            WeaponVisuals.BuildSpear(impaledSpear, Def.color, 20, fighter.Variant);
            yield return Wait(0.2f);

            fighter.Body.SetVelocity(new Vector2(-2f * Dir, 11f));
            yield return anim.PoseTo(P(90f, -0.2f, 0.2f, 1f, 25f), 0.3f, Ease.OutQuad); // coil in the air

            // Flying kick into the end of the handle
            Vector2 handleEnd = (Vector2)impaledSpear.position - new Vector2(0.45f * Dir, 0f);
            Vector2 kickFrom = handleEnd - new Vector2(0.5f * Dir, 0.9f);
            yield return Parallel(
                anim.PoseTo(P(-30f, 0.3f, -0.3f, 1f, -40f), 0.1f, Ease.InQuad),
                MoveFighter(fighter, kickFrom, 0.1f, Ease.InQuad));

            impaledSpear.position += new Vector3(0.45f * Dir, 0f, 0f); // driven deeper
            ReleaseHeld();
            fighter.DealDamage(target, 3.0f, false, new Vector2(12f * Dir, 6f), 0.6f);
            impaledSpear.SetParent(target.transform, true);
            Fx.Shake(0.4f, 0.3f);
            Fx.Burst(target.Center, Color.white, 2.5f, 0.3f);
            Fx.Ring(target.Center, Def.color, 4f, 0.4f);
            anim.Punch(0.2f);

            yield return Wait(0.35f);
            ClearImpaledSpear();
        }

        void ClearImpaledSpear()
        {
            if (impaledSpear != null) Destroy(impaledSpear.gameObject);
            impaledSpear = null;
            if (!spearAway) anim.SetWeaponVisible(true);
        }

        protected override void OnInterrupted() => ClearImpaledSpear();
    }
}
