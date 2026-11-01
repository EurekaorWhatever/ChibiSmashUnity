using UnityEngine;

namespace WeaponBrawl
{
    /// <summary>Gamepad + keyboard input for a local player. The on-screen skill buttons call the same weapon methods.</summary>
    [RequireComponent(typeof(Fighter))]
    public class PlayerController : MonoBehaviour
    {
        public PlayerInputMap Input { get; set; }

        Fighter fighter;

        void Awake() => fighter = GetComponent<Fighter>();

        void Update()
        {
            var weapon = fighter.Weapon;
            if (weapon == null || Input == null) return;

            fighter.Move(Mathf.Abs(Input.MoveX) > 0.2f ? Input.MoveX : 0f);

            if (Input.Down(GameAction.Jump)) fighter.Jump();
            if (Input.Held(GameAction.Attack)) weapon.TryAttack(); // hold to keep the combo going
            if (Input.Down(GameAction.Skill1)) weapon.TrySkill(0);
            if (Input.Down(GameAction.Skill2)) weapon.TrySkill(1);
            if (Input.Down(GameAction.Skill3)) weapon.TrySkill(2);
            if (Input.Down(GameAction.Ultimate)) weapon.TryUltimate();
            if (UnityEngine.Input.GetKeyDown(KeyCode.F1)) fighter.AddUlt(Fighter.UltMax); // debug
        }
    }
}
