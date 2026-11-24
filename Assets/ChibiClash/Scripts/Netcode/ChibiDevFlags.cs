using System;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// DEV-ONLY test aids for running several game windows on one PC as different online players (a real 3-4 player Relay
    /// match from the editor plus built exes). Inert in release builds: every value is null unless
    /// <c>Application.isEditor || Debug.isDebugBuild</c>.
    ///
    ///   -authProfile NAME   Unity Authentication profile (AuthenticationService.SwitchProfile before the anonymous sign-in),
    ///                       so each window has its own anonymous player id and session token.
    ///   -devName NAME       display name for this run only; never written to PlayerPrefs (the exes share PlayerPrefs).
    ///
    /// From the editor (or a dev build console): <c>ChibiDevFlags.SetAuthProfile("p2")</c> before going online.
    /// </summary>
    /// <example><code>ChibiClash.exe -authProfile p2 -devName Tester2</code></example>
    public static class ChibiDevFlags
    {
        static bool _parsed;
        static string _profile, _name;

        /// <summary>True only in the editor and in development builds.</summary>
        public static bool Allowed => Application.isEditor || Debug.isDebugBuild;

        /// <summary>The dev Authentication profile, or null (release build, or not set).</summary>
        public static string AuthProfile { get { Parse(); return Allowed ? _profile : null; } }

        /// <summary>The dev display name for this run, or null (release build, or not set). Never saved.</summary>
        public static string DevName { get { Parse(); return Allowed ? _name : null; } }

        /// <summary>Dev only: use this Authentication profile from the next sign-in on. Ignored in release builds.</summary>
        public static void SetAuthProfile(string profile) { Parse(); if (Allowed) _profile = CleanProfile(profile); }

        /// <summary>Dev only: this run's display name (not saved). Ignored in release builds.</summary>
        public static void SetDevName(string name) { Parse(); if (Allowed) _name = CleanName(name); }

        /// <summary>A legal profile name (1-30 of A-Z a-z 0-9 - _), else null.</summary>
        public static string CleanProfile(string p)
        {
            if (string.IsNullOrEmpty(p) || p.Length > 30) return null;
            foreach (char c in p) if (!(char.IsLetterOrDigit(c) && c < 128) && c != '-' && c != '_') return null;
            return p;
        }

        /// <summary>A dev name filtered like a saved name, or null.</summary>
        static string CleanName(string n)
        {
            string c = ChibiSocialRules.CleanSavedName(n ?? "");
            return c.Length > 0 ? c : null;
        }

        /// <summary>Value after <paramref name="flag"/> in <paramref name="args"/>, or null.</summary>
        public static string ArgValue(string[] args, string flag)
        {
            if (args == null) return null;
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        static void Parse()
        {
            if (_parsed) return;
            _parsed = true;
            if (!Allowed) return;
            string[] args;
            try { args = Environment.GetCommandLineArgs(); } catch (Exception) { return; }
            _profile = CleanProfile(ArgValue(args, "-authProfile"));
            _name = CleanName(ArgValue(args, "-devName"));
        }

        /// <summary>
        /// Switch to the dev profile if one is set. Call after UnityServices.InitializeAsync and before any sign-in
        /// (SwitchProfile only works while signed out). No-op in release builds and when no profile is set.
        /// </summary>
        public static void ApplyAuthProfile()
        {
            string p = AuthProfile;
            if (p == null || UnityServices.State != ServicesInitializationState.Initialized) return;
            var auth = AuthenticationService.Instance;
            if (auth.IsSignedIn || auth.Profile == p) return;
            auth.SwitchProfile(p);
            Debug.Log("[ChibiDevFlags] Authentication profile: " + p);
        }

        /// <summary>No domain reload between Play sessions: forget the flags at each Play start.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _parsed = false; _profile = null; _name = null; }
    }
}
