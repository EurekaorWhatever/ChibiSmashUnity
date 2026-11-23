using System;

namespace ChibiClash.Netcode
{
    /// <summary>8-way stick direction. Value 0 is neutral so default(ChibiInputPacket) is "no input".</summary>
    public enum ChibiDir : byte { Neutral = 0, N = 1, NE = 2, E = 3, SE = 4, S = 5, SW = 6, W = 7, NW = 8 }

    /// <summary>Button bits, already shifted into their position inside <see cref="ChibiInputPacket.Raw"/>.</summary>
    [Flags]
    public enum ChibiButtons : ushort
    {
        None = 0,
        Light = 1 << 4,
        Heavy = 1 << 5,
        Jump = 1 << 6,
        Shield = 1 << 7,
        Skill1 = 1 << 8,
        Skill2 = 1 << 9,
        Skill3 = 1 << 10,
        Ultimate = 1 << 11,
        /// <summary>Dash request (bit 12, press edge). Consumed by the simulation since RulesVersion 6; never enters the attack input buffer.</summary>
        Dash = 1 << 12,
        /// <summary>
        /// Special (B) button, bit 15 (protocol 3, rules v18, decision CH-3). Held level like every button: the sim takes
        /// press edges for the four directional specials and reads the level for charge / hold moves.
        /// </summary>
        Special = 1 << 15,
    }

    /// <summary>
    /// One player's input for one simulation frame, packed into 16 bits:
    ///
    ///   bit  0..3   direction (ChibiDir, 0..8; 9..15 are illegal and sanitised to neutral)
    ///   bit  4      light attack        bit  8   skill 1
    ///   bit  5      heavy attack        bit  9   skill 2
    ///   bit  6      jump                bit 10   skill 3
    ///   bit  7      shield              bit 11   ultimate
    ///   bit 12      dash
    ///   bit 13..14  gesture id (ChibiClash.Gesture: 0 None, 1 CrossArms, 2 PunchForward, 3 RaiseHands)
    ///   bit 15      special (B), protocol 3 / rules v18 (was the cosmetic PoseVerified hint in protocol 2)
    /// Since RulesVersion 15 the simulation reads the gesture id (bits 13..14) as a held input: ult window, CrossArms
    /// block, poses. Since RulesVersion 18 bit 15 is the Special button, an ordinary gameplay button.
    ///
    /// The struct holds *state* (what is held), never events. Press edges are derived inside the simulation
    /// from the previous frame's value, so nothing extra travels over the network and rollback stays exact.
    /// </summary>
    public struct ChibiInputPacket : IEquatable<ChibiInputPacket>
    {
        /// <summary>Number of input bits sent per frame on the wire.</summary>
        public const int WireBits = 16;
        /// <summary>Mask of all bits that travel on the wire (all 16).</summary>
        public const ushort WireMask = 0xFFFF;
        /// <summary>Mask of the direction nibble (bits 0..3).</summary>
        public const ushort DirMask = 0xF;
        /// <summary>Bit position of the 2-bit gesture id.</summary>
        public const int GestureShift = 13;
        /// <summary>Mask of the gesture id bits (13..14).</summary>
        public const ushort GestureMask = 0x3 << 13;

        /// <summary>
        /// Rules v25 reserved sentinel: "this slot dropped out" (direction nibble 15 with no buttons, a value no input
        /// source produces and <see cref="Sanitized"/> / <see cref="Read"/> turn into neutral, so it never arrives from the
        /// wire). Fed to <c>ChibiSimulation.Step(state, inputs)</c> outside 1v1 it eliminates that fighter on that frame.
        /// </summary>
        public const ushort DroppedRaw = 0x000F;
        /// <summary>The <see cref="DroppedRaw"/> sentinel packet.</summary>
        public static ChibiInputPacket Dropped => new ChibiInputPacket { Raw = DroppedRaw };

        public ushort Raw;

