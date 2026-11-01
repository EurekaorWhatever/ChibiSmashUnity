using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WeaponBrawl
{
    public enum AccentMode
    {
        None,
        PullBack, // bow: nocked arrow is pulled back by Pose.accent
        Glow,     // staff: orb grows by Pose.accent
    }

    /// <summary>
    /// Procedural animation for the body + weapon arm. Weapon classes describe moves as a sequence of
    /// <see cref="Pose"/>s and tween between them (wind-up, strike, recovery).
    /// If an <see cref="Animator"/> is assigned, matching triggers (Attack1..5, Skill1..3, Ultimate) are
    /// also fired so real animation clips can be layered on or replace the procedural motion.
    /// </summary>
    public class WeaponAnimator : MonoBehaviour
    {
        [Serializable]
        public struct Pose
        {
            public float angle;    // weapon rotation in degrees: 0 = forward, 90 = up, -90 = down
            public Vector2 offset; // pivot offset from the shoulder (+x = forward)
            public float reach;    // weapon length multiplier
            public float lean;     // body tilt in degrees (negative = lean forward)
            public float accent;   // 0..1, bow draw / orb charge

            public static Pose Lerp(Pose a, Pose b, float t) => new Pose
            {
                angle = Mathf.LerpUnclamped(a.angle, b.angle, t),
                offset = Vector2.LerpUnclamped(a.offset, b.offset, t),
                reach = Mathf.LerpUnclamped(a.reach, b.reach, t),
                lean = Mathf.LerpUnclamped(a.lean, b.lean, t),
                accent = Mathf.LerpUnclamped(a.accent, b.accent, t),
            };
        }

        public static Pose P(float angle, float x = 0f, float y = 0f, float reach = 1f, float lean = 0f, float accent = 0f) =>
            new Pose { angle = angle, offset = new Vector2(x, y), reach = reach, lean = lean, accent = accent };

        [Tooltip("Optional. Receives triggers Attack1..5, Skill1..3 and Ultimate when they exist in the controller.")]
        public Animator animator;
        [Tooltip("Disable if your Animator clips drive the rig transforms instead.")]
        public bool useProcedural = true;

        [Header("Rig")]
        public Transform body;
        public Transform pivot;
        public Transform weapon;
        public Transform tip;
        public Transform accent;

        Pose current, rest;
        AccentMode accentMode;
        Vector3 pivotBase, weaponBaseScale, accentBasePos, accentBaseScale;
        SpriteRenderer[] weaponRenderers;
        Color[] weaponColors;
        TrailRenderer trail;
        Coroutine restRoutine, punchRoutine;
        HashSet<string> animatorTriggers;

        public Pose Current => current;

        public void Setup(Pose restPose, Color trailColor, AccentMode mode)
        {
            rest = current = restPose;
            accentMode = mode;
            pivotBase = pivot.localPosition;
            weaponBaseScale = weapon.localScale;
            if (accent != null)
            {
                accentBasePos = accent.localPosition;
                accentBaseScale = accent.localScale;
                if (mode == AccentMode.PullBack) accent.gameObject.SetActive(false);
            }
            weaponRenderers = weapon.GetComponentsInChildren<SpriteRenderer>(true);
            weaponColors = Array.ConvertAll(weaponRenderers, r => r.color);

            trail = tip.gameObject.AddComponent<TrailRenderer>();
            trail.time = 0.14f;
            trail.minVertexDistance = 0.03f;
            trail.startWidth = 0.28f;
            trail.endWidth = 0f;
            trail.material = SpriteFactory.LineMaterial;
            trail.sortingOrder = 4;
            trail.emitting = false;
            SetTrailColor(trailColor);
            Apply();
        }

        // ---------------------------------------------------------------- tweening

        public void SetPose(Pose pose)
        {
            current = pose;
            Apply();
        }

        /// <summary>Tween to a pose. Yields only null, so it can be nested or run inside Parallel().</summary>
        public IEnumerator PoseTo(Pose target, float duration, Func<float, float> ease = null)
        {
            StopRest();
            return Tween(target, duration, ease ?? Ease.OutCubic);
        }

        IEnumerator Tween(Pose target, float duration, Func<float, float> ease)
        {
            Pose from = current;
            if (duration > 0f)
            {
                for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
                {
                    SetPose(Pose.Lerp(from, target, ease(t)));
                    yield return null;
                }
            }
            SetPose(target);
        }

        public void ReturnToRest(float duration = 0.15f)
        {
            StopRest();
            restRoutine = StartCoroutine(Tween(rest, duration, Ease.OutQuad));
        }

        void StopRest()
        {
            if (restRoutine != null) StopCoroutine(restRoutine);
            restRoutine = null;
        }

        /// <summary>Wrap the angle back into -180..180 after spins.</summary>
        public void NormalizeAngle()
        {
            current.angle = Mathf.DeltaAngle(0f, current.angle);
            Apply();
        }

        void Apply()
        {
            if (!useProcedural) return;
            pivot.localPosition = pivotBase + (Vector3)current.offset;
            pivot.localRotation = Quaternion.Euler(0f, 0f, current.angle);
            weapon.localScale = new Vector3(weaponBaseScale.x * current.reach, weaponBaseScale.y, weaponBaseScale.z);
            if (body != null) body.localRotation = Quaternion.Euler(0f, 0f, current.lean);
            if (accent == null) return;
            if (accentMode == AccentMode.PullBack) accent.localPosition = accentBasePos - new Vector3(current.accent * 0.22f, 0f, 0f);
            else if (accentMode == AccentMode.Glow) accent.localScale = accentBaseScale * (1f + current.accent * 0.7f);
        }

        // ---------------------------------------------------------------- effects

        public void SetTrail(bool on)
        {
            if (trail == null) return;
            if (on && !trail.emitting) trail.Clear();
            trail.emitting = on;
        }

        public void SetTrailColor(Color color)
        {
            if (trail == null) return;
            trail.startColor = new Color(color.r, color.g, color.b, 0.85f);
            trail.endColor = new Color(color.r, color.g, color.b, 0f);
        }

        /// <summary>Tint every weapon sprite, or pass null to restore the original colors.</summary>
        public void Tint(Color? color)
        {
            for (int i = 0; i < weaponRenderers.Length; i++)
                weaponRenderers[i].color = color ?? weaponColors[i];
        }

        public void ShowAccent(bool visible)
        {
            if (accent != null) accent.gameObject.SetActive(visible);
        }

        public void SetWeaponVisible(bool visible) => weapon.gameObject.SetActive(visible);

        /// <summary>Quick squash-and-stretch of the body (impacts, landings).</summary>
        public void Punch(float amount = 0.12f, float duration = 0.15f)
        {
            if (body == null) return;
            if (punchRoutine != null) StopCoroutine(punchRoutine);
            punchRoutine = StartCoroutine(PunchRoutine(amount, duration));
        }

        IEnumerator PunchRoutine(float amount, float duration)
        {
            for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
            {
                float k = Mathf.Sin(t * Mathf.PI) * amount;
                body.localScale = new Vector3(1f + k, 1f - k, 1f);
                yield return null;
            }
            body.localScale = Vector3.one;
        }

        public void Trigger(string triggerName)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return;
            if (animatorTriggers == null)
            {
                animatorTriggers = new HashSet<string>();
                foreach (var p in animator.parameters)
                    if (p.type == AnimatorControllerParameterType.Trigger) animatorTriggers.Add(p.name);
            }
            if (animatorTriggers.Contains(triggerName)) animator.SetTrigger(triggerName);
        }
    }
}
