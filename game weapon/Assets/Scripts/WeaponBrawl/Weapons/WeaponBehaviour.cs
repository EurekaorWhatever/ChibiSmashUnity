using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WeaponBrawl
{
    /// <summary>
    /// Base class for the 5 weapon classes: looping combo, skill cooldowns, ultimate, interrupts and
    /// shared combat helpers (hitboxes, grabs, dashes). Subclasses only describe the moves.
    /// </summary>
    public abstract class WeaponBehaviour : MonoBehaviour
    {
        public const int AttackAction = -1;
        public const int UltimateAction = 99;
        const float ComboResetDelay = 0.8f;

        protected Fighter fighter;
        protected WeaponAnimator anim;

        /// <summary>Raised when an action starts: <see cref="AttackAction"/>, skill index, or <see cref="UltimateAction"/>.</summary>
        public event Action<int> ActionUsed;

        public bool Busy { get; private set; }
        public bool LockMovement { get; protected set; }
        public WeaponDefinition Def => fighter.Definition;
        public int NextComboStep => !Busy && Time.time - lastAttackEnd > ComboResetDelay ? 0 : comboStep;
        public virtual bool CanAct => !Busy && !fighter.IsDead && !fighter.IsStunned && !fighter.IsHeld;

        float[] cooldownEnds;
        int comboStep;
        float lastAttackEnd = -999f;
        bool currentIsAttack, attackQueued;
        Coroutine current;
        Fighter held;
        float heldGravity;

        public virtual void Init(Fighter owner, WeaponAnimator animator)
        {
            fighter = owner;
            anim = animator;
            cooldownEnds = new float[Def.skills.Length];
        }

        // ---------------------------------------------------------------- actions

        public bool TryAttack()
        {
            if (!CanAct)
            {
                if (Busy && currentIsAttack) attackQueued = true;
                return false;
            }
            if (Time.time - lastAttackEnd > ComboResetDelay) comboStep = 0;
            int step = comboStep;
            comboStep = (comboStep + 1) % Def.comboLength;
            Begin(AttackRoutine(step), AttackAction, "Attack" + (step + 1));
            return true;
        }

        public bool TrySkill(int index)
        {
            if (index < 0 || index >= Def.skills.Length || !CanAct || CooldownRemaining(index) > 0f) return false;
            cooldownEnds[index] = Time.time + Def.skills[index].cooldown;
            Begin(SkillRoutine(index), index, "Skill" + (index + 1));
            return true;
        }

        public bool TryUltimate()
        {
            if (!CanAct || !fighter.UltReady) return false;
            fighter.ConsumeUlt();
            Begin(UltimateRoutine(), UltimateAction, "Ultimate");
            return true;
        }

        public float CooldownRemaining(int index) =>
            index < cooldownEnds.Length ? Mathf.Max(0f, cooldownEnds[index] - Time.time) : 0f;

        public float CooldownFraction(int index)
        {
            float cd = index < Def.skills.Length ? Def.skills[index].cooldown : 0f;
            return cd <= 0f ? 0f : CooldownRemaining(index) / cd;
        }

        /// <summary>Cancels the running action (stun, grab, death).</summary>
        public void Interrupt()
        {
            if (!Busy) return;
            if (current != null) StopCoroutine(current);
            current = null;
            attackQueued = false;
            OnInterrupted();
            Finish();
        }

        protected abstract IEnumerator AttackRoutine(int step);
        protected abstract IEnumerator SkillRoutine(int index);
        protected abstract IEnumerator UltimateRoutine();

        /// <summary>Clean up anything the interrupted routine left behind (tints, props...).</summary>
        protected virtual void OnInterrupted() { }

        void Begin(IEnumerator routine, int actionId, string trigger)
        {
            Busy = true;
            LockMovement = true;
            currentIsAttack = actionId == AttackAction;
            Halt();
            fighter.NotifyActionStarted();
            anim.Trigger(trigger);
            ActionUsed?.Invoke(actionId);
            current = StartCoroutine(Run(routine));
        }

        IEnumerator Run(IEnumerator routine)
        {
            yield return routine;
            current = null;
            Finish();
            if (attackQueued)
            {
                attackQueued = false;
                TryAttack();
            }
        }

        void Finish()
        {
            Busy = false;
            LockMovement = false;
            if (currentIsAttack) lastAttackEnd = Time.time;
            fighter.IsParrying = false;
            ReleaseHeld();
            fighter.NotifyActionEnded();
            anim.SetTrail(false);
            anim.ReturnToRest();
        }

        // ---------------------------------------------------------------- helpers

        protected int Dir => fighter.Facing;

        /// <summary>Scales an animation duration by attack speed.</summary>
        protected float T(float seconds) => seconds / Mathf.Max(0.2f, fighter.AttackSpeed);

        protected static WaitForSeconds Wait(float seconds) => new WaitForSeconds(seconds);

        /// <summary>World position relative to the fighter's feet; +x is the facing direction.</summary>
        protected Vector2 Local(float x, float y) => (Vector2)fighter.transform.position + new Vector2(x * Dir, y);

        protected void Lunge(float speed) => fighter.Body.SetVelocity(new Vector2(speed * Dir, fighter.Body.GetVelocity().y));
        protected void Halt() => fighter.Body.SetVelocity(new Vector2(0f, fighter.Body.GetVelocity().y));

        /// <summary>Damages every enemy overlapping a box in front of the fighter. Knockback pushes away from the fighter.</summary>
        protected List<Fighter> HitBox(float x, float y, float width, float height, float damageMul, Vector2 knockback,
            float stun = 0f, HashSet<Fighter> exclude = null)
        {
            var box = new Bounds(Local(x, y), new Vector3(width, height, 10f));
            DebugDrawBox(box);
            var hits = new List<Fighter>();
            foreach (var target in Fighter.All.ToArray())
            {
                if (target == fighter || target.IsDead || !box.Intersects(target.Bounds)) continue;
                if (exclude != null && !exclude.Add(target)) continue;
                float side = Mathf.Sign(target.transform.position.x - fighter.transform.position.x);
                fighter.DealDamage(target, damageMul, false, new Vector2(knockback.x * side, knockback.y), stun);
                Fx.Burst(target.Center, Color.white, 0.8f, 0.15f);
                hits.Add(target);
            }
            return hits;
        }

        protected Fighter FirstEnemyIn(float x, float y, float width, float height)
        {
            var box = new Bounds(Local(x, y), new Vector3(width, height, 10f));
            DebugDrawBox(box);
            foreach (var target in Fighter.All)
                if (target != fighter && !target.IsDead && box.Intersects(target.Bounds)) return target;
            return null;
        }

        /// <summary>Pins an enemy (grabs, chokes, impales). Released automatically when the action ends.</summary>
        protected void Hold(Fighter target)
        {
            ReleaseHeld();
            if (target.Weapon != null) target.Weapon.Interrupt();
            held = target;
            heldGravity = target.Body.gravityScale;
            target.IsHeld = true;
            target.Body.gravityScale = 0f;
            target.Body.SetVelocity(Vector2.zero);
        }

        protected void ReleaseHeld()
        {
            if (held == null) return;
            held.IsHeld = false;
            held.Body.gravityScale = heldGravity;
            held = null;
        }

        protected IEnumerator MoveFighter(Fighter target, Vector2 to, float duration, Func<float, float> ease = null)
        {
            if (target == null) yield break;
            ease = ease ?? Ease.OutQuad;
            to.x = Mathf.Clamp(to.x, -Arena.HalfWidth + 0.5f, Arena.HalfWidth - 0.5f);
            Vector2 from = target.transform.position;
            for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
            {
                if (target == null) yield break;
                target.transform.position = Vector2.Lerp(from, to, ease(t));
                target.Body.SetVelocity(Vector2.zero);
                yield return null;
            }
            if (target != null) target.transform.position = to;
        }

        protected IEnumerator WaitForLanding(float maxTime)
        {
            for (float t = 0f; t < maxTime; t += Time.deltaTime)
            {
                if (t > 0.08f && fighter.Body.GetVelocity().y <= 0.01f && fighter.IsGrounded) yield break;
                yield return null;
            }
        }

        /// <summary>Steps several routines together. Routines must only yield null (PoseTo, MoveFighter do).</summary>
        protected static IEnumerator Parallel(params IEnumerator[] routines)
        {
            bool running = true;
            while (running)
            {
                running = false;
                foreach (var r in routines)
                    if (r.MoveNext()) running = true;
                if (running) yield return null;
            }
        }

        static void DebugDrawBox(Bounds b)
        {
            Vector3 a = new Vector3(b.min.x, b.min.y), c = new Vector3(b.max.x, b.max.y);
            Vector3 bx = new Vector3(c.x, a.y), d = new Vector3(a.x, c.y);
            Debug.DrawLine(a, bx, Color.red, 0.2f);
            Debug.DrawLine(bx, c, Color.red, 0.2f);
            Debug.DrawLine(c, d, Color.red, 0.2f);
            Debug.DrawLine(d, a, Color.red, 0.2f);
        }
    }
}