        // x/y components of ChibiDir indices 0..8 (y up).
        static readonly sbyte[] DirX = { 0, 0, 1, 1, 1, 0, -1, -1, -1 };
        static readonly sbyte[] DirY = { 0, 1, 1, 0, -1, -1, -1, 0, 1 };
        // (y+1)*3 + (x+1) -> ChibiDir index
        static readonly byte[] AxesToDir = { 6, 5, 4, 7, 0, 3, 8, 1, 2 };

        public ChibiDir Direction => (ChibiDir)DirIndex;
        int DirIndex { get { int d = Raw & DirMask; return d > 8 ? 0 : d; } }
        public int MoveX => DirX[DirIndex];
        public int MoveY => DirY[DirIndex];
        public bool Has(ChibiButtons b) => (Raw & (ushort)b) != 0;

        /// <summary>Camera gesture id held this frame (bits 13..14). Get/set touches only those bits.</summary>
        public Gesture Gesture
        {
            get => (Gesture)((Raw & GestureMask) >> GestureShift);
            set => Raw = (ushort)((Raw & ~GestureMask) | (((int)value << GestureShift) & GestureMask));
        }

        /// <summary>Pack a direction, held buttons and an optional gesture id into one frame of input.</summary>
        public static ChibiInputPacket Make(ChibiDir dir, ChibiButtons buttons, Gesture gesture = Gesture.None)
        {
            var p = new ChibiInputPacket { Raw = (ushort)(((ushort)dir & DirMask) | ((ushort)buttons & ~GestureMask)) };
            p.Gesture = gesture;
            return p;
        }

        /// <summary>Quantise a stick/dpad (each axis already reduced to -1, 0, +1) into the 8-way direction.</summary>
        public static ChibiDir DirFromAxes(int x, int y)
        {
            x = x < 0 ? -1 : (x > 0 ? 1 : 0);
            y = y < 0 ? -1 : (y > 0 ? 1 : 0);
            return (ChibiDir)AxesToDir[(y + 1) * 3 + (x + 1)];
        }

        /// <summary>Drop bits outside <see cref="WireMask"/> and illegal direction values (never trust the wire).
        /// Every button bit is kept, including Special (bit 15, protocol 3): unlike the retired PoseVerified hint it is
        /// independent of the gesture bits, so it is never cleared here.</summary>
        public ChibiInputPacket Sanitized()
        {
            ushort r = (ushort)(Raw & WireMask);
            if ((r & DirMask) > 8) r = (ushort)(r & ~DirMask);
            return new ChibiInputPacket { Raw = r };
        }

        public void Write(BitBuffer b) => b.Write((uint)(Raw & WireMask), WireBits);

        public static ChibiInputPacket Read(BitBuffer b)
        {
            return new ChibiInputPacket { Raw = (ushort)b.Read(WireBits) }.Sanitized();
        }

        public bool Equals(ChibiInputPacket o) => Raw == o.Raw;
        public override bool Equals(object obj) => obj is ChibiInputPacket p && Equals(p);
        public override int GetHashCode() => Raw;
        public override string ToString() => Direction + (Raw >> 4 != 0 ? "+0x" + (Raw >> 4).ToString("X") : "");
    }

    /// <summary>
    /// One UDP datagram. Every datagram carries the oldest unacknowledged local inputs again (up to 16 frames,
    /// delta coded), so a lost packet is repaired by the very next one with no retransmit timer.
    /// </summary>
    public sealed class ChibiNetMessage
    {
        /// <summary>Inputs per datagram (4-bit count on the wire since protocol 2; was 8 / 3 bits).</summary>
        public const int MaxInputs = 16;

