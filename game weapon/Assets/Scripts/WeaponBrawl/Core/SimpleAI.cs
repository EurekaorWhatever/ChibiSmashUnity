using UnityEngine;

namespace WeaponBrawl
{
    /// <summary>Very small opponent brain: walk into range, attack, sometimes use skills, ult when ready.</summary>
    [RequireComponent(typeof(Fighter))]
    public class SimpleAI : MonoBehaviour
    {
        public bool active = true;

        Fighter self;
        float nextDecision;

        void Awake() => self = GetComponent<Fighter>();

        float PreferredRange
        {
            get
            {
                switch (self.Definition.type)
                {
                    case WeaponType.Ranged:
                    case WeaponType.Magic: return 6f;
                    case WeaponType.Polearm: return 2.4f;
                    default: return 1.6f;
                }
            }
        }

        void Update()
        {
            var weapon = self.Weapon;
            if (weapon == null) return;
            var target = active ? self.NearestEnemy() : null;
            if (target == null || self.IsDead)
            {
                self.Move(0f);
                return;
            }

            float dx = target.transform.position.x - transform.position.x;
            float dist = Mathf.Abs(dx);
            float range = PreferredRange;
            bool keepsDistance = range > 3f;

            if (dist > range) self.Move(Mathf.Sign(dx));
            else if (keepsDistance && dist < range * 0.4f) self.Move(-Mathf.Sign(dx));
            else
            {
                self.Move(0f);
                if (!weapon.Busy) self.FaceTowards(target);
            }

            if (Time.time < nextDecision || dist > range + 0.5f) return;
            nextDecision = Time.time + Random.Range(0.35f, 0.8f);
            self.FaceTowards(target);
            if (self.UltReady && weapon.TryUltimate()) return;
            if (Random.value < 0.3f && weapon.TrySkill(Random.Range(0, self.Definition.skills.Length))) return;
            weapon.TryAttack();
        }
    }
}
