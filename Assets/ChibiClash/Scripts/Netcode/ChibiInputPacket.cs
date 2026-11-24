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
    /// One player's run of inputs inside a <see cref="ChibiNetMessage"/> (protocol 6): the player slot, the newest frame,
    /// up to 16 inputs (oldest first) and that player's latest state checksum as known by the sender.
    /// </summary>
    public sealed class ChibiInputBlock
    {
        /// <summary>Player slot (0..3) whose inputs these are.</summary>
        public int Slot;
        /// <summary>Frame number of Inputs[Count-1].</summary>
        public int LastFrame;
        /// <summary>1..<see cref="ChibiNetMessage.MaxInputs"/>.</summary>
        public int Count;
        /// <summary>Inputs[0] = LastFrame - Count + 1.</summary>
        public readonly ChibiInputPacket[] Inputs = new ChibiInputPacket[ChibiNetMessage.MaxInputs];
        /// <summary>True when the block carries the player's latest checksum.</summary>
        public bool HasChecksum;
        /// <summary>Frame of <see cref="Checksum"/> (a multiple of the checksum interval).</summary>
        public int ChecksumFrame;
        /// <summary>State checksum computed by player <see cref="Slot"/> at <see cref="ChecksumFrame"/>.</summary>
        public uint Checksum;
    }

    /// <summary>
    /// One rollback datagram (protocol 6). Every datagram carries the oldest unacknowledged inputs again (up to 16 frames
    /// per player, delta coded), so a lost packet is repaired by the very next one with no retransmit timer.
    ///
    /// Star topology (four-player-design.md 2.1): a guest sends ONE block (its own inputs) to the host; the host sends each
    /// guest one block per OTHER player (its own and the forwarded ones). Block 0 is the sender's own stream; the
    /// <see cref="LastFrame"/> / <see cref="Count"/> / <see cref="Inputs"/> / checksum properties alias block 0, so a 1v1
    /// message reads exactly like the protocol 5 one.
    /// </summary>
    public sealed class ChibiNetMessage
    {
        /// <summary>Inputs per block (4-bit count on the wire since protocol 2; was 8 / 3 bits).</summary>
        public const int MaxInputs = 16;
        /// <summary>Blocks per datagram (one per player).</summary>
        public const int MaxBlocks = ChibiSimState.MaxFighters;

        /// <summary>Player blocks; only the first <see cref="BlockCount"/> are valid.</summary>
        public readonly ChibiInputBlock[] Blocks = { new ChibiInputBlock(), new ChibiInputBlock(), new ChibiInputBlock(), new ChibiInputBlock() };
        /// <summary>1..<see cref="MaxBlocks"/>.</summary>
        public int BlockCount = 1;
        /// <summary>Newest frame of the RECEIVER's own stream the sender holds contiguously (-1 = none).</summary>
        public int AckFrame;
        /// <summary>Sender's frame advantage estimate, clamped to -8..7 on the wire.</summary>
        public int Advantage;
        /// <summary>Guest to host (3-4 players): bit s set = <see cref="ExtraAck"/>[s] is the sender's contiguous frame of
        /// forwarded stream s (a stream that is neither the sender's nor the receiver's).</summary>
        public int ExtraAckMask;
        /// <summary>See <see cref="ExtraAckMask"/>; indexed by slot.</summary>
        public readonly int[] ExtraAck = new int[MaxBlocks];
        /// <summary>Host only: bit s set = player s left the match from frame <see cref="LeaveFrame"/>[s] on (presentMask).</summary>
        public int LeaveMask;
        /// <summary>See <see cref="LeaveMask"/>; indexed by slot.</summary>
        public readonly int[] LeaveFrame = new int[MaxBlocks];

        /// <summary>Block 0: player slot.</summary>
        public int Slot { get => Blocks[0].Slot; set => Blocks[0].Slot = value; }
        /// <summary>Block 0: frame number of Inputs[Count-1].</summary>
        public int LastFrame { get => Blocks[0].LastFrame; set => Blocks[0].LastFrame = value; }
        /// <summary>Block 0: 1..MaxInputs.</summary>
        public int Count { get => Blocks[0].Count; set => Blocks[0].Count = value; }
        /// <summary>Block 0: inputs, Inputs[0] = LastFrame - Count + 1.</summary>
        public ChibiInputPacket[] Inputs => Blocks[0].Inputs;
        /// <summary>Block 0: carries a checksum.</summary>
        public bool HasChecksum { get => Blocks[0].HasChecksum; set => Blocks[0].HasChecksum = value; }
        /// <summary>Block 0: checksum frame.</summary>
        public int ChecksumFrame { get => Blocks[0].ChecksumFrame; set => Blocks[0].ChecksumFrame = value; }
        /// <summary>Block 0: checksum.</summary>
        public uint Checksum { get => Blocks[0].Checksum; set => Blocks[0].Checksum = value; }
    }

    /// <summary>
    /// Wire format, protocol 6 (bits, LSB first):
    ///   8  magic 0xC7                        2  blockCount - 1
    ///   24 ackFrame + 1                      4  advantage + 8
    ///   per block:
    ///     2  slot   24 lastFrame   4 count - 1
    ///     n  inputs: first = 16 raw bits; each next = 1 bit "same as previous" (1) or 0 + 16 raw bits
    ///     1  hasChecksum; if set: 24 frame, 32 checksum
    ///   4  extraAckMask; per set bit (slot order): 24 ack + 1
    ///   4  leaveMask;    per set bit (slot order): 24 leave frame
    /// Typical 1v1 size when held inputs repeat: 38 + 30 + 16 + 15 + 1 + 8 = 108 bits = 14 bytes.
    /// Worst case: 38 + 4 x (30 + 16 + 255 + 57) + 100 + 100 = 1670 bits = 209 bytes. See <see cref="MaxPacketBytes"/>.
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
        /// 6: 2-4 players over a star (four-player-design.md 2.3): input datagrams carry player blocks with a slot, extra
        /// acks and the host's leave frames; Start carries player count, mode, teams, 4 seats and the receiver's slot; new
        /// Roster control message with every player's name.
        /// </summary>
        public const int ProtocolVersion = 6;
        /// <summary>
        /// Capacity of every send/receive BitBuffer. Worst case is 1670 bits = 209 bytes (4 full blocks); 216 stays under
        /// the mux's <see cref="ChibiChannelMux.MaxDatagram"/> (256) and far below Relay's 1400-byte limit.
        /// Received datagrams larger than this are rejected (BitBuffer.LoadFrom flags Overrun, TryRead returns false).
        /// </summary>
        public const int MaxPacketBytes = 216;
        const int CountBits = 4;
        const int FrameBits = 24;
        const int SlotBits = 2;
        const uint FrameMask = (1u << FrameBits) - 1u;

        /// <summary>Serialise <paramref name="m"/> into <paramref name="b"/>.</summary>
        /// <exception cref="InvalidOperationException">The message did not fit the buffer or a field is out of range (never sent truncated).</exception>
        public static void Write(BitBuffer b, ChibiNetMessage m)
        {
            if (m.BlockCount < 1 || m.BlockCount > ChibiNetMessage.MaxBlocks)
                throw new InvalidOperationException("ChibiPacketCodec.Write: BlockCount " + m.BlockCount + " outside 1.." + ChibiNetMessage.MaxBlocks);
            b.Write(Magic, 8);
            b.Write((uint)(m.BlockCount - 1), SlotBits);
            b.Write((uint)(m.AckFrame + 1) & FrameMask, FrameBits);
            int adv = m.Advantage < -8 ? -8 : (m.Advantage > 7 ? 7 : m.Advantage);
            b.Write((uint)(adv + 8), 4);

            for (int k = 0; k < m.BlockCount; k++)
            {
                var blk = m.Blocks[k];
                if (blk.Count < 1 || blk.Count > ChibiNetMessage.MaxInputs)
                    throw new InvalidOperationException("ChibiPacketCodec.Write: Count " + blk.Count + " outside 1.." + ChibiNetMessage.MaxInputs);
                if (blk.Slot < 0 || blk.Slot >= ChibiNetMessage.MaxBlocks)
                    throw new InvalidOperationException("ChibiPacketCodec.Write: Slot " + blk.Slot + " outside 0.." + (ChibiNetMessage.MaxBlocks - 1));
                b.Write((uint)blk.Slot, SlotBits);
                b.Write((uint)blk.LastFrame & FrameMask, FrameBits);
                b.Write((uint)(blk.Count - 1), CountBits);
                ushort prev = 0;
                for (int i = 0; i < blk.Count; i++)
                {
                    var cur = blk.Inputs[i];
                    if (i == 0) cur.Write(b);
                    else if (cur.Raw == prev) b.WriteBool(true);
                    else { b.WriteBool(false); cur.Write(b); }
                    prev = cur.Raw;
                }
                b.WriteBool(blk.HasChecksum);
                if (blk.HasChecksum)
                {
                    b.Write((uint)blk.ChecksumFrame & FrameMask, FrameBits);
                    b.Write(blk.Checksum, 32);
                }
            }

            int ackMask = m.ExtraAckMask & 0xF;
            b.Write((uint)ackMask, 4);
            for (int s = 0; s < ChibiNetMessage.MaxBlocks; s++)
                if ((ackMask & (1 << s)) != 0) b.Write((uint)(m.ExtraAck[s] + 1) & FrameMask, FrameBits);
            int leaveMask = m.LeaveMask & 0xF;
            b.Write((uint)leaveMask, 4);
            for (int s = 0; s < ChibiNetMessage.MaxBlocks; s++)
                if ((leaveMask & (1 << s)) != 0) b.Write((uint)m.LeaveFrame[s] & FrameMask, FrameBits);

            if (b.Overrun)
                throw new InvalidOperationException(
                    "ChibiPacketCodec.Write: message overran " + b.Bytes.Length + "-byte packet (BlockCount=" + m.BlockCount +
                    ", Count=" + m.Count + "); refusing to send a truncated datagram.");
        }

        /// <summary>
        /// Returns false for anything that is not a well-formed message (wrong magic, truncated, two blocks for one slot,
        /// a block starting before frame 0). Never throws and never allocates: the message's arrays are reused.
        /// </summary>
        public static bool TryRead(BitBuffer b, ChibiNetMessage m)
        {
            if (b.Read(8) != Magic) return false;
            m.BlockCount = (int)b.Read(SlotBits) + 1;
            m.AckFrame = (int)b.Read(FrameBits) - 1;
            m.Advantage = (int)b.Read(4) - 8;

            int seen = 0;
            for (int k = 0; k < m.BlockCount; k++)
            {
                var blk = m.Blocks[k];
                blk.Slot = (int)b.Read(SlotBits);
                blk.LastFrame = (int)b.Read(FrameBits);
                blk.Count = (int)b.Read(CountBits) + 1;
                ushort prev = 0;
                for (int i = 0; i < blk.Count; i++)
                {
                    ChibiInputPacket cur;
                    if (i == 0) cur = ChibiInputPacket.Read(b);
                    else if (b.ReadBool()) cur = new ChibiInputPacket { Raw = prev };
                    else cur = ChibiInputPacket.Read(b);
                    blk.Inputs[i] = cur;
                    prev = cur.Raw;
                }
                blk.HasChecksum = b.ReadBool();
                if (blk.HasChecksum)
                {
                    blk.ChecksumFrame = (int)b.Read(FrameBits);
                    blk.Checksum = b.Read(32);
                }
                else { blk.ChecksumFrame = 0; blk.Checksum = 0; }
                if (b.Overrun) return false;
                if ((seen & (1 << blk.Slot)) != 0) return false;           // one block per player
                seen |= 1 << blk.Slot;
                if (blk.LastFrame - blk.Count + 1 < 0) return false;
            }

            m.ExtraAckMask = (int)b.Read(4);
            for (int s = 0; s < ChibiNetMessage.MaxBlocks; s++)
                m.ExtraAck[s] = (m.ExtraAckMask & (1 << s)) != 0 ? (int)b.Read(FrameBits) - 1 : -1;
            m.LeaveMask = (int)b.Read(4);
            for (int s = 0; s < ChibiNetMessage.MaxBlocks; s++)
                m.LeaveFrame[s] = (m.LeaveMask & (1 << s)) != 0 ? (int)b.Read(FrameBits) : 0;

            return !b.Overrun;
        }
    }
}
