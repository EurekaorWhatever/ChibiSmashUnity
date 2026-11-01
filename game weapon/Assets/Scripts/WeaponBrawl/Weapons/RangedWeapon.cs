using System.Collections;
using UnityEngine;
using static WeaponBrawl.WeaponAnimator;

namespace WeaponBrawl
{
    /// <summary>Bow / crossbow / slingshot. 4-shot combo. Skills: Dash Shot, Bowstring Choke, Arrow Barrage. Ult: Thousand Arrows.</summary>
    public class RangedWeapon : WeaponBehaviour
    {
        const int SlingshotVariant = 2;
        static readonly Color Gold = new Color(1f, 0.85f, 0.35f);

        bool FiresStones => fighter.Variant == SlingshotVariant;
        Sprite ShotSprite => FiresStones ? SpriteFactory.Circle : SpriteFactory.Square;

        protected override IEnumerator AttackRoutine(int step)
        {
            bool finisher = step == Def.comboLength - 1;
            float aim = step == 1 ? 5f : step == 2 ? -4f : 0f;

            anim.ShowAccent(true);
            yield return anim.PoseTo(P(aim, -0.05f, 0f, 1f, 4f, 1f), T(finisher ? 0.3f : 0.12f), Ease.OutQuad); // draw
            if (finisher) Fx.Burst(anim.tip.position, Gold, 0.8f, 0.15f);
            FireArrow(aim, finisher ? 1.5f : 1f, finisher ? 26f : 18f, finisher);
            yield return anim.PoseTo(P(aim + 10f, -0.15f, 0f, 1f, 2f, 0f), T(0.06f), Ease.OutExpo); // recoil
            yield return Wait(T(finisher ? 0.2f : 0.08f));
        }

        Projectile FireArrow(float aimDegrees, float damageMul, float speed, bool heavy = false)
        {
            anim.ShowAccent(false);
            float rad = aimDegrees * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Cos(rad) * Dir, Mathf.Sin(rad));
            Vector2 size = FiresStones ? Vector2.one * (heavy ? 0.3f : 0.2f) : heavy ? new Vector2(0.9f, 0.1f) : new Vector2(0.7f, 0.06f);
            var arrow = Projectile.Spawn(fighter, anim.tip.position, dir * speed, damageMul, ShotSprite,
                heavy ? Gold : WeaponVisuals.ArrowColor, size);
            arrow.radius = heavy ? 0.36f : 0.28f; // same hit size for every variant
            arrow.lifetime = 1.4f;
            arrow.pierce = heavy;
            arrow.knockback = heavy ? new Vector2(5f, 2f) : new Vector2(1.5f, 0.8f);
            return arrow;
        }

        protected override IEnumerator SkillRoutine(int index)
        {
            switch (index)
            {
                case 0: yield return DashShot(); break;
                case 1: yield return BowstringChoke(); break;
                default: yield return ArrowBarrage(); break;
            }
        }

        IEnumerator DashShot()
        {
            anim.ShowAccent(true);
            fighter.Body.SetVelocity(new Vector2(-14f * Dir, 5f)); // hop backwards, still facing forward
            yield return anim.PoseTo(P(-5f, -0.05f, 0f, 1f, 12f, 1f), 0.14f, Ease.OutQuad);
            FireArrow(-5f, 1.3f, 22f);
            yield return anim.PoseTo(P(5f, -0.15f, 0f, 1f, 6f, 0f), 0.06f);
            yield return Wait(0.12f);
            Halt();
        }

        IEnumerator BowstringChoke()
        {
            yield return anim.PoseTo(P(90f, 0.45f, 0.1f, 1f, -10f), T(0.1f), Ease.OutExpo); // bow raised like a garrote
            var target = FirstEnemyIn(0.9f, 1.0f, 1.4f, 1.8f);
            if (target == null)
            {
                yield return Wait(0.3f);
                yield break;
            }

            Hold(target);
            Fx.Popup(target.Center + Vector2.up, "CHOKE", Def.color);
            yield return MoveFighter(target, Local(0.7f, 0f), 0.1f);
            for (int i = 0; i < 3; i++)
            {
                yield return anim.PoseTo(P(80f, 0.4f, 0.05f, 1f, 8f), 0.08f);
                yield return anim.PoseTo(P(100f, 0.45f, 0.15f, 1f, -6f), 0.08f);
                fighter.DealDamage(target, 0.25f, false, Vector2.zero);
            }
            ReleaseHeld();

            // Kick them away
            yield return anim.PoseTo(P(20f, 0.6f, 0f, 1f, -15f), 0.06f, Ease.OutExpo);
            fighter.DealDamage(target, 1.0f, false, new Vector2(14f * Dir, 5f), 0.4f);
            Fx.Burst(target.Center, Color.white, 1.2f);
            yield return Wait(0.2f);
        }

        IEnumerator ArrowBarrage()
        {
            for (int i = 0; i < 7; i++)
            {
                anim.ShowAccent(true);
                yield return anim.PoseTo(P(0f, -0.05f, 0f, 1f, 3f, 1f), T(0.05f));
                FireArrow(Random.Range(-6f, 6f), 0.5f, 20f);
                yield return anim.PoseTo(P(6f, -0.12f, 0f, 1f, 2f, 0f), T(0.03f));
            }
        }

        protected override IEnumerator UltimateRoutine()
        {
            anim.ShowAccent(true);
            yield return anim.PoseTo(P(55f, -0.1f, 0f, 1f, 10f, 1f), 0.35f, Ease.OutQuad);
            Fx.Burst(anim.tip.position, Gold, 1.2f);
            var lead = FireArrow(55f, 1f, 16f, true);
            lead.lifetime = 5f;
            StartCoroutine(SplitArrow(lead));
            yield return anim.PoseTo(P(65f, -0.2f, 0f, 1f, 4f, 0f), 0.08f);
            yield return Wait(0.2f);
        }

        /// <summary>The lead arrow multiplies into a rain of arrows over the opponent.</summary>
        IEnumerator SplitArrow(Projectile lead)
        {
            yield return new WaitForSeconds(0.45f);
            if (lead == null) yield break;
            Vector2 origin = lead.transform.position;
            lead.Expire();
            Fx.Burst(origin, Gold, 2.2f, 0.35f);
            Fx.Popup(origin + Vector2.up * 0.5f, "THOUSAND ARROWS", Gold, 1.2f);

            var target = fighter.NearestEnemy();
            float centerX = target != null ? target.transform.position.x : origin.x + 3f * Dir;
            for (int i = 0; i < 30; i++)
            {
                if (fighter == null) yield break;
                Vector2 start = origin + Random.insideUnitCircle * 0.5f;
                var landing = new Vector2(centerX + Random.Range(-3f, 3f), Arena.GroundY);
                var arrow = Projectile.Spawn(fighter, start, (landing - start).normalized * 22f, 0.3f,
                    ShotSprite, Gold, FiresStones ? Vector2.one * 0.18f : new Vector2(0.6f, 0.06f));
                arrow.radius = 0.24f;
                arrow.destroyOnGround = true;
                arrow.lifetime = 2f;
                arrow.knockback = new Vector2(0.5f, 0.5f);
                yield return new WaitForSeconds(0.02f);
            }
        }

        protected override void OnInterrupted() => anim.ShowAccent(false);
    }
}
