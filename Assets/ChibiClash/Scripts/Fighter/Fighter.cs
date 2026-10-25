using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>HUD feedback for the CV ultimate trigger: yellow preparing, green detected, red failed.</summary>
    public enum PromptState { Hidden, Preparing, Detected, Failed }

    [RequireComponent(typeof(Rigidbody2D), typeof(BoxCollider2D))]
    public class Fighter : MonoBehaviour
    {
        public const float UltMax = 100f;
        public const float UltWindowDuration = 3f;

        public ClassDef Def { get; private set; }
        public Skin Skin { get; private set; }
        public int Passive { get; private set; }
        public int Index { get; private set; }
        public bool IsCpu { get; private set; }
        public FighterInput Controls { get; private set; }
        public GestureTracker Gestures { get; private set; }
        public MatchController Match { get; private set; }
        public FighterVisual Visual { get; private set; }
        public Fighter Opponent;

        public float Hp, MaxHp, Ult;
        public int Facing { get; private set; } = 1;
        public bool Grounded { get; private set; }
        public bool IsDead => Hp <= 0f;
        public Vector2 Velocity => rb.linearVelocity;
        public Vector2 Center => col.bounds.center;
        public bool IsAttacking => actionLock > 0f;
        public readonly float[] SkillCd = new float[3];

        public bool UltWindow { get; private set; }
        public float UltWindowLeft { get; private set; }
        public PromptState Prompt { get; private set; }
        public string PromptMessage { get; private set; } = "";
        float promptTimer, ultRetryCd;

        public int HitsLanded, CombosLanded, UltsUsed, Parries, MaxCombo;
        public int ComboCount { get; private set; }
        float comboTimer;

        Rigidbody2D rb;
        BoxCollider2D col;
        ContactFilter2D noFilter;
        readonly List<Collider2D> overlap = new List<Collider2D>();

        float hitstun, actionLock, invuln, dashTimer, dashCd;
        int dashDir, comboIndex, airJumps;
        float lastAttackTime = -10f;
        bool blocking;
        float blockStart = -10f;
        float parryUntil, spinGuardUntil, swordAuraUntil;
        public float FireRingUntil;
        float slowUntil, weakenUntil, sluggishUntil, defDownUntil;
        float tailwindUntil, tailwindReady;
        float superJumpCd, poseShotCd;
        // Archer / Mage balance: after a full set of 4 normal shots they must reload before shooting again.
        public const float SetCooldown = 1.5f;
        float setCooldownUntil;
        // Guard meter: blocking drains it by the raw damage blocked; empty = guard break.
        public float GuardHp { get; private set; }
        public float GuardMax { get; private set; }
        float guardBrokenUntil;
        public bool GuardBroken => Time.time < guardBrokenUntil;
        bool spearOut, held;
        Fighter heldTarget;
        Collider2D droppedPlatform;
        float dropTimer;

        // ---------------------------------------------------------------- setup

        public WeaponDef Weapon { get; private set; }

        public void Init(MatchController match, int index, ClassDef def, int passive, Skin skin, FighterInput input, GestureTracker gestures, bool cpu, WeaponDef weapon)
        {
            Match = match;
            Index = index;
            Weapon = weapon;
            Def = def;
            Passive = passive;
            Skin = skin;
            Controls = input;
            Gestures = gestures;
            IsCpu = cpu;
            MaxHp = Hp = def.health;
            GuardMax = GuardHp = CombatAuthority.GuardMax(def.defense);

            rb = GetComponent<Rigidbody2D>();
            rb.gravityScale = 3.6f;
            rb.freezeRotation = true;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            col = GetComponent<BoxCollider2D>();
            col.size = new Vector2(0.75f, 1.35f);
            col.offset = new Vector2(0f, -0.05f);
            col.sharedMaterial = new PhysicsMaterial2D("NoFriction") { friction = 0f, bounciness = 0f };
            noFilter = ContactFilter2D.noFilter;

            Visual = gameObject.AddComponent<FighterVisual>();
            Visual.Build(def, skin, 10 + index * 20, weapon);
            SetFacing(index == 0 ? 1 : -1);
        }

        public Collider2D Collider => col;

        // ---------------------------------------------------------------- derived stats (passives & debuffs)

        public float AttackPower
        {
            get
            {
                float a = Def.attack;
                if (Def.type == FighterClass.Heavy) a *= 1.2f;
                if (Time.time < weakenUntil) a *= 0.9f;
                return a;
            }
        }

        public float AttackSpeed
        {
            get
            {
                float s = 1f;
                if (Def.type == FighterClass.Melee && Passive == 0) s *= 1.15f;
                if (Def.type == FighterClass.Heavy && Passive == 0) s *= 0.85f;
                if (Time.time < sluggishUntil) s *= 0.85f;
                return s;
            }
        }

        public float MoveSpeed
        {
            get
            {
                float s = Def.moveSpeed;
                if (Def.type == FighterClass.Heavy && Passive == 1) s *= 0.85f;
                if (Time.time < slowUntil) s *= 0.85f;
                if (Time.time < tailwindUntil) s *= 1.1f;
                return s;
            }
        }

        public float CritRate => Def.critRate + (Def.type == FighterClass.Ranged && Passive == 0 ? 0.2f : 0f);
        public float Defense => Def.defense * (Time.time < defDownUntil ? 0.85f : 1f);
        public bool SkillReady(int i) => i < Def.SkillCount && SkillCd[i] <= 0f;

        public List<string> Statuses()
        {
            var s = new List<string>();
            float t = Time.time;
            if (t < swordAuraUntil) s.Add("AURA " + Mathf.CeilToInt(swordAuraUntil - t));
            if (t < FireRingUntil) s.Add("FIRE RING " + Mathf.CeilToInt(FireRingUntil - t));
            if (t < tailwindUntil) s.Add("TAILWIND");
            if (GuardBroken) s.Add("GUARD BROKEN " + (guardBrokenUntil - t).ToString("0.0") + "s");
            else if (GuardHp < GuardMax - 0.5f) s.Add("GUARD " + Mathf.RoundToInt(GuardHp / GuardMax * 100f) + "%");
            if (Def.rangedNormals && t < setCooldownUntil)
                s.Add((Def.type == FighterClass.Ranged ? "RELOADING " : "RECHARGING ") + (setCooldownUntil - t).ToString("0.0") + "s");
            if (t < slowUntil) s.Add("HEXED");
            if (t < defDownUntil) s.Add("ARMOR -15%");
            return s;
        }

        // ---------------------------------------------------------------- frame update

        void Update()
        {
            float dt = Time.deltaTime;
            Gestures.Tick(Time.unscaledDeltaTime);
            Controls.Tick(this, dt);
            TickTimers(dt);
            GroundCheck();
            UpdateUltWindow();

            var v = rb.linearVelocity;
            bool canAct = Match.InputEnabled && !IsDead && hitstun <= 0f && !held;
            if (!canAct) blocking = false;
            if (canAct)
            {
                HandlePoses(ref v);
                HandleActions(ref v, dt);
            }
            else if (Grounded && hitstun <= 0f && !held)
            {
                v.x = Mathf.MoveTowards(v.x, 0f, 30f * dt);
            }
            if (!held) rb.linearVelocity = v;

            Visual.Tick(v, Grounded, blocking);
            bool parryLit = Time.time < parryUntil || (blocking && Time.time - blockStart <= CombatAuthority.PerfectParryWindow);
            Visual.SetShield(blocking || Time.time < parryUntil || Time.time < spinGuardUntil, parryLit, blocking ? GuardHp / GuardMax : 1f);
            Visual.SetAura(Time.time < swordAuraUntil, Skin.ultFx);
            Visual.SetStunned(hitstun > 0.3f);
        }

        void TickTimers(float dt)
        {
            hitstun -= dt;
            if (!blocking && !GuardBroken && GuardHp < GuardMax)
                GuardHp = Mathf.Min(GuardMax, GuardHp + CombatAuthority.GuardRegenPerSecond * dt);
            actionLock -= dt;
            invuln -= dt;
            dashTimer -= dt;
            dashCd -= dt;
            superJumpCd -= dt;
            poseShotCd -= dt;
            ultRetryCd -= Time.unscaledDeltaTime;
            for (int i = 0; i < SkillCd.Length; i++) SkillCd[i] = Mathf.Max(0f, SkillCd[i] - dt);

            comboTimer -= dt;
            if (comboTimer <= 0f) ComboCount = 0;

            if (dropTimer > 0f)
            {
                dropTimer -= dt;
                if (dropTimer <= 0f && droppedPlatform != null)
                {
                    Physics2D.IgnoreCollision(col, droppedPlatform, false);
                    droppedPlatform = null;
                }
            }

            if (Prompt == PromptState.Detected || Prompt == PromptState.Failed)
            {
                promptTimer -= Time.unscaledDeltaTime;
                if (promptTimer <= 0f) Prompt = PromptState.Hidden;
            }

            if (Def.type == FighterClass.Ranged && Passive == 1 && Match.Running && Time.time >= tailwindReady)
            {
                tailwindUntil = Time.time + 10f;
                tailwindReady = Time.time + 30f;
                Fx.Text(Center + Vector2.up, "TAILWIND", Def.color, 0.8f);
            }
        }

        void GroundCheck()
        {
            var b = col.bounds;
            overlap.Clear();
            Physics2D.OverlapBox(new Vector2(b.center.x, b.min.y - 0.05f), new Vector2(b.size.x * 0.9f, 0.1f), 0f, noFilter, overlap);
            bool g = false;
            foreach (var c in overlap)
            {
                if (c == col || c.isTrigger || c == droppedPlatform) continue;
                if (c.attachedRigidbody != null && c.attachedRigidbody.bodyType != RigidbodyType2D.Static) continue;
                g = true;
                break;
            }
            if (g && rb.linearVelocity.y > 0.5f) g = false;
            Grounded = g;
            if (g) airJumps = 1;
        }

        void HandleActions(ref Vector2 v, float dt)
        {
            float mx = Controls.MoveX;
            bool wantBlock = (Controls.Block || (Gestures.Stable == Gesture.CrossArms && !UltWindow && Gestures.TelemetryValid))
                             && actionLock <= 0f && dashTimer <= 0f && !GuardBroken;
            if (wantBlock && !blocking) blockStart = Time.time;
            blocking = wantBlock;

            if (Controls.Ult) TryOpenUltWindow();

            if (dashTimer > 0f)
            {
                v = new Vector2(dashDir * 17f, 0f);
                return;
            }

            if (blocking)
            {
                if (Grounded) v.x = Mathf.MoveTowards(v.x, 0f, 60f * dt);
                return;
            }

            if (actionLock <= 0f)
            {
                float target = mx * MoveSpeed;
                v.x = Mathf.MoveTowards(v.x, target, (Grounded ? 70f : 40f) * dt);
                if (Mathf.Abs(mx) > 0.1f) SetFacing(mx > 0f ? 1 : -1);
            }
            else if (Grounded)
            {
                v.x = Mathf.MoveTowards(v.x, 0f, 25f * dt);
            }

            if (Controls.Jump)
            {
                if (Controls.Down && Grounded && TryDropThrough()) { }
                else if (Grounded)
                {
                    v.y = 13f;
                    Fx.Burst(new Vector2(Center.x, col.bounds.min.y), new Color(1f, 1f, 1f, 0.7f), 6, 2f, 0.25f);
                }
                else if (airJumps > 0)
                {
                    airJumps--;
                    v.y = 11.5f;
                    Fx.Burst(new Vector2(Center.x, col.bounds.min.y), new Color(1f, 1f, 1f, 0.7f), 8, 3f, 0.25f);
                }
            }

            if (!Grounded && Controls.Down && v.y < 3f) v.y = Mathf.Max(v.y - 45f * dt, -24f);

            if (Controls.Dash && dashCd <= 0f)
            {
                dashDir = Mathf.Abs(mx) > 0.1f ? (mx > 0f ? 1 : -1) : Facing;
                dashTimer = 0.13f;
                dashCd = 0.55f;
                invuln = Mathf.Max(invuln, 0.1f);
                Fx.Burst(Center, Skin.ultFx, 8, 3f, 0.3f);
            }

            if (actionLock <= 0f)
            {
                if (Controls.Light && !NormalsLocked) DoNormal(false);
                else if (Controls.Heavy && !NormalsLocked) DoNormal(true);
                else
                {
                    for (int i = 0; i < Def.SkillCount; i++)
                    {
                        if (Controls.Skill[i] && SkillCd[i] <= 0f)
                        {
                            UseSkill(i);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>Archer and mage can't use normal attacks while reloading between sets of 4 shots.</summary>
        public bool NormalsLocked => Def.rangedNormals && Time.time < setCooldownUntil;

        /// <summary>Start a normal attack if allowed right now (used by input handling and by automated tests).</summary>
        public bool TryNormal(bool heavy)
        {
            if (IsDead || actionLock > 0f || hitstun > 0f || NormalsLocked) return false;
            DoNormal(heavy);
            return true;
        }

        bool TryDropThrough()
        {
            var b = col.bounds;
            overlap.Clear();
            Physics2D.OverlapBox(new Vector2(b.center.x, b.min.y - 0.05f), new Vector2(b.size.x * 0.9f, 0.1f), 0f, noFilter, overlap);
            foreach (var c in overlap)
            {
                if (c.GetComponent<PlatformEffector2D>() == null) continue;
                Physics2D.IgnoreCollision(col, c, true);
                droppedPlatform = c;
                dropTimer = 0.35f;
                return true;
            }
            return false;
        }

        public void SetFacing(int f)
        {
            Facing = f;
            Visual.SetFacing(f);
        }

        // ---------------------------------------------------------------- camera poses (special moves)

        void HandlePoses(ref Vector2 v)
        {
            if (UltWindow || !Gestures.TelemetryValid) return;
            var g = Gestures.JustStarted;
            if (g == Gesture.RaiseHands && superJumpCd <= 0f)
            {
                superJumpCd = 3f;
                v.y = 19f;
                airJumps = 1;
                Fx.Burst(new Vector2(Center.x, col.bounds.min.y), new Color(0.6f, 0.9f, 1f), 18, 5f, 0.35f);
                Fx.Text(Center + Vector2.up * 1.2f, "POSE: SUPER JUMP", new Color(0.6f, 0.9f, 1f), 0.7f);
            }
            else if (g == Gesture.PunchForward && poseShotCd <= 0f && actionLock <= 0f)
            {
                poseShotCd = 4f;
                actionLock = 0.25f;
                Visual.PlayRecoil(0.25f);
                Shoot(new Vector2(0.6f, 0.1f), new Vector2(Facing * 13f, 0f), HitSpec.Make(0.8f, new Vector2(4f, 3f), 0.3f, true),
                    new Color(1f, 0.55f, 0.15f), new Vector2(0.45f, 0.45f), 0.3f, 1.6f, Art.Circle);
                Fx.Text(Center + Vector2.up * 1.2f, "POSE: FIREBALL", new Color(1f, 0.6f, 0.2f), 0.7f);
            }
        }

        // ---------------------------------------------------------------- ultimate gesture flow

        void TryOpenUltWindow()
        {
            if (Ult < UltMax || UltWindow || ultRetryCd > 0f || IsDead) return;
            UltWindow = true;
            UltWindowLeft = UltWindowDuration;
            Prompt = PromptState.Preparing;
            PromptMessage = "Perform: " + ClassDef.GestureName(Def.ultGesture);
        }

        void UpdateUltWindow()
        {
            if (!UltWindow) return;
            UltWindowLeft -= Time.unscaledDeltaTime;
            bool match = Gestures.Stable == Def.ultGesture && Gestures.StableTime >= 0.15f;
            if (match && !Gestures.TelemetryValid)
            {
                PromptMessage = "Rejected: " + Gestures.Validator.Reason;
            }
            else if (match && Match.InputEnabled && !IsDead)
            {
                UltWindow = false;
                Prompt = PromptState.Detected;
                PromptMessage = Def.ultName + "!";
                promptTimer = 2f;
                FireUltimate();
                return;
            }
            if (UltWindowLeft <= 0f)
            {
                UltWindow = false;
                Prompt = PromptState.Failed;
                PromptMessage = "Time's up!";
                promptTimer = 1.3f;
                ultRetryCd = 1.5f;
            }
        }

        public void GainUlt(float amount)
        {
            Ult = Mathf.Min(UltMax, Ult + amount);
        }

        // ---------------------------------------------------------------- normal attacks

        float TakePatienceBonus()
        {
            if (Def.type == FighterClass.Polearm && Passive == 1 && Time.time - lastAttackTime >= 3f)
            {
                Fx.Text(Center + Vector2.up * 1.1f, "FOCUS +50%", Def.color, 0.7f);
                return 1.5f;
            }
            return 1f;
        }

        void DoNormal(bool heavy)
        {
            if (spearOut) return;
            float dur = (heavy ? 0.42f : 0.24f) / AttackSpeed;
            if (Time.time - lastAttackTime > 0.9f) comboIndex = 0;
            int idx = comboIndex;
            bool finisher = !heavy && idx == Def.patternLength - 1;
            comboIndex = heavy ? 0 : (comboIndex + 1) % Def.patternLength;
            float mult = (heavy ? 1.8f : finisher ? 1.5f : 1f) * TakePatienceBonus();
            actionLock = dur;
            lastAttackTime = Time.time;
            Visual.PlayNormal(heavy, idx, dur);
            StartCoroutine(NormalStrike(dur * 0.4f, mult, heavy, finisher));
        }

        IEnumerator NormalStrike(float delay, float mult, bool heavy, bool finisher)
        {
            yield return new WaitForSeconds(delay);
            var kb = heavy ? new Vector2(8f, 6f) : finisher ? new Vector2(7f, 7f) : new Vector2(2.5f, 2f);
            float stun = heavy ? 0.5f : finisher ? 0.45f : 0.28f;
            // the 4th shot of a set starts the reload
            if (finisher && Def.rangedNormals) setCooldownUntil = Time.time + SetCooldown;
            if (Def.type == FighterClass.Ranged)
            {
                SpawnArrow(mult, kb * 0.6f, stun, 0.05f, heavy);
            }
            else if (Def.type == FighterClass.Magic)
            {
                var c = Color.Lerp(Skin.ultFx, Color.white, 0.3f);
                float size = heavy ? 0.55f : 0.35f;
                Shoot(new Vector2(0.6f, 0.25f), new Vector2(Facing * 14f, 0f), HitSpec.Make(mult, kb * 0.6f, stun, true), c,
                    new Vector2(size, size), size * 0.8f, 1.2f, Art.Circle);
            }
            else
            {
                MeleeBox(Def.reach * (heavy ? 1.15f : 1f), 1.3f, HitSpec.Make(mult, kb, stun));
            }
            if (Time.time < swordAuraUntil)
            {
                Shoot(new Vector2(0.7f, 0f), new Vector2(Facing * 15f, 0f), HitSpec.Make(0.8f, new Vector2(4f, 2f), 0.25f, true),
                    new Color(Skin.ultFx.r, Skin.ultFx.g, Skin.ultFx.b, 0.85f), new Vector2(0.25f, 1.2f), 0.5f, 0.9f, Art.Rounded);
            }
        }

        HitResult MeleeBox(float reach, float height, HitSpec spec, float yOff = 0f)
        {
            var c = Center + new Vector2(Facing * (reach * 0.5f + 0.15f), yOff);
            var size = new Vector2(reach + 0.3f, height);
            Fx.Burst(c, new Color(1f, 1f, 1f, 0.5f), 2, 1.5f, 0.2f);
            if (Opponent == null || !Opponent.OverlapsBox(c, size)) return HitResult.Miss;
            spec.knockback.x *= Facing;
            return Opponent.ReceiveHit(this, spec);
        }

        public bool OverlapsBox(Vector2 c, Vector2 size)
        {
            var b = col.bounds;
            return Mathf.Abs(b.center.x - c.x) <= b.extents.x + size.x * 0.5f &&
                   Mathf.Abs(b.center.y - c.y) <= b.extents.y + size.y * 0.5f;
        }

        public bool OverlapsCircle(Vector2 p, float r)
        {
            var b = col.bounds;
            var closest = new Vector2(Mathf.Clamp(p.x, b.min.x, b.max.x), Mathf.Clamp(p.y, b.min.y, b.max.y));
            return (closest - p).sqrMagnitude <= r * r;
        }

        /// <summary>Spin Guard and Fire Ring destroy incoming projectiles.</summary>
        public bool DestroysProjectile(Vector2 p, Projectile proj)
        {
            if (!proj.Destructible) return false;
            if (Time.time < spinGuardUntil && Vector2.Distance(p, Center) < 1.6f) return true;
            if (Time.time < FireRingUntil && Vector2.Distance(p, Center) < 2.1f) return true;
            return false;
        }

        public Projectile Shoot(Vector2 offset, Vector2 vel, HitSpec spec, Color c, Vector2 size, float radius, float life, Sprite sprite)
        {
            var pos = Center + new Vector2(offset.x * Facing, offset.y);
            return Projectile.Spawn(this, pos, vel, spec, c, size, radius, life, sprite);
        }

        void SpawnArrow(float mult, Vector2 kb, float stun, float yOff, bool big)
        {
            var p = Shoot(new Vector2(0.5f, yOff), new Vector2(Facing * (big ? 21f : 18f), 0f), HitSpec.Make(mult, kb, stun, true),
                new Color(0.95f, 0.9f, 0.75f), big ? new Vector2(0.9f, 0.14f) : new Vector2(0.6f, 0.08f), 0.25f, 1.3f, Art.Rounded);
            p.TrailColor = Skin.ultFx;
        }

        // ---------------------------------------------------------------- receiving damage

        public HitResult ReceiveHit(Fighter attacker, HitSpec s)
        {
            if (IsDead || invuln > 0f || !Match.Running) return HitResult.Miss;
            s.knockback *= CombatAuthority.KnockbackScale(attacker.Def.type);

            if (Time.time < parryUntil && !s.unblockable)
            {
                float reflected = CombatAuthority.RollDamage(attacker, s.mult, out bool rc);
                var away = new Vector2(Mathf.Sign(attacker.Center.x - Center.x) * 5f, 4f);
                attacker.ApplyDamage(CombatAuthority.ApplyDefense(reflected, attacker.Defense), rc, away, 0.5f);
                parryUntil = 0f;
                Parries++;
                GainUlt(15f);
                Fx.Text(Center + Vector2.up * 1.3f, "REFLECT!", new Color(0.6f, 0.95f, 1f), 1.2f);
                Match.HitStop(0.12f);
                return HitResult.Parried;
            }

            if (blocking && !s.unblockable && Time.time - blockStart <= CombatAuthority.PerfectParryWindow)
            {
                Parries++;
                GainUlt(15f);
                attacker.Stun(0.45f);
                Fx.Burst(Center, Color.white, 16, 5f, 0.3f);
                Fx.Text(Center + Vector2.up * 1.3f, "PARRY!", Color.white, 1.2f);
                Match.HitStop(0.1f);
                return HitResult.Parried;
            }

            if (Def.type == FighterClass.Magic && Passive == 1 && Random.value < 0.2f)
            {
                Fx.Burst(Center, Def.color, 10, 3f, 0.3f);
                Fx.Text(Center + Vector2.up * 1.2f, "WARD", Def.color, 0.8f);
                return HitResult.Negated;
            }

            bool blocked = blocking && !s.unblockable;
            float raw = CombatAuthority.RollDamage(attacker, s.mult, out bool crit);
            float stun = s.stun;
            if (blocked)
            {
                GuardHp -= raw;                                   // the guard soaks the full hit...
                if (GuardHp <= 0f)
                {
                    GuardBreak();                                 // ...and shatters if it can't: full damage + long stun
                    blocked = false;
                    stun = Mathf.Max(stun, CombatAuthority.GuardBreakStun);
                }
                else raw *= CombatAuthority.BlockedFraction(Defense);   // tougher (higher DEF) fighters block more
            }
            float dmg = CombatAuthority.ApplyDefense(raw, Defense);
            ApplyDamage(dmg, crit, blocked ? s.knockback * 0.25f : s.knockback, blocked ? 0f : stun);
            attacker.OnHitLanded(this, dmg, blocked);
            return blocked ? HitResult.Blocked : HitResult.Hit;
        }

        void GuardBreak()
        {
            GuardHp = 0f;
            blocking = false;
            guardBrokenUntil = Time.time + CombatAuthority.GuardBrokenTime;
            Fx.Burst(Center, new Color(0.6f, 0.85f, 1f), 30, 7f, 0.35f);
            Fx.Text(Center + Vector2.up * 1.5f, "GUARD BREAK!", new Color(1f, 0.35f, 0.35f), 1.5f);
            Match.Shake(0.35f);
        }

        void ApplyDamage(float dmg, bool crit, Vector2 kb, float stun)
        {
            Hp = Mathf.Max(0f, Hp - dmg);
            if (stun > 0f)
            {
                hitstun = Mathf.Max(hitstun, stun);
                Interrupt();
            }
            if (!held && kb.sqrMagnitude > 0.01f) rb.linearVelocity = kb;
            Visual.Flash();
            GainUlt(1.5f);
            var c = crit ? new Color(1f, 0.85f, 0.2f) : Color.white;
            Fx.Burst(Center, c, crit ? 18 : 10, crit ? 6f : 4f, 0.3f);
            Fx.Text(Center + Vector2.up * 1.0f, Mathf.RoundToInt(dmg) + (crit ? " CRIT!" : ""), c, crit ? 1.3f : 0.9f);
            Match.HitStop(crit ? 0.08f : 0.045f);
            Match.Shake(crit ? 0.25f : 0.12f);
            if (Hp <= 0f) Match.OnKO(this);
        }

        void OnHitLanded(Fighter target, float dmg, bool blocked)
        {
            if (blocked) { GainUlt(1f); return; }
            HitsLanded++;
            ComboCount++;
            comboTimer = 1.1f;
            MaxCombo = Mathf.Max(MaxCombo, ComboCount);
            if (ComboCount % Def.patternLength == 0)
            {
                CombosLanded++;
                Fx.Text(target.Center + Vector2.up * 1.7f, ComboCount + " HIT COMBO!", new Color(1f, 0.6f, 0.9f), 1f);
            }
            float gain = 3f + (ComboCount >= 3 ? 2f : 0f);
            if (Hp < MaxHp * 0.3f) gain *= 2f; // calculated risk: fighting on low health
            GainUlt(gain);

            if (Def.type == FighterClass.Melee && Passive == 1) Hp = Mathf.Min(MaxHp, Hp + dmg * 0.05f);
            if (Def.type == FighterClass.Magic && Passive == 0 && Random.value < 0.1f) target.ApplyHex();
            if (Def.type == FighterClass.Polearm && Passive == 0 && Random.value < 0.1f) target.ApplyArmorBreak();
        }

        public void ApplyHex()
        {
            slowUntil = weakenUntil = sluggishUntil = Time.time + 4f;
            Fx.Text(Center + Vector2.up * 1.5f, "HEXED", new Color(0.75f, 0.45f, 1f), 0.8f);
        }

        public void ApplyArmorBreak()
        {
            defDownUntil = Time.time + 5f;
            Fx.Text(Center + Vector2.up * 1.5f, "ARMOR BREAK", new Color(1f, 0.8f, 0.3f), 0.8f);
        }

        public void Stun(float t)
        {
            hitstun = Mathf.Max(hitstun, t);
            Interrupt();
            if (!held) rb.linearVelocity = new Vector2(0f, Mathf.Min(0f, rb.linearVelocity.y));
        }

        void Interrupt()
        {
            StopAllCoroutines();
            actionLock = 0f;
            dashTimer = 0f;
            blocking = false;
            ReleaseHeld();
        }

        void ReleaseHeld()
        {
            if (heldTarget != null) heldTarget.SetHeld(false);
            heldTarget = null;
        }

        public void SetHeld(bool on)
        {
            held = on;
            rb.bodyType = on ? RigidbodyType2D.Kinematic : RigidbodyType2D.Dynamic;
            rb.linearVelocity = Vector2.zero;
        }

        public void RingOut(Vector2 respawn)
        {
            ReleaseHeld();
            if (held) SetHeld(false);
            float penalty = MaxHp * 0.15f;
            Hp = Mathf.Max(0f, Hp - penalty);
            transform.position = respawn;
            rb.linearVelocity = Vector2.zero;
            hitstun = 0f;
            invuln = 1.5f;
            Fx.Burst(respawn, Skin.ultFx, 25, 5f, 0.4f);
            Fx.Text(respawn + Vector2.up, "RING OUT -15%", new Color(1f, 0.4f, 0.4f), 1.1f);
            if (Hp <= 0f) Match.OnKO(this);
        }

        public void Revive()
        {
            Hp = MaxHp;
            hitstun = 0f;
        }

        // ---------------------------------------------------------------- skills

        void UseSkill(int i)
        {
            SkillCd[i] = Def.skillCooldowns[i];
            float b = TakePatienceBonus();
            lastAttackTime = Time.time;
            Fx.Text(Center + Vector2.up * 1.25f, Def.skillNames[i], Color.Lerp(Def.color, Color.white, 0.4f), 0.75f);
            switch (Def.type)
            {
                case FighterClass.Melee:
                    if (i == 0) StartCoroutine(DashThrust(b));
                    else if (i == 1) StartParry();
                    else StartCoroutine(ThrustBarrage(b));
                    break;
                case FighterClass.Heavy:
                    if (i == 0) StartCoroutine(Grab(b));
                    else StartCoroutine(TornadoSpin(b));
                    break;
                case FighterClass.Ranged:
                    if (i == 0) StartCoroutine(BackstepShot(b));
                    else if (i == 1) Choke(b);
                    else StartCoroutine(ArrowBarrage(b));
                    break;
                case FighterClass.Magic:
                    if (i == 0) HomingFireball(b);
                    else if (i == 1) SummonMinions();
                    else SummonCloud();
                    break;
                case FighterClass.Polearm:
                    if (i == 0) StartCoroutine(LeapSlam(b));
                    else if (i == 1) StartCoroutine(SpinGuard(b));
                    else ThrowSpear(b);
                    break;
            }
        }

        IEnumerator DashThrust(float b)
        {
            actionLock = 0.4f;
            Visual.PlayThrust(0.3f);
            bool done = false;
            for (float t = 0f; t < 0.22f; t += Time.deltaTime)
            {
                rb.linearVelocity = new Vector2(Facing * 18f, 0f);
                if (!done && MeleeBox(0.8f, 1.2f, HitSpec.Make(1.6f * b, new Vector2(7f, 4f), 0.45f)) != HitResult.Miss) done = true;
                Fx.Burst(Center, Skin.ultFx, 1, 1f, 0.3f);
                yield return null;
            }
            rb.linearVelocity = new Vector2(Facing * 3f, 0f);
        }

        void StartParry()
        {
            parryUntil = Time.time + 0.35f;
            actionLock = 0.4f;
            Visual.PlaySwing(0.3f);
        }

        IEnumerator ThrustBarrage(float b)
        {
            actionLock = 0.8f;
            for (int k = 0; k < 6; k++)
            {
                actionLock = Mathf.Max(actionLock, 0.15f);
                Visual.PlayThrust(0.1f);
                bool last = k == 5;
                MeleeBox(1.5f, 1.1f, HitSpec.Make(0.55f * b, last ? new Vector2(6f, 4f) : new Vector2(0.8f, 0.5f), 0.25f));
                yield return new WaitForSeconds(0.11f);
            }
        }

        IEnumerator Grab(float b)
        {
            actionLock = 0.35f;
            Visual.PlayThrust(0.2f);
            var o = Opponent;
            if (o == null || o.IsDead || o.invuln > 0f || !o.OverlapsBox(Center + new Vector2(Facing * 0.8f, 0f), new Vector2(1.3f, 1.4f)))
                yield break;

            actionLock = 1.1f;
            o.Stun(1.2f);
            o.SetHeld(true);
            heldTarget = o;
            Vector2 start = o.transform.position;
            Vector2 top = (Vector2)transform.position + new Vector2(Facing * 0.2f, 1.5f);
            for (float t = 0f; t < 0.3f; t += Time.deltaTime)
            {
                o.transform.position = Vector2.Lerp(start, top, t / 0.3f);
                yield return null;
            }
            Visual.PlaySlam(0.2f);
            Vector2 slam = (Vector2)transform.position + new Vector2(Facing * 1.0f, 0f);
            for (float t = 0f; t < 0.12f; t += Time.deltaTime)
            {
                o.transform.position = Vector2.Lerp(top, slam, t / 0.12f);
                yield return null;
            }
            ReleaseHeld();
            o.ReceiveHit(this, HitSpec.Make(2.2f * b, new Vector2(Facing * 4f, 8f), 0.7f, false, true));
            Fx.Burst(slam, new Color(0.8f, 0.7f, 0.6f), 20, 5f, 0.4f);
            Match.Shake(0.35f);
        }

        IEnumerator TornadoSpin(float b)
        {
            actionLock = 1.05f;
            Visual.PlaySpin(1.0f);
            float tick = 0f;
            for (float t = 0f; t < 1.0f; t += Time.deltaTime)
            {
                actionLock = Mathf.Max(actionLock, 0.1f);
                rb.linearVelocity = new Vector2(Controls.MoveX * 3.5f, rb.linearVelocity.y);
                tick -= Time.deltaTime;
                if (tick <= 0f)
                {
                    tick = 0.2f;
                    Fx.Burst(Center, new Color(0.9f, 0.9f, 1f, 0.6f), 6, 4f, 0.3f);
                    if (Opponent != null && Opponent.OverlapsCircle(Center, 1.6f))
                    {
                        float dir = Mathf.Sign(Opponent.Center.x - Center.x);
                        Opponent.ReceiveHit(this, HitSpec.Make(0.7f * b, new Vector2(dir * 3f, 3f), 0.25f));
                    }
                }
                yield return null;
            }
        }

        IEnumerator BackstepShot(float b)
        {
            actionLock = 0.4f;
            for (float t = 0f; t < 0.15f; t += Time.deltaTime)
            {
                rb.linearVelocity = new Vector2(-Facing * 16f, 2f);
                yield return null;
            }
            rb.linearVelocity = new Vector2(-Facing * 2f, rb.linearVelocity.y);
            Visual.PlayRecoil(0.2f);
            SpawnArrow(1.5f * b, new Vector2(5f, 3f), 0.4f, 0.05f, true);
        }

        void Choke(float b)
        {
            actionLock = 0.5f;
            Visual.PlayThrust(0.3f);
            var r = MeleeBox(1.1f, 1.3f, HitSpec.Make(1.2f * b, new Vector2(11f, 5f), 0.6f));
            if (r == HitResult.Hit) Fx.Burst(Opponent.Center, Color.white, 12, 4f, 0.25f);
        }

        IEnumerator ArrowBarrage(float b)
        {
            actionLock = 0.6f;
            for (int k = 0; k < 5; k++)
            {
                Visual.PlayRecoil(0.08f);
                SpawnArrow(0.6f * b, new Vector2(1.2f, 0.8f), 0.18f, Random.Range(-0.25f, 0.3f), false);
                yield return new WaitForSeconds(0.09f);
            }
        }

        void HomingFireball(float b)
        {
            actionLock = 0.35f;
            Visual.PlayRecoil(0.3f);
            var p = Shoot(new Vector2(0.6f, 0.3f), new Vector2(Facing * 9f, 0f), HitSpec.Make(1.6f * b, new Vector2(5f, 4f), 0.4f, true),
                new Color(1f, 0.45f, 0.1f), new Vector2(0.55f, 0.55f), 0.38f, 3f, Art.Circle);
            p.Homing = true;
            p.TrailColor = new Color(1f, 0.8f, 0.2f);
        }

        void SummonMinions()
        {
            actionLock = 0.4f;
            Visual.PlayRecoil(0.3f);
            for (int k = 0; k < 3; k++)
                Minion.Spawn(this, Center + new Vector2(Facing * (0.6f + 0.5f * k), 0.3f));
        }

        void SummonCloud()
        {
            actionLock = 0.3f;
            Visual.PlayRecoil(0.3f);
            ThunderCloud.Spawn(this);
        }

        IEnumerator LeapSlam(float b)
        {
            actionLock = 1.0f;
            rb.linearVelocity = new Vector2(Facing * 6f, 14f);
            Visual.PlaySwing(0.5f);
            for (float t = 0f; t < 1.3f; t += Time.deltaTime)
            {
                actionLock = Mathf.Max(actionLock, 0.1f);
                if (t > 0.15f && Grounded) break;
                yield return null;
            }
            Visual.PlaySlam(0.2f);
            MeleeBox(2.2f, 1.6f, HitSpec.Make(1.8f * b, new Vector2(6f, 8f), 0.5f));
            Fx.Burst(new Vector2(Center.x + Facing * 1.2f, col.bounds.min.y), new Color(0.9f, 0.8f, 0.5f), 18, 5f, 0.35f);
            Match.Shake(0.25f);
            actionLock = 0.2f;
        }

        IEnumerator SpinGuard(float b)
        {
            spinGuardUntil = Time.time + 1.2f;
            actionLock = 1.2f;
            Visual.PlaySpin(1.2f);
            for (int k = 0; k < 4; k++)
            {
                if (Opponent != null && Opponent.OverlapsCircle(Center, 1.5f))
                {
                    float dir = Mathf.Sign(Opponent.Center.x - Center.x);
                    Opponent.ReceiveHit(this, HitSpec.Make(0.3f * b, new Vector2(dir * 2.5f, 2f), 0.2f));
                }
                yield return new WaitForSeconds(0.3f);
            }
        }

        void ThrowSpear(float b)
        {
            actionLock = 0.3f;
            spearOut = true;
            Visual.SetWeaponVisible(false);
            var p = Shoot(new Vector2(0.6f, 0.1f), new Vector2(Facing * 16f, 0f), HitSpec.Make(1.4f * b, new Vector2(5f, 3f), 0.35f, true),
                new Color(0.85f, 0.88f, 0.95f), new Vector2(1.5f, 0.1f), 0.3f, 3f, Art.Rounded);
            p.Destructible = false;
            p.ReturnAfter = 0.45f;
            p.ReturnMult = 0.8f * b;
            p.OnGone = () =>
            {
                spearOut = false;
                if (Visual != null) Visual.SetWeaponVisible(true);
            };
        }

        // ---------------------------------------------------------------- ultimates

        void FireUltimate()
        {
            Ult = 0f;
            UltsUsed++;
            Interrupt();
            hitstun = 0f;
            invuln = Mathf.Max(invuln, 0.6f);
            Match.Cinematic(this);
            Fx.Burst(Center, Skin.ultFx, 40, 8f, 0.5f);
            switch (Def.type)
            {
                case FighterClass.Melee:
                    swordAuraUntil = Time.time + 10f;
                    break;
                case FighterClass.Heavy:
                    StartCoroutine(UltSmash());
                    break;
                case FighterClass.Ranged:
                    StartCoroutine(UltThousandArrows());
                    break;
                case FighterClass.Magic:
                    FireRingUntil = Time.time + 10f;
                    FireRing.Spawn(this);
                    break;
                case FighterClass.Polearm:
                    StartCoroutine(UltPolearmKick());
                    break;
            }
        }

        IEnumerator UltSmash()
        {
            actionLock = 1.4f;
            invuln = 1.4f;
            rb.linearVelocity = new Vector2(0f, 15f);
            Visual.PlaySwing(0.4f);
            for (float t = 0f; t < 1.1f; t += Time.deltaTime)
            {
                if (t > 0.15f && Grounded) break;
                yield return null;
            }
            Visual.PlaySlam(0.2f);
            float y = col.bounds.min.y + 0.55f;
            for (int dir = -1; dir <= 1; dir += 2)
            {
                var wave = Projectile.Spawn(this, new Vector2(Center.x + dir * 0.6f, y), new Vector2(dir * 13f, 0f),
                    HitSpec.Make(3f, new Vector2(6f, 12f), 0.8f, true), Skin.ultFx, new Vector2(0.5f, 1.1f), 0.6f, 1.1f, Art.Rounded);
                wave.Pierce = true;
                wave.Destructible = false;
                wave.GrowRate = 1.4f;
            }
            Fx.Burst(new Vector2(Center.x, col.bounds.min.y), Skin.ultFx, 50, 9f, 0.5f);
            Match.Shake(0.6f);
            actionLock = 0.3f;
        }

        IEnumerator UltThousandArrows()
        {
            actionLock = 0.8f;
            invuln = 0.8f;
            Visual.PlayRecoil(0.3f);
            var lead = Shoot(new Vector2(0.5f, 0.3f), new Vector2(Facing * 9f, 12f), HitSpec.Make(0f, Vector2.zero, 0f, true),
                Skin.ultFx, new Vector2(1f, 0.18f), 0f, 0.45f, Art.Rounded);
            lead.Harmless = true;
            lead.Destructible = false;
            lead.OnExpire = p =>
            {
                Fx.Burst(p.transform.position, Skin.ultFx, 40, 7f, 0.4f);
                for (int k = 0; k < 18; k++)
                {
                    Vector2 target = (Opponent != null ? Opponent.Center : (Vector2)p.transform.position + Vector2.down * 4f)
                                     + new Vector2(Random.Range(-2.5f, 2.5f), Random.Range(-0.5f, 0.5f));
                    Vector2 from = (Vector2)p.transform.position + Random.insideUnitCircle * 0.6f;
                    var a = Projectile.Spawn(this, from, (target - from).normalized * Random.Range(14f, 19f),
                        HitSpec.Make(0.45f, new Vector2(1.5f, 1f), 0.15f, true), new Color(0.95f, 0.9f, 0.75f),
                        new Vector2(0.55f, 0.07f), 0.25f, 1.5f, Art.Rounded);
                    a.TrailColor = Skin.ultFx;
                }
            };
            yield break;
        }

        IEnumerator UltPolearmKick()
        {
            actionLock = 1.9f;
            invuln = 2.0f;
            var o = Opponent;
            bool inRange = o != null && !o.IsDead && Mathf.Abs(o.Center.x - Center.x) < 8f && Mathf.Abs(o.Center.y - Center.y) < 3f;
            if (!inRange)
            {
                Visual.PlayThrust(0.3f);
                for (float t = 0f; t < 0.25f; t += Time.deltaTime)
                {
                    rb.linearVelocity = new Vector2(Facing * 20f, 0f);
                    yield return null;
                }
                MeleeBox(2.2f, 1.4f, HitSpec.Make(2f, new Vector2(8f, 6f), 0.5f));
                yield break;
            }

            SetFacing(o.Center.x > Center.x ? 1 : -1);
            Vector2 from = transform.position;
            Vector2 to = new Vector2(o.transform.position.x - Facing * 1.3f, o.transform.position.y);
            for (float t = 0f; t < 0.15f; t += Time.deltaTime)
            {
                rb.linearVelocity = Vector2.zero;
                transform.position = Vector2.Lerp(from, to, t / 0.15f);
                Fx.Burst(Center, Skin.ultFx, 2, 2f, 0.3f);
                yield return null;
            }
            o.Stun(1.6f);
            o.SetHeld(true);
            heldTarget = o;
            Visual.PlayThrust(0.25f);
            o.ReceiveHit(this, HitSpec.Make(1.5f, Vector2.zero, 1.2f, false, true));
            Fx.Burst(o.Center, new Color(1f, 0.3f, 0.3f), 20, 5f, 0.35f);
            yield return new WaitForSeconds(0.35f);
            rb.linearVelocity = new Vector2(0f, 12f);
            Visual.PlaySpin(0.35f);
            yield return new WaitForSeconds(0.32f);
            Visual.PlaySwing(0.2f);
            ReleaseHeld();
            o.ReceiveHit(this, HitSpec.Make(2.5f, new Vector2(Facing * 12f, 9f), 0.8f, false, true));
            Fx.Burst(o.Center, Skin.ultFx, 45, 9f, 0.5f);
            Match.Shake(0.55f);
        }

        void OnDestroy()
        {
            ReleaseHeld();
        }
    }
}