        public int LastFrame;      // frame number of Inputs[Count-1]
        public int AckFrame;       // newest remote frame the sender holds contiguously (-1 = none)
        public int Advantage;      // sender's frame advantage estimate, clamped to -8..7 on the wire
        public int Count;          // 1..MaxInputs
        public readonly ChibiInputPacket[] Inputs = new ChibiInputPacket[MaxInputs]; // Inputs[0] = LastFrame-Count+1
        public bool HasChecksum;
        public int ChecksumFrame;
        public uint Checksum;
        /// <summary>Protocol 6: roster slot of the sender (0..3). A link accepts only its own bound slot (security BLOCKER 1).</summary>
        public int Sender;
        /// <summary>
        /// Protocol 6 drop vote: bit s set = the sender considers slot s gone and votes to drop it. Always 0 in 1v1.
        /// A vote never changes once cast, and it is repeated in every datagram, so it needs no retransmit timer.
        /// </summary>
        public int VoteMask;
        /// <summary>Per voted slot: the last frame of that slot the sender held contiguously when it voted (-1..).</summary>
        public readonly int[] VoteFrame = new int[4];
        /// <summary>
        /// Protocol 6, team play only: bit k set = <see cref="Hold"/>[k] is "the sender holds every input of slot k up to
        /// this frame". Survivors confirm a frame only once every live peer reports holding every live slot that far, so an
        /// agreed drop can never rewrite a frame someone already confirmed (review B1/N2). Always 0 in 1v1.
        /// </summary>
        public int HoldMask;
        /// <summary>Per slot in <see cref="HoldMask"/>: newest frame the sender holds contiguously (-1..).</summary>
        public readonly int[] Hold = new int[4];
        /// <summary>
        /// Protocol 6 drop agreement (second review, BLOCKER 1b): bit t set = <see cref="AgreeFrame"/>[t] is the drop frame
        /// the sender computed for slot t once its vote was unanimous. A drop is applied only when every survivor reports
        /// the same frame; any difference (an equivocating voter) is a no contest for everyone. Repeated like votes.
        /// </summary>
        public int AgreeMask;
        /// <summary>Per slot in <see cref="AgreeMask"/>: the agreed drop frame.</summary>
        public readonly int[] AgreeFrame = new int[4];
        /// <summary>
        /// Protocol 6 (final security review, gate-stall framing): bit s set = the sender is stalled waiting on slot s (its
        /// inputs or its holds hold the sender back; a pending drop target appears because its frozen inputs hold the sender
        /// back, not as a separate rule). Receivers do not count silence against
        /// a peer that says it is waiting on someone ELSE (it is plainly alive and blocked), so an honest peer stalled by a
        /// cheater is never timed out by the bystanders. Always 0 in 1v1.
        /// </summary>
        public int WaitMask;
    }

