using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Random = UnityEngine.Random;

namespace WeaponBrawl
{
    public struct DamageInfo
    {
        public Fighter source;
        public float amount;
        public bool isCrit;
        public bool isProjectile;
        public bool isReflected;
        public Vector2 knockback;
        public float stun;
    }

    /// <summary>Timed stat change (buffs / debuffs). Multipliers stack multiplicatively.</summary>
    public class StatModifier
    {
        public string label;
        public float attackMul = 1f, attackSpeedMul = 1f, moveMul = 1f, defenseMul = 1f;
        public Color? tint;
        public float expiresAt;
    }

    /// <summary>
    /// A character in the arena: stats, passive effects, health, ultimate meter, damage formula,
    /// movement and status (stun, parry, held, projectile block).
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public class Fighter : MonoBehaviour
    {
        public static readonly List<Fighter> All = new List<Fighter>();

        public const float UltMax = 100f;
        public static readonly Vector2 HurtboxSize = new Vector2(0.8f, 1.95f);

        const float BaseMoveSpeed = 6f;
        const float JumpVelocity = 12.5f;
        const float UltPerSecond = 2f;
        const float UltPerDamageDealt = 0.5f;
        const float UltPerDamageTaken = 0.35f;
        const float RespawnDelay = 2f;
        const float HitStagger = 0.2f;

        public string DisplayName { get; private set; }
        public WeaponDefinition Definition { get; private set; }
        public PassiveInfo Passive { get; private set; }
        public WeaponBehaviour Weapon { get; private set; }
        public WeaponAnimator Anim { get; private set; }
        public Rigidbody2D Body { get; private set; }
        public SpriteRenderer BodyRenderer { get; set; }

        /// <summary>Which look of the weapon type this fighter uses. Never affects gameplay.</summary>
        public int Variant { get; set; }
        public string VariantName => Definition.variants[Variant];

        public float Health { get; private set; }
        public float MaxHealth => Definition.health;
        public float UltMeter { get; private set; }
        public bool UltReady => UltMeter >= UltMax;
        public int Facing { get; private set; } = 1;
        public bool IsDead { get; private set; }
        public bool IsParrying { get; set; }
        public bool IsHeld { get; set; }
        public bool IsStunned => Time.time < stunUntil;
        public bool BlocksProjectiles => Time.time < projectileBlockUntil;
        public bool PatientStrikeReady => Time.time - lastActionTime >= 3f;

        public Vector2 Center => (Vector2)transform.position + Vector2.up * (HurtboxSize.y * 0.5f);
        public Bounds Bounds => new Bounds(Center, new Vector3(HurtboxSize.x, HurtboxSize.y, 10f));

        public event Action<Fighter> Died;
        /// <summary>Raised when a parry succeeds. Args: attacker, the parried hit.</summary>
        public event Action<Fighter, DamageInfo> Parried;

        float passiveAttackMul = 1f, passiveAttackSpeedMul = 1f, passiveMoveMul = 1f;
        float passiveCritBonus, lifeSteal, blockChance;

        readonly List<StatModifier> modifiers = new List<StatModifier>();
        float stunUntil, projectileBlockUntil, movementLockUntil;
        float lastActionTime = -999f, speedBurstReadyAt;
        bool patientStrikeArmed;
        SpriteRenderer[] renderers;
        Color baseColor;
        Vector3 spawnPoint;

        public float Attack => Definition.attack * passiveAttackMul * Product(m => m.attackMul);
        public float Defense => Definition.defense * Product(m => m.defenseMul);
        public float AttackSpeed => passiveAttackSpeedMul * Product(m => m.attackSpeedMul);
        public float MoveSpeed => BaseMoveSpeed * passiveMoveMul * Product(m => m.moveMul);
        public float CritRate => Definition.critRate + passiveCritBonus;

        public bool CanMove => !IsDead && !IsStunned && !IsHeld && Time.time >= movementLockUntil &&
                               (Weapon == null || !Weapon.LockMovement);

        void Awake() => Body = GetComponent<Rigidbody2D>();
        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        public void Init(string displayName, WeaponDefinition def, int passiveIndex, WeaponBehaviour weapon, WeaponAnimator anim)
        {
            DisplayName = displayName;
            Definition = def;
            Passive = def.passives[Mathf.Clamp(passiveIndex, 0, def.passives.Length - 1)];
            Weapon = weapon;
            Anim = anim;
            ApplyPassive(Passive.id);
            Health = MaxHealth;
            spawnPoint = transform.position;
            renderers = GetComponentsInChildren<SpriteRenderer>(true);
            if (BodyRenderer != null) baseColor = BodyRenderer.color;
        }

        void ApplyPassive(PassiveId id)
        {
            switch (id)
            {
                case PassiveId.SwiftBlade: passiveAttackSpeedMul = 1.15f; break;
                case PassiveId.Bloodthirst: lifeSteal = 0.05f; break;
                case PassiveId.BruteForce: passiveAttackMul = 1.2f; passiveAttackSpeedMul = 0.85f; break;
                case PassiveId.Juggernaut: passiveAttackMul = 1.2f; passiveMoveMul = 0.85f; break;
                case PassiveId.EagleEye: passiveCritBonus = 0.2f; break;
                case PassiveId.ArcaneWard: blockChance = 0.2f; break;
                // FleetFoot, Hex, ArmorPiercer and PatientStrike trigger on events (NotifyActionStarted / DealDamage).
            }
        }

        void Update()
        {
            if (Definition == null || IsDead) return;
            for (int i = modifiers.Count - 1; i >= 0; i--)
            {
                if (Time.time < modifiers[i].expiresAt) continue;
                modifiers.RemoveAt(i);
                RefreshTint();
            }
            AddUlt(UltPerSecond * Time.deltaTime);
        }

        // ---------------------------------------------------------------- combat

        /// <summary>Called by the weapon whenever an attack, skill or ultimate starts.</summary>
        public void NotifyActionStarted()
        {
            patientStrikeArmed = Passive.id == PassiveId.PatientStrike && PatientStrikeReady;
            if (patientStrikeArmed) Fx.Popup(Center + Vector2.up * 1.2f, "CHARGED!", Definition.color);
            lastActionTime = Time.time;

            if (Passive.id == PassiveId.FleetFoot && Time.time >= speedBurstReadyAt)
            {
                speedBurstReadyAt = Time.time + 30f;
                AddModifier(new StatModifier { label = "Fleet Foot", moveMul = 1.1f }, 10f);
                Fx.Popup(Center + Vector2.up * 1.2f, "SPEED UP", Definition.color);
            }
        }

        public void NotifyActionEnded()
        {
            patientStrikeArmed = false;
            lastActionTime = Time.time;
        }

        /// <summary>Damage = Attack x multiplier (x1.5 on crit), then the target subtracts floor(defense / 10).</summary>
        public float DealDamage(Fighter target, float multiplier, bool isProjectile, Vector2 knockback, float stun = 0f)
        {
            if (target == null || target == this || target.IsDead || IsDead) return 0f;

            float amount = Attack * multiplier;
            if (patientStrikeArmed) amount *= 1.5f;
            bool crit = Random.value < CritRate;
            if (crit) amount *= 1f + Definition.critDamage;

            float dealt = target.ReceiveDamage(new DamageInfo
            {
                source = this, amount = amount, isCrit = crit, isProjectile = isProjectile,
                knockback = knockback, stun = stun,
            });
            if (dealt <= 0f) return 0f;

            AddUlt(dealt * UltPerDamageDealt);
            if (lifeSteal > 0f) Heal(dealt * lifeSteal);

            if (Passive.id == PassiveId.ArmorPiercer && Random.value < 0.10f)
            {
                target.AddModifier(new StatModifier { label = "Armor Break", defenseMul = 0.85f, tint = new Color(0.75f, 0.75f, 0.75f) }, 5f);
                Fx.Popup(target.Center + Vector2.up * 1.3f, "ARMOR BREAK", Definition.color);
            }
            if (Passive.id == PassiveId.Hex && Random.value < 0.10f)
            {
                target.AddModifier(new StatModifier
                {
                    label = "Hex", moveMul = 0.85f, attackMul = 0.9f, attackSpeedMul = 0.85f,
                    tint = new Color(0.7f, 0.5f, 1f),
                }, 4f);
                Fx.Popup(target.Center + Vector2.up * 1.3f, "HEXED", Definition.color);
            }
            return dealt;
        }

        public float ReceiveDamage(DamageInfo info)
        {
            if (IsDead) return 0f;
            Vector2 popup = Center + Vector2.up * 0.8f;

            if (info.isProjectile && BlocksProjectiles)
            {
                Fx.Popup(popup, "BLOCKED", new Color(0.6f, 0.9f, 1f));
                return 0f;
            }

            if (IsParrying && info.source != null && !info.isReflected)
            {
                Fx.Popup(popup, "PARRY!", Color.white, 1.3f);
                Fx.Ring(Center, Color.white, 2.5f, 0.3f);
                Fx.Shake(0.12f, 0.12f);
                var reflected = info;
                reflected.source = this;
                reflected.isReflected = true;
                reflected.isProjectile = false;
                reflected.knockback = new Vector2(Mathf.Sign(info.source.transform.position.x - transform.position.x) * 6f, 3f);
                reflected.stun = 0.5f;
                info.source.ReceiveDamage(reflected);
                Parried?.Invoke(info.source, info);
                return 0f;
            }

            if (blockChance > 0f && Random.value < blockChance)
            {
                Fx.Popup(popup, "WARD", Definition.color);
                Fx.Ring(Center, Definition.color, 2f, 0.25f);
                return 0f;
            }

            float reduction = Mathf.Floor(Defense / 10f);
            float damage = Mathf.Max(1f, info.amount - reduction);
            Health = Mathf.Max(0f, Health - damage);
            AddUlt(damage * UltPerDamageTaken);

            Fx.Popup(popup, Mathf.RoundToInt(damage) + (info.isCrit ? "!" : ""),
                info.isCrit ? new Color(1f, 0.85f, 0.2f) : Color.white, info.isCrit ? 1.35f : 1f);
            StartCoroutine(HitFlash());

            if (!IsHeld && info.knockback != Vector2.zero)
            {
                Body.SetVelocity(info.knockback);
                movementLockUntil = Time.time + HitStagger;
            }
            if (info.stun > 0f) Stun(info.stun);
            if (Health <= 0f) Die();
            return damage;
        }

        public void Stun(float duration)
        {
            if (IsDead) return;
            stunUntil = Mathf.Max(stunUntil, Time.time + duration);
            if (Weapon != null) Weapon.Interrupt();
            if (duration >= 0.4f) Fx.Popup(Center + Vector2.up * 1.4f, "STUN", new Color(1f, 0.9f, 0.4f), 0.8f);
        }

        public void Heal(float amount)
        {
            if (IsDead || amount <= 0f) return;
            Health = Mathf.Min(MaxHealth, Health + amount);
        }

        public void AddUlt(float amount) => UltMeter = Mathf.Clamp(UltMeter + amount, 0f, UltMax);
        public void ConsumeUlt() => UltMeter = 0f;
        public void SetProjectileBlock(float duration) => projectileBlockUntil = Mathf.Max(projectileBlockUntil, Time.time + duration);

        public void AddModifier(StatModifier modifier, float duration)
        {
            modifiers.RemoveAll(m => m.label == modifier.label);
            modifier.expiresAt = Time.time + duration;
            modifiers.Add(modifier);
            RefreshTint();
        }

        public string StatusText
        {
            get
            {
                var sb = new StringBuilder();
                if (IsStunned) sb.Append("STUNNED  ");
                if (BlocksProjectiles) sb.Append("PROJ-BLOCK  ");
                if (Passive != null && Passive.id == PassiveId.PatientStrike && PatientStrikeReady) sb.Append("CHARGED  ");
                foreach (var m in modifiers) sb.Append(m.label.ToUpperInvariant()).Append("  ");
                return sb.ToString();
            }
        }

        float Product(Func<StatModifier, float> selector)
        {
            float v = 1f;
            foreach (var m in modifiers) v *= selector(m);
            return v;
        }

        void RefreshTint()
        {
            if (BodyRenderer == null) return;
            Color c = baseColor;
            foreach (var m in modifiers)
                if (m.tint.HasValue) c = m.tint.Value;
            BodyRenderer.color = c;
        }

        IEnumerator HitFlash()
        {
            if (BodyRenderer == null) yield break;
            BodyRenderer.color = new Color(1f, 0.35f, 0.35f);
            yield return new WaitForSeconds(0.08f);
            RefreshTint();
        }

        void Die()
        {
            IsDead = true;
            if (Weapon != null) Weapon.Interrupt();
            IsParrying = false;
            IsHeld = false;
            Fx.Burst(Center, Definition.color, 3f, 0.5f);
            Fx.Popup(Center + Vector2.up, "K.O.", new Color(1f, 0.3f, 0.3f), 1.8f);
            Fx.Shake(0.3f, 0.25f);
            SetVisible(false);
            Body.simulated = false;
            Died?.Invoke(this);
            StartCoroutine(Respawn());
        }

        IEnumerator Respawn()
        {
            yield return new WaitForSeconds(RespawnDelay);
            transform.position = spawnPoint;
            Body.simulated = true;
            Body.SetVelocity(Vector2.zero);
            modifiers.Clear();
            RefreshTint();
            stunUntil = projectileBlockUntil = movementLockUntil = 0f;
            Health = MaxHealth;
            IsDead = false;
            SetVisible(true);
            Fx.Burst(Center, Color.white, 2f, 0.4f);
        }

        void SetVisible(bool visible)
        {
            foreach (var r in renderers)
                if (r != null) r.enabled = visible;
        }

        // ---------------------------------------------------------------- movement

        public void Move(float input)
        {
            if (!CanMove) return;
            Body.SetVelocity(new Vector2(input * MoveSpeed, Body.GetVelocity().y));
            if (Mathf.Abs(input) > 0.01f) SetFacing(input > 0f ? 1 : -1);
        }

        public void Jump()
        {
            if (CanMove && IsGrounded) Body.SetVelocity(new Vector2(Body.GetVelocity().x, JumpVelocity));
        }

        public bool IsGrounded
        {
            get
            {
                var hits = Physics2D.RaycastAll((Vector2)transform.position + Vector2.up * 0.05f, Vector2.down, 0.15f);
                foreach (var h in hits)
                    if (!h.collider.isTrigger && h.collider.GetComponentInParent<Fighter>() == null) return true;
                return false;
            }
        }

        public void SetFacing(int dir)
        {
            if (dir == 0) return;
            Facing = dir > 0 ? 1 : -1;
            var s = transform.localScale;
            s.x = Mathf.Abs(s.x) * Facing;
            transform.localScale = s;
        }

        public void FaceTowards(Fighter other)
        {
            if (other != null) SetFacing(other.transform.position.x >= transform.position.x ? 1 : -1);
        }

        public Fighter NearestEnemy(float maxRange = float.PositiveInfinity)
        {
            Fighter best = null;
            float bestDist = maxRange;
            foreach (var f in All)
            {
                if (f == this || f.IsDead) continue;
                float d = Vector2.Distance(f.transform.position, transform.position);
                if (d <= bestDist)
                {
                    best = f;
                    bestDist = d;
                }
            }
            return best;
        }
    }
}
