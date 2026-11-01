using System.Text;
using UnityEngine;

namespace WeaponBrawl
{
    /// <summary>Bottom-right action cluster for P1: big Attack button, 3 skill buttons, Ultimate button.</summary>
    public class SkillBarUI : MonoBehaviour
    {
        SkillButton attack, ultimate;
        readonly SkillButton[] skills = new SkillButton[3];
        WeaponBehaviour weapon;

        public static SkillBarUI Create(Canvas canvas)
        {
            var rt = UIFactory.Node("SkillBar", canvas.transform)
                .Place(new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-30f, 30f), new Vector2(640f, 480f));
            var bar = rt.gameObject.AddComponent<SkillBarUI>();
            bar.attack = SkillButton.Create(rt, "Attack", new Vector2(-150f, 150f), 180f);
            bar.skills[0] = SkillButton.Create(rt, "Skill1", new Vector2(-385f, 120f), 120f);
            bar.skills[1] = SkillButton.Create(rt, "Skill2", new Vector2(-345f, 300f), 120f);
            bar.skills[2] = SkillButton.Create(rt, "Skill3", new Vector2(-160f, 380f), 120f);
            bar.ultimate = SkillButton.Create(rt, "Ultimate", new Vector2(-555f, 330f), 140f);
            return bar;
        }

        public void Bind(Fighter fighter)
        {
            if (weapon != null) weapon.ActionUsed -= OnActionUsed;
            weapon = fighter.Weapon;
            weapon.ActionUsed += OnActionUsed;
            var def = fighter.Definition;
            var w = weapon;
            var input = GameInput.P1;

            attack.Bind("Attack", () => input.Label(GameAction.Attack), UITheme.P1,
                () => w.CanAct, null,
                () => $"{w.NextComboStep + 1}/{def.comboLength}",
                () => w.TryAttack(), flashWhenReady: false);

            for (int i = 0; i < skills.Length; i++)
            {
                int index = i;
                if (index >= def.skills.Length)
                {
                    skills[i].SetLocked("No skill");
                    continue;
                }
                string glyph = Initials(def.skills[index].name);
                var action = GameAction.Skill1 + index;
                skills[i].Bind(def.skills[index].name, () => input.Label(action), UITheme.Paper,
                    () => w.CooldownRemaining(index) <= 0f,
                    () => w.CooldownFraction(index),
                    () =>
                    {
                        float cd = w.CooldownRemaining(index);
                        return cd > 0f ? cd.ToString(cd < 1f ? "0.0" : "0") : glyph;
                    },
                    () => w.TrySkill(index));
            }

            ultimate.Bind(def.ultimate.name, () => input.Label(GameAction.Ultimate), UITheme.Gold,
                () => fighter.UltReady,
                () => 1f - fighter.UltMeter / Fighter.UltMax,
                () => fighter.UltReady ? "ULT" : $"{Mathf.FloorToInt(fighter.UltMeter)}%",
                () => w.TryUltimate(), pulse: true);
        }

        void OnActionUsed(int action)
        {
            if (action == WeaponBehaviour.AttackAction) attack.Flash();
            else if (action == WeaponBehaviour.UltimateAction) ultimate.Flash();
            else if (action >= 0 && action < skills.Length) skills[action].Flash();
        }

        void OnDestroy()
        {
            if (weapon != null) weapon.ActionUsed -= OnActionUsed;
        }

        /// <summary>"Dash and Thrust" -> "DT".</summary>
        static string Initials(string name)
        {
            var sb = new StringBuilder();
            foreach (var word in name.Split(' '))
                if (word.Length > 0 && char.IsUpper(word[0]) && sb.Length < 3) sb.Append(word[0]);
            return sb.Length > 0 ? sb.ToString() : name.Substring(0, 1);
        }
    }
}
