using System;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>What the name service said (or why it could not say anything).</summary>
    public enum ChibiNameStatus : byte
    {
        /// <summary>claim: the name is now ours. join / stats: done.</summary>
        Ok,
        /// <summary>check: nobody owns the name.</summary>
        Available,
        /// <summary>check: we already own the name.</summary>
        Yours,
        /// <summary>Another player owns the name.</summary>
        Taken,
        /// <summary>The server refused the name (rules) or the request.</summary>
        Invalid,
        /// <summary>No answer we can trust: offline, timeout, script missing, server error. Play offline; claim later.</summary>
        ServiceUnavailable,
        /// <summary>claim: this player changed name too recently (server rename cooldown). Keep the old name for now.</summary>
        TooSoon,
    }

    /// <summary>Raw JSON reply of the ChibiNames Cloud Code script (JsonUtility; public fields; stripping-safe).</summary>
    [Serializable]
    public sealed class ChibiNameReplyJson
    {
        /// <summary>"ok", "available", "yours", "taken", "invalid", "wait" (rename cooldown) or "error".</summary>
        public string status = "";
        /// <summary>The claimed name (claim only).</summary>
        public string name = "";
        /// <summary>Why a name was invalid (server text; not shown, the client has its own messages).</summary>
        public string reason = "";
        /// <summary>stats: unique registered players.</summary>
        public long players;
        /// <summary>stats: game launches (join calls).</summary>
        public long joins;
    }

    /// <summary>A parsed, checked reply. Never null fields.</summary>
    public readonly struct ChibiNameReply
    {
        /// <summary>The outcome.</summary>
        public readonly ChibiNameStatus Status;
        /// <summary>The name the server confirmed (claim), else "".</summary>
        public readonly string Name;
        /// <summary>stats: registered players (0 when not a stats reply).</summary>
        public readonly long Players;
        /// <summary>stats: total launches (0 when not a stats reply).</summary>
        public readonly long Joins;

        /// <summary>Build a reply.</summary>
        public ChibiNameReply(ChibiNameStatus status, string name = "", long players = 0, long joins = 0)
        {
            Status = status;
            Name = name ?? "";
            Players = players < 0 ? 0 : players;
            Joins = joins < 0 ? 0 : joins;
        }

        /// <summary>The reply used for every failure that is not the server's verdict.</summary>
        public static ChibiNameReply Unavailable => new ChibiNameReply(ChibiNameStatus.ServiceUnavailable);
    }

    /// <summary>
    /// Pure, unit-tested rules of the worldwide-unique name registry (ChibiNameRegistry does the network calls).
    /// The server (Assets/ChibiClash/CloudCode/Chibi_Clash.js) is the authority; these rules only shape requests and read replies.
    /// </summary>
    public static class ChibiNameRegistryRules
    {
        /// <summary>Exactly what the player sees when another player owns the name (spec, 2026-11-22).</summary>
        public const string TakenMessage = "This name is already taken";
        /// <summary>Shown when the name could not be checked before an online action.</summary>
        public const string UnavailableMessage = "Could not check your name online. Check your internet and try again.";
        /// <summary>Shown when the server's rename cooldown refuses a new name.</summary>
        public const string TooSoonMessage = "You changed your name a moment ago. Try again in a minute.";
        /// <summary>Names the dev automation uses (GameManager.DevAutomation.cs).</summary>
        static readonly string[] DevNames = { "DevHost", "DevGuest", "DevPlayer" };

        /// <summary>The registry key of a name: lower case (invariant), trimmed. "Kira" and "KIRA" are the same name.</summary>
        public static string Normalize(string name) => (name ?? "").Trim().ToLowerInvariant();

        /// <summary>
        /// Read a script reply. Accepts the JSON object text the Cloud Code SDK returns; anything else (null, garbage,
        /// unknown status, "error") is <see cref="ChibiNameStatus.ServiceUnavailable"/>. A claim reply whose name is
        /// not the requested one (case-insensitive) is refused too.
        /// </summary>
        /// <example><code>var r = ChibiNameRegistryRules.ParseReply(json, "Kira");   // r.Status == Ok</code></example>
        public static ChibiNameReply ParseReply(string json, string requestedName = null)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 4096) return ChibiNameReply.Unavailable;
            ChibiNameReplyJson raw;
            try { raw = JsonUtility.FromJson<ChibiNameReplyJson>(json); }
            catch (Exception) { return ChibiNameReply.Unavailable; }
            if (raw == null) return ChibiNameReply.Unavailable;
            switch (raw.status)
            {
                case "ok":
                    if (requestedName != null && Normalize(raw.name) != Normalize(requestedName)) return ChibiNameReply.Unavailable;
                    return new ChibiNameReply(ChibiNameStatus.Ok, raw.name, raw.players, raw.joins);
                case "available": return new ChibiNameReply(ChibiNameStatus.Available);
                case "yours": return new ChibiNameReply(ChibiNameStatus.Yours);
                case "taken": return new ChibiNameReply(ChibiNameStatus.Taken);
                case "invalid": return new ChibiNameReply(ChibiNameStatus.Invalid);
                case "wait": return new ChibiNameReply(ChibiNameStatus.TooSoon);
                default: return ChibiNameReply.Unavailable;
            }
        }

        /// <summary>
        /// The text the name window shows for a claim result, or null when the name may be saved. ServiceUnavailable
        /// returns null: the name is saved locally as UNVERIFIED and claimed again before any online use.
        /// </summary>
        public static string DialogError(ChibiNameStatus s)
        {
            switch (s)
            {
                case ChibiNameStatus.Taken: return TakenMessage;
                case ChibiNameStatus.Invalid: return "Please choose a different name.";
                case ChibiNameStatus.TooSoon: return TooSoonMessage;
                default: return null;
            }
        }

        /// <summary>True when the server confirmed the name belongs to us.</summary>
        public static bool IsOwned(ChibiNameStatus s) => s == ChibiNameStatus.Ok || s == ChibiNameStatus.Yours;

        /// <summary>True for the dev automation names (DevHost / DevGuest / DevPlayer, exact).</summary>
        public static bool IsDevName(string name) => Array.IndexOf(DevNames, (name ?? "").Trim()) >= 0;

        /// <summary>
        /// Editor and development builds only: the dev automation names skip the claim, and a service failure does not
        /// block online play (so the 2-instance tests work before the script is deployed). Release builds never skip.
        /// </summary>
        public static bool MaySkipClaim(bool isEditor, bool isDebugBuild, string name, ChibiNameStatus? lastFailure = null)
        {
            if (!(isEditor || isDebugBuild)) return false;
            return IsDevName(name) || lastFailure == ChibiNameStatus.ServiceUnavailable;
        }
    }
}
