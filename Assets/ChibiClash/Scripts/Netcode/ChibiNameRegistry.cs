using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.CloudCode;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Worldwide-unique player names through the ChibiNames Cloud Code script (Assets/ChibiClash/CloudCode/Chibi_Clash.js; data in Cloud
    /// Save private custom items, writable only by the server). Check / Claim / Join / Stats never throw: every failure
    /// (offline, timeout, script not deployed, server error) becomes <see cref="ChibiNameStatus.ServiceUnavailable"/>.
    /// Calls go over HTTPS, so they work in the Web build too. Results are delivered on Unity's main thread.
    /// Design: docs/architecture/netcode/player-names.md.
    /// </summary>
    public sealed class ChibiNameRegistry
    {
        /// <summary>Cloud Code script name (dashboard: Cloud Code > Scripts). Must match exactly.</summary>
        public const string ScriptName = "Chibi_Clash";   // the dashboard script name (created 2026-11-23); code: Assets/ChibiClash/CloudCode/Chibi_Clash.js
        /// <summary>Per-call timeout in seconds (claim waits ~0.3 s on the server for its read-back).</summary>
        public const float TimeoutSeconds = 10f;

        /// <summary>Runs one script call and returns its JSON text (injectable for tests; default: Cloud Code).</summary>
        readonly Func<Dictionary<string, object>, Task<string>> _call;
        /// <summary>Sign-in step before a call; returns null on success, else a reason (default: NetworkRoomManager).</summary>
        readonly Func<Task<string>> _signIn;

        string _verifiedKey = "";      // normalised name the server confirmed as ours this session
        bool _joined;

        /// <summary>Create a registry. Both arguments are optional (defaults: anonymous sign-in + Cloud Code).</summary>
        public ChibiNameRegistry(Func<Dictionary<string, object>, Task<string>> call = null, Func<Task<string>> signIn = null)
        {
            _call = call ?? (args => CloudCodeService.Instance.CallEndpointAsync(ScriptName, args));
            _signIn = signIn ?? NetworkRoomManager.EnsureSignedInAsync;
        }

        /// <summary>True when <paramref name="name"/> was confirmed ours by the server during this session.</summary>
        public bool IsVerified(string name) =>
            _verifiedKey.Length > 0 && _verifiedKey == ChibiNameRegistryRules.Normalize(name);

        /// <summary>Forget the verified name (account switch).</summary>
        public void ResetVerified() => _verifiedKey = "";

        /// <summary>Is the name free? (available / yours / taken / invalid / unavailable). Reserves nothing.</summary>
        public Task<ChibiNameReply> CheckAsync(string name) => Run("check", name, null);

        /// <summary>Reserve the name for this player worldwide and release our previous one. Ok = it is ours now.</summary>
        public async Task<ChibiNameReply> ClaimAsync(string name)
        {
            string n = (name ?? "").Trim();
            if (ChibiSocialRules.ValidateName(n) != null) return new ChibiNameReply(ChibiNameStatus.Invalid);
            var r = await Run("claim", n, n);
            if (ChibiNameRegistryRules.IsOwned(r.Status)) _verifiedKey = ChibiNameRegistryRules.Normalize(n);
            else if (r.Status == ChibiNameStatus.Taken && IsVerified(n)) _verifiedKey = "";
            return r;
        }

        /// <summary>Count this launch once (later calls in the same run do nothing). Fire and forget; never throws.</summary>
        public async void JoinOncePerLaunch()
        {
            if (_joined) return;
            _joined = true;
            await Run("join", null, null);   // a failed count is not retried this run (no request storm when offline)
        }

        /// <summary>Registered players and total launches.</summary>
        public Task<ChibiNameReply> StatsAsync() => Run("stats", null, null);

        async Task<ChibiNameReply> Run(string action, string name, string requestedName)
        {
            try
            {
                // ONE deadline for the whole request: sign-in AND the script call (a hung sign-in cannot trap the UI).
                Task<string> work = SignInThenCall(action, name);
                // Frame-polled timeout (no timer threads: safe in the single-threaded Web build).
                float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
                while (!work.IsCompleted)
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        LogOnce("ChibiNameRegistry: " + action + " timed out");
                        // Observe a late fault without a thread-pool hop (WebGL has no threads).
                        _ = work.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.ExecuteSynchronously);
                        return ChibiNameReply.Unavailable;
                    }
                    await Task.Yield();
                }
                if (work.IsFaulted || work.IsCanceled)
                {
                    LogOnce("ChibiNameRegistry: " + action + " failed: " + work.Exception?.GetBaseException().Message);
                    return ChibiNameReply.Unavailable;
                }
                return work.Result == null ? ChibiNameReply.Unavailable : ChibiNameRegistryRules.ParseReply(work.Result, requestedName);
            }
            catch (Exception e)
            {
                LogOnce("ChibiNameRegistry: " + action + " failed: " + e.Message);
                return ChibiNameReply.Unavailable;
            }
        }

        /// <summary>Sign in, then call the script. Null when sign-in failed (reported as ServiceUnavailable).</summary>
        async Task<string> SignInThenCall(string action, string name)
        {
            string signInError = await _signIn();
            if (signInError != null) return null;
            var args = new Dictionary<string, object> { { "action", action } };
            if (name != null) args["name"] = name;
            return await _call(args);
        }

        // Rate-limited log: at most one warning per 30 s.
        static float _lastLog = -999f;
        static void LogOnce(string msg)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastLog < 30f) return;
            _lastLog = now;
            Debug.LogWarning(msg);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => _lastLog = -999f;
    }
}
