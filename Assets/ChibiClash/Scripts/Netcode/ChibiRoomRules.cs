using System;
using System.Collections.Generic;

namespace ChibiClash.Netcode
{
    /// <summary>One player seat in a room, as every client sees it (already validated and safe to draw).</summary>
    public struct ChibiRoomSlot
    {
        public string PlayerId, Name;
        public FighterClass Class;
        public int Passive;
        /// <summary>The player pressed OK after choosing a fighter: they are "in" for the next fight.</summary>
        public bool Ready;
        /// <summary>Guest only: changes every time the guest presses START FIGHT (the host launches on a new value).</summary>
        public string StartNonce;
        /// <summary>Guest only: the host launch ("go") this guest accepted and is connecting to.</summary>
        public string Ack;
    }

    /// <summary>A room as the screen shows it (built from the Lobby by <see cref="ChibiRoomRules.Snapshot"/>).</summary>
    public sealed class ChibiRoomSnapshot
    {
        public string Id = "", Name = "", Code = "", HostId = "";
        public int Stage;
        /// <summary>Rules v20 (ST-5): the host's stage hazards switch, from the member key "hz" ("0" = off; anything else or
        /// absent = on, the design default).</summary>
        public bool HazardsOff;
        /// <summary>Host's launch counter: a NEW value tells the guest to connect to <see cref="RelayCode"/> now.</summary>
        public string Go = "";
        public string RelayCode = "";
        /// <summary>Host first (index 0), then the guest. At most 2.</summary>
        public readonly List<ChibiRoomSlot> Slots = new List<ChibiRoomSlot>(2);

        /// <summary>The seat of <paramref name="playerId"/>, or -1.</summary>
        public int IndexOf(string playerId)
        {
            for (int i = 0; i < Slots.Count; i++) if (Slots[i].PlayerId == playerId) return i;
            return -1;
        }
    }

    /// <summary>Raw lobby fields (one per player), so the rules stay testable without the Unity Lobby SDK.</summary>
    public struct ChibiRoomRawPlayer
    {
        public string Id, Name, Class, Passive, Ready, Start, Ack;
    }

    /// <summary>
    /// Rules of a private room (docs/architecture/netcode/online-rooms.md "Rooms"): who is in, when START FIGHT works,
    /// when the host launches and when the guest connects. Everything read from the Lobby is another player's data, so
    /// it is validated here (unknown class -> not ready, names and room names sanitised). Pure C#; ChibiRoomRulesTests.
    /// </summary>
    /// <example><code>
    /// var snap = ChibiRoomRules.Snapshot(id, name, code, hostId, stage, go, relay, rawPlayers);
    /// bool canFight = ChibiRoomRules.CanStart(snap);
    /// </code></example>
    public static class ChibiRoomRules
    {
        /// <summary>Players per room (this game is 1 v 1).</summary>
        public const int Seats = 2;
        /// <summary>Longest nonce accepted from the network.</summary>
        public const int MaxNonce = 16;

        /// <summary>Build the snapshot from raw lobby values; drops malformed players, host first, max 2 seats.</summary>
        /// <param name="hazards">Room key "hz" (rules v20, ST-5): "0" = hazards off; null / anything else = on.</param>
        public static ChibiRoomSnapshot Snapshot(string id, string roomName, string code, string hostId, string stage, string go,
                                                 string relayCode, IList<ChibiRoomRawPlayer> players, string hazards = null)
        {
            var s = new ChibiRoomSnapshot
            {
                Id = id ?? "", Name = ChibiSocialRules.SafeRoomName(roomName), Code = ChibiSocialRules.NormalizeCode(code) ?? "",
                HostId = hostId ?? "", Stage = ParseStage(stage), Go = Nonce(go), RelayCode = ValidRelayCode(relayCode) ? relayCode : "",
                HazardsOff = ParseHazardsOff(hazards),
            };
            if (players == null) return s;
            // Host seat first, then everyone else in lobby order.
            for (int pass = 0; pass < 2 && s.Slots.Count < Seats; pass++)
                foreach (var p in players)
                {
                    if (s.Slots.Count >= Seats) break;
                    if (string.IsNullOrEmpty(p.Id) || (p.Id == s.HostId) != (pass == 0)) continue;
                    s.Slots.Add(Slot(p));
                }
            return s;
        }

