using UnityEditor;
using UnityEngine;

namespace WeaponBrawl.EditorTools
{
    /// <summary>
    /// Unity's old Input Manager can only read one specific gamepad through named axes, and those can't be
    /// created at runtime. This adds "P1 Horizontal", "P2 DPad X" and so on to the project's Input Manager.
    /// </summary>
    [InitializeOnLoad]
    public static class InputAxesInstaller
    {
        const int JoystickAxisType = 2;

        // Xbox layout on Windows: left stick = axes 0/1, D-pad = axes 5/6 (zero-based).
        static readonly (string name, int axis, bool invert)[] Axes =
        {
            ("Horizontal", 0, false),
            ("Vertical", 1, true),
            ("DPad X", 5, false),
            ("DPad Y", 6, false),
        };

        static InputAxesInstaller() => EditorApplication.delayCall += () => Install(false);

        [MenuItem("Tools/Weapon Brawl/Install Gamepad Axes")]
        static void InstallFromMenu() => Install(true);

        static void Install(bool verbose)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/InputManager.asset");
            if (assets == null || assets.Length == 0) return;
            var manager = new SerializedObject(assets[0]);
            var axes = manager.FindProperty("m_Axes");

            int added = 0;
            for (int player = 0; player < 2; player++)
            {
                foreach (var a in Axes)
                {
                    string name = GameInput.AxisName(player, a.name);
                    if (Exists(axes, name)) continue;
                    AddAxis(axes, name, a.axis, player + 1, a.invert);
                    added++;
                }
            }

            if (added > 0)
            {
                manager.ApplyModifiedProperties();
                Debug.Log($"WeaponBrawl: added {added} gamepad axes to the Input Manager.");
            }
            else if (verbose)
            {
                Debug.Log("WeaponBrawl: gamepad axes are already installed.");
            }
        }

        static bool Exists(SerializedProperty axes, string name)
        {
            for (int i = 0; i < axes.arraySize; i++)
                if (axes.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name").stringValue == name) return true;
            return false;
        }

        static void AddAxis(SerializedProperty axes, string name, int axis, int joystick, bool invert)
        {
            axes.arraySize++;
            var p = axes.GetArrayElementAtIndex(axes.arraySize - 1);
            p.FindPropertyRelative("m_Name").stringValue = name;
            p.FindPropertyRelative("descriptiveName").stringValue = "";
            p.FindPropertyRelative("descriptiveNegativeName").stringValue = "";
            p.FindPropertyRelative("negativeButton").stringValue = "";
            p.FindPropertyRelative("positiveButton").stringValue = "";
            p.FindPropertyRelative("altNegativeButton").stringValue = "";
            p.FindPropertyRelative("altPositiveButton").stringValue = "";
            p.FindPropertyRelative("gravity").floatValue = 0f;
            p.FindPropertyRelative("dead").floatValue = 0.2f;
            p.FindPropertyRelative("sensitivity").floatValue = 1f;
            p.FindPropertyRelative("snap").boolValue = false;
            p.FindPropertyRelative("invert").boolValue = invert;
            p.FindPropertyRelative("type").intValue = JoystickAxisType;
            p.FindPropertyRelative("axis").intValue = axis;
            p.FindPropertyRelative("joyNum").intValue = joystick;
        }
    }
}
