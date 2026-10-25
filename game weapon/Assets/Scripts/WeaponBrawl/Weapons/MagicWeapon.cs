using System.Collections;
using UnityEngine;
using static WeaponBrawl.WeaponAnimator;

namespace WeaponBrawl
{
    /// <summary>Staff magic. 4-cast combo. Skills: Fireball, Summon Minions, Thunder Cloud. Ult: Fire Ring.</summary>
    public class MagicWeapon : WeaponBehaviour
    {
        static readonly Color Fire = new Color(1f, 0.5f, 0.15f);
        static readonly Color Ember = new Color(1f, 0.8f, 0.3f);

        // staff wind-up and release angles for each combo step
        static readonly float[] WindUp = { 110f, 160f, -60f, 120f };
        static readonly float[] Release = { 15f, -10f, 30f, 5f };

        protected override IEnumerator AttackRoutine(int step)
        {
            bool finisher = step == Def.comboLength - 1;
            int i = Mathf.Min(step, WindUp.Length - 1);
            yield return anim.PoseTo(P(WindUp[i], -0.1f, 0.1f, 1f, 5f, 1f), T(finisher ? 0.22f : 0.1f), Ease.OutQuad);
            yield return anim.PoseTo(P(Release[i], 0.2f, 0f, 1f, -6f, 0f), T(0.06f), Ease.OutExpo);

            Color c = Color.Lerp(Def.color, Color.white, 0.25f);
            var bolt = Projectile.Spawn(fighter, anim.tip.position, new Vector2(15f * Dir, 0f), finisher ? 1.4f : 1f,
                SpriteFactory.Circle, c, Vector2.one * (finisher ? 0.5f : 0.32f));
            bolt.lifetime = 1.2f;
            bolt.knockback = finisher ? new Vector2(4f, 2f) : new Vector2(1.5f, 0.8f);
            Fx.Burst(anim.tip.position, c, 0.6f, 0.15f);
            yield return Wait(T(0.12f));
        }

        protected override IEnumerator SkillRoutine(int index)
        {
            switch (index)
            {
                case 0: yield return Fireball(); break;
                case 1: yield return SummonMinions(); break;
                default: yield return CastThunderCloud(); break;
            }
        }

        IEnumerator Fireball()
        {
            yield return anim.PoseTo(P(140f, -0.15f, 0.15f, 1f, 8f, 1f), T(0.18f));
            yield return anim.PoseTo(P(10f, 0.25f, 0f, 1f, -8f, 0f), T(0.06f), Ease.OutExpo);
            var fireball = Projectile.Spawn(fighter, anim.tip.position, new Vector2(10f * Dir, 0f), 1.6f,
                SpriteFactory.Circle, Fire, Vector2.one * 0.55f);
            fireball.homingTarget = fighter.NearestEnemy();
            fireball.homingTurnRate = 240f;
            fireball.lifetime = 3f;
            fireball.knockback = new Vector2(4f, 3f);
            fireball.onUpdate = p =>
            {
                p.Visual.transform.localScale = Vector3.one * (0.55f + Mathf.Sin(p.Age * 40f) * 0.06f);
                if (Random.value < 0.4f) Fx.Burst(p.transform.position, Ember, 0.25f, 0.2f);
            };
            fireball.onExpire = p => Fx.Burst(p.transform.position, Fire, 1.4f, 0.25f);
            yield return Wait(T(0.15f));
        }

        IEnumerator SummonMinions()
        {
            yield return anim.PoseTo(P(90f, 0f, 0.2f, 1f, 0f, 1f), T(0.25f), Ease.OutBack);
            for (int i = 0; i < 3; i++)
            {
                Vector2 pos = Local(0.8f + i * 0.7f, 0f);
                Fx.Burst(pos + Vector2.up * 0.4f, Def.color, 1f);
                Minion.Spawn(fighter, pos, Def.color);
                yield return Wait(0.08f);
            }
            yield return anim.PoseTo(P(75f, 0f, 0f, 1f, 0f, 0f), 0.1f);
        }

        IEnumerator CastThunderCloud()
        {
            yield return anim.PoseTo(P(100f, 0f, 0.25f, 1f, 6f, 1f), T(0.2f), Ease.OutBack);
            Fx.Bolt(anim.tip.position, (Vector2)anim.tip.position + Vector2.up * 2f, new Color(1f, 1f, 0.6f), 0.15f);
            ThunderCloud.Spawn(fighter, fighter.NearestEnemy(), Local(2f, ThunderCloud.Height));
            yield return anim.PoseTo(P(80f, 0f, 0f, 1f, 0f, 0f), 0.1f);
        }

        protected override IEnumerator UltimateRoutine()
        {
            yield return anim.PoseTo(P(90f, 0f, 0.3f, 1.1f, 0f, 1f), 0.3f, Ease.OutBack);
            Fx.Popup(fighter.Center + Vector2.up * 1.6f, "FIRE RING", Fire, 1.3f);
            Fx.Burst(fighter.Center, Fire, 4f, 0.4f);
            FireRing.Spawn(fighter, 10f, Fire);
            yield return Wait(0.25f);
        }
    }
}
