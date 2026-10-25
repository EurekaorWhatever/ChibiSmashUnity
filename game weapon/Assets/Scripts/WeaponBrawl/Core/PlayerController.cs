using UnityEngine;

namespace WeaponBrawl
{
    public static class Controls
    {
        public static KeyCode Left = KeyCode.A;
        public static KeyCode Right = KeyCode.D;
        public static KeyCode Jump = KeyCode.Space;
        public static KeyCode Attack = KeyCode.J;
        public static KeyCode Ultimate = KeyCode.R;
        public static readonly KeyCode[] Skills = { KeyCode.Q, KeyCode.E, KeyCode.F };

        public static KeyCode ChangeWeapon = KeyCode.Tab;
        public static KeyCode ToggleDummyAI = KeyCode.T;
        public static KeyCode DebugFillUltimate = KeyCode.P;
    }

    /// <summary>Keyboard input for the local player. The on-screen skill buttons call the same weapon methods.</summary>
    [RequireComponent(typeof(Fighter))]
    public class PlayerController : MonoBehaviour
    {
        Fighter fighter;

        void Awake() => fighter = GetComponent<Fighter>();

        void Update()
        {
            var weapon = fighter.Weapon;
            if (weapon == null) return;

            float x = 0f;
            if (Input.GetKey(Controls.Left) || Input.GetKey(KeyCode.LeftArrow)) x -= 1f;
            if (Input.GetKey(Controls.Right) || Input.GetKey(KeyCode.RightArrow)) x += 1f;
            fighter.Move(x);

            if (Input.GetKeyDown(Controls.Jump) || Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow)) fighter.Jump();
            if (Input.GetKey(Controls.Attack)) weapon.TryAttack(); // hold to keep the combo going
            for (int i = 0; i < Controls.Skills.Length; i++)
                if (Input.GetKeyDown(Controls.Skills[i])) weapon.TrySkill(i);
            if (Input.GetKeyDown(Controls.Ultimate)) weapon.TryUltimate();
            if (Input.GetKeyDown(Controls.DebugFillUltimate)) fighter.AddUlt(Fighter.UltMax);
        }
    }
}