        static ChibiRoomSlot Slot(ChibiRoomRawPlayer p)
        {
            bool classOk = int.TryParse(p.Class, out int c) && c >= 0 && c <= (int)FighterClass.Polearm;
            return new ChibiRoomSlot
            {
                PlayerId = p.Id,
                // Self-reported lobby data, not bound to the account: shown without the #tag (no impersonation).
                Name = string.IsNullOrEmpty(p.Name) ? "Player" : ChibiSocialRules.PeerDisplayName(p.Name, verified: false),
                Class = classOk ? (FighterClass)c : FighterClass.Melee,
                Passive = p.Passive == "1" ? 1 : 0,
                Ready = classOk && p.Ready == "1",   // a player with no valid fighter is never ready
                StartNonce = Nonce(p.Start),
                Ack = Nonce(p.Ack),
            };
        }

        /// <summary>A Relay join code: 6-12 letters / digits (anything else is never passed to the Relay service).</summary>
        public static bool ValidRelayCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length < 6 || code.Length > 12) return false;
            foreach (char c in code)
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) return false;
            return true;
        }

        /// <summary>Stage index 0..7 (rules v20, ST-1; anything else -> 0).</summary>
        /// <remarks>SEC-001: strict: 1-2 ASCII digits only (no sign, spaces or overflow); anything else is 0.</remarks>
        public static int ParseStage(string stage)
        {
            if (string.IsNullOrEmpty(stage) || stage.Length > 2) return 0;
            int v = 0;
            foreach (char c in stage)
            {
                if (c < '0' || c > '9') return 0;
                v = v * 10 + (c - '0');
            }
            return v < ChibiStageTable.Count ? v : 0;
        }

        /// <summary>Room hazards key "hz" (ST-5): exactly "0" means off; anything else (absent, "1", garbage) means on.</summary>
        public static bool ParseHazardsOff(string hz) => hz == "0";

        /// <summary>Value written to the room's "hz" key.</summary>
        public static string HazardsValue(bool hazardsOn) => hazardsOn ? "1" : "0";

        static string Nonce(string n) => string.IsNullOrEmpty(n) || n.Length > MaxNonce ? "" : n;

        /// <summary>START FIGHT works only when BOTH seats are taken and BOTH players chose a fighter and pressed OK.</summary>
        public static bool CanStart(ChibiRoomSnapshot s)
        {
            if (s == null || s.Slots.Count != Seats) return false;
            foreach (var slot in s.Slots) if (!slot.Ready) return false;
            return true;
        }

        /// <summary>Why START FIGHT is not available yet (for the button hint); "" when it is.</summary>
        public static string WaitingFor(ChibiRoomSnapshot s, string myId)
        {
            if (s == null) return "Loading the room...";
            if (s.Slots.Count < Seats) return "Waiting for your friend to join the room.";
            int me = s.IndexOf(myId);
            if (me >= 0 && !s.Slots[me].Ready) return "Choose your fighter and press OK.";
            foreach (var slot in s.Slots) if (!slot.Ready) return "Waiting for " + slot.Name + " to press OK.";
            return "";
        }

        /// <summary>Host: launch when the guest pressed START (a nonce not handled before) and both are ready.</summary>
        public static bool HostShouldLaunch(ChibiRoomSnapshot s, string lastHandledGuestNonce)
        {
            if (!CanStart(s)) return false;
            var guest = s.Slots[1];
            return guest.StartNonce.Length > 0 && guest.StartNonce != (lastHandledGuestNonce ?? "");
        }

        /// <summary>Guest: connect when the host announced a NEW launch with a relay code (the baseline is the value seen on entering).</summary>
        public static bool GuestShouldConnect(ChibiRoomSnapshot s, string baselineGo, string myId)
        {
            if (s == null || s.HostId == myId || s.IndexOf(myId) != 1) return false;
            return s.Go.Length > 0 && s.Go != (baselineGo ?? "") && s.RelayCode.Length > 0;
        }

        /// <summary>A short random token for launch / start presses (not a secret: only "is it new").</summary>
        public static string NewNonce() => Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>
        /// True when a room published by another build can host a match with ours: the room's protocol tag
        /// ("p&lt;protocol&gt;r&lt;rules&gt;", NetworkRoomManager.ProtocolTag) must equal ours exactly. A missing tag (very old
        /// build, or tampered data) is refused. Checked right after joining by code, so a version mismatch is reported
        /// at once instead of after both players press OK and the handshake refuses (network review 2026-11-10, finding 1).
        /// </summary>
        /// <example><code>if (!ChibiRoomRules.SameBuild(roomTag, NetworkRoomManager.ProtocolTag)) LeaveWithVersionMessage();</code></example>
        public static bool SameBuild(string roomProtocolTag, string myProtocolTag)
            => !string.IsNullOrEmpty(roomProtocolTag) && !string.IsNullOrEmpty(myProtocolTag) &&
               string.Equals(roomProtocolTag, myProtocolTag, StringComparison.Ordinal);
    }
}