    /// <summary>
    /// Wire format (bits, LSB first):
    ///   8  magic 0xC7  2 sender slot (protocol 6)  24 lastFrame
    ///   24 ackFrame + 1                      4  advantage + 8
    ///   4  count - 1
    ///   n  inputs: first = 16 raw bits; each next = 1 bit "same as previous" (1) or 0 + 16 raw bits
    ///   1  hasChecksum; if set: 24 frame, 32 checksum
    ///   1  hasVotes (protocol 6); if set: 4 vote mask, then per set bit (ascending slot) 24 bits lastFrame + 1
    ///   1  hasHolds (protocol 6); if set: 4 hold mask, then per set bit (ascending slot) 24 bits heldFrame + 1
    ///   1  hasAgree (protocol 6); if set: 4 agree mask, then per set bit (ascending slot) 24 bits dropFrame
    ///   1  hasWait (protocol 6); if set: 4 bits wait mask
    /// Typical size when held inputs repeat: 66 + 16 + 7 + 1 + 1 = 91 bits = 12 bytes.
    /// Worst case (16 inputs, no repeats, checksum, four votes): header 8+2+24+24+4+4 = 66, first input 16,
    /// 15 x (1+16) = 255, checksum 1+24+32 = 57, votes 1+4+4x24 = 101 -> 495 bits = 62 bytes. See <see cref="MaxPacketBytes"/>.
    /// Frame counters are 24 bit (about 77 hours at 60 Hz).
    /// The layout is versioned by <see cref="ProtocolVersion"/>, which the match handshake compares before any
    /// 0xC7 datagram is exchanged (this datagram carries no version byte of its own, to stay small).
    /// </summary>
    public static class ChibiPacketCodec
    {
        public const byte Magic = 0xC7;
        /// <summary>
        /// Wire protocol version, checked in the handshake and in the Lobby filter. Bump on ANY change to this layout,
        /// the control messages or the rollback rules. 1: 3-bit count / 8 inputs. 2: 4-bit count / 16 inputs, oldest-
        /// unacked resend window, control channel 0xC9. 3: input bit 15 is the Special (B) button instead of the
        /// PoseVerified hint (CH-3); same layout, new meaning, so old builds must refuse new ones. 4: the handshake Start
        /// message carries a 1-bit HazardsOff after the stage, and the stage range is 0..7 (rules v20 / v21, ST-1 / ST-5).
        /// 5: same layout, new rollback/result rules (review 2026-11-16): DisconnectTicks is capped at 600, a peer that
        /// stops advancing its input times out, Leave(MatchComplete) no longer gives a no contest, and a peer that never
        /// sent a real input frame gives a no contest, a match stalled past the stall limit ends as a no contest and says
        /// so with the new Leave reason Stalled (3). Old and new builds would score the same drop differently.
        /// 6: team play (2v2 / FFA full mesh): the input datagram carries a 2-bit sender slot and an optional drop-vote
        /// block; the handshake Start carries a full roster (format, count, per-slot class / passive / team / player
        /// hash) and guest-to-guest links open with PeerHello.
        /// </summary>
        public const int ProtocolVersion = 6;
        /// <summary>
        /// Capacity of every send/receive BitBuffer. Worst case (protocol 6, team play): 395 bits of
        /// inputs and checksum + 77 for three votes + 77 for three holds + 77 for three agreed
        /// drop frames + 5 for the wait mask = 631 bits = 79 bytes, plus the 8-byte link tag = 87 bytes; 96 leaves headroom, and stays far below Relay's 1400-byte limit.
        /// Received datagrams larger than this are rejected (BitBuffer.LoadFrom flags Overrun, TryRead returns false).
        /// </summary>
        public const int MaxPacketBytes = 96;
        const int CountBits = 4;
        const int FrameBits = 24;
        const uint FrameMask = (1u << FrameBits) - 1u;

