using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Menu sounds (sfx-events.md 5.4) routed to <see cref="Netcode.ChibiAudio.PlayUi"/>. Requests made during one frame
    /// are merged and only the most important one plays (error &gt; lock-in &gt; back &gt; slider step &gt; confirm &gt;
    /// navigate), so a click that is also a lock-in or a back action never doubles up. Flushed once per frame by
    /// <see cref="MenuInput.Poll"/> (one frame of latency). Display only: never touches game state.
    /// </summary>
    public static class UiSound
    {
        const string NavigateId = "core_ui_navigate", ConfirmId = "core_ui_confirm", BackId = "core_ui_back",
                     ErrorId = "core_ui_error", LockInId = "core_ui_lockin";

        static int pendingPriority;
        static string pendingId;
        static float pendingPitch = 1f;

        static void Request(string id, int priority, float pitch = 1f)
        {
            if (priority <= pendingPriority) return;
            pendingPriority = priority;
            pendingId = id;
            pendingPitch = pitch;
        }

        /// <summary>Focus / cursor / tab moved.</summary>
        public static void Navigate() => Request(NavigateId, 1);
        /// <summary>Accept, open a panel, open the pause menu.</summary>
        public static void Confirm() => Request(ConfirmId, 2);
        /// <summary>Slider / value step (navigate sound, pitched up).</summary>
        public static void Step() => Request(NavigateId, 3, 1.1f);
        /// <summary>Back, close, cancel.</summary>
        public static void Back() => Request(BackId, 4);
        /// <summary>Character lock-in, room joined, match found.</summary>
        public static void LockIn() => Request(LockInId, 5);
        /// <summary>Disabled button, invalid input, failed join.</summary>
        public static void Error() => Request(ErrorId, 6);

        /// <summary>Plays the frame's winning request (called once per frame).</summary>
        public static void Flush()
        {
            if (pendingPriority == 0) return;
            string id = pendingId;
            float pitch = pendingPitch;
            pendingPriority = 0;
            pendingId = null;
            pendingPitch = 1f;
            if (Application.isPlaying) Netcode.ChibiAudio.PlayUi(id, pitch);
        }
    }
}