        /// <summary>Serialise <paramref name="m"/> into <paramref name="b"/>.</summary>
        /// <exception cref="InvalidOperationException">The message did not fit the buffer (never sent truncated).</exception>
        public static void Write(BitBuffer b, ChibiNetMessage m)
        {
            b.Write(Magic, 8);
            b.Write((uint)m.Sender & 3u, 2);
            b.Write((uint)m.LastFrame & FrameMask, FrameBits);
            b.Write((uint)(m.AckFrame + 1) & FrameMask, FrameBits);
            int adv = m.Advantage < -8 ? -8 : (m.Advantage > 7 ? 7 : m.Advantage);
            b.Write((uint)(adv + 8), 4);
            if (m.Count < 1 || m.Count > ChibiNetMessage.MaxInputs)
                throw new InvalidOperationException("ChibiPacketCodec.Write: Count " + m.Count + " outside 1.." + ChibiNetMessage.MaxInputs);
            b.Write((uint)(m.Count - 1), CountBits);

            ushort prev = 0;
            for (int i = 0; i < m.Count; i++)
            {
                var cur = m.Inputs[i];
                if (i == 0) cur.Write(b);
                else if (cur.Raw == prev) b.WriteBool(true);
                else { b.WriteBool(false); cur.Write(b); }
                prev = cur.Raw;
            }

            b.WriteBool(m.HasChecksum);
            if (m.HasChecksum)
            {
                b.Write((uint)m.ChecksumFrame & FrameMask, FrameBits);
                b.Write(m.Checksum, 32);
            }

            int votes = m.VoteMask & 0xF;
            b.WriteBool(votes != 0);
            if (votes != 0)
            {
                b.Write((uint)votes, 4);
                for (int s = 0; s < 4; s++)
                    if ((votes & (1 << s)) != 0) b.Write((uint)(m.VoteFrame[s] + 1) & FrameMask, FrameBits);
            }

            int holds = m.HoldMask & 0xF;
            b.WriteBool(holds != 0);
            if (holds != 0)
            {
                b.Write((uint)holds, 4);
                for (int s = 0; s < 4; s++)
                    if ((holds & (1 << s)) != 0) b.Write((uint)(m.Hold[s] + 1) & FrameMask, FrameBits);
            }

            int wait = m.WaitMask & 0xF;
            b.WriteBool(wait != 0);
            if (wait != 0) b.Write((uint)wait, 4);

            int agree = m.AgreeMask & 0xF;
            b.WriteBool(agree != 0);
            if (agree != 0)
            {
                b.Write((uint)agree, 4);
                for (int s = 0; s < 4; s++)
                    if ((agree & (1 << s)) != 0) b.Write((uint)m.AgreeFrame[s] & FrameMask, FrameBits);
            }

            if (b.Overrun)
                throw new InvalidOperationException(
                    "ChibiPacketCodec.Write: message overran " + b.Bytes.Length + "-byte packet (Count=" + m.Count +
                    ", HasChecksum=" + m.HasChecksum + "); refusing to send a truncated datagram.");
        }

        /// <summary>Returns false for anything that is not a well-formed message (wrong magic, truncated).</summary>
        public static bool TryRead(BitBuffer b, ChibiNetMessage m)
        {
            if (b.Read(8) != Magic) return false;
            m.Sender = (int)b.Read(2);
            m.LastFrame = (int)b.Read(FrameBits);
            m.AckFrame = (int)b.Read(FrameBits) - 1;
            m.Advantage = (int)b.Read(4) - 8;
            m.Count = (int)b.Read(CountBits) + 1;

            ushort prev = 0;
            for (int i = 0; i < m.Count; i++)
            {
                ChibiInputPacket cur;
                if (i == 0) cur = ChibiInputPacket.Read(b);
                else if (b.ReadBool()) cur = new ChibiInputPacket { Raw = prev };
                else cur = ChibiInputPacket.Read(b);
                m.Inputs[i] = cur;
                prev = cur.Raw;
            }

            m.HasChecksum = b.ReadBool();
            if (m.HasChecksum)
            {
                m.ChecksumFrame = (int)b.Read(FrameBits);
                m.Checksum = b.Read(32);
            }
            else { m.ChecksumFrame = 0; m.Checksum = 0; }

            m.VoteMask = 0;
            if (b.ReadBool())
            {
                int votes = (int)b.Read(4);
                for (int s = 0; s < 4; s++)
                    m.VoteFrame[s] = (votes & (1 << s)) != 0 ? (int)b.Read(FrameBits) - 1 : -1;
                m.VoteMask = votes;
            }

            m.HoldMask = 0;
            if (b.ReadBool())
            {
                int holds = (int)b.Read(4);
                for (int s = 0; s < 4; s++)
                    m.Hold[s] = (holds & (1 << s)) != 0 ? (int)b.Read(FrameBits) - 1 : -1;
                m.HoldMask = holds;
            }

            m.WaitMask = b.ReadBool() ? (int)b.Read(4) : 0;

            m.AgreeMask = 0;
            if (b.ReadBool())
            {
                int agree = (int)b.Read(4);
                for (int s = 0; s < 4; s++)
                    m.AgreeFrame[s] = (agree & (1 << s)) != 0 ? (int)b.Read(FrameBits) : -1;
                m.AgreeMask = agree;
            }

            return !b.Overrun && m.LastFrame - m.Count + 1 >= 0;
        }
    }
}
