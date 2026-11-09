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
        /// Bit 15: set by the input side when the gesture (bits 13-14) came from a live, telemetry-validated
        /// camera pose. A hint for the receiver, not proof: the peer can set it freely.
        /// </summary>
        PoseVerified = 1 << 15,
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
    ///   bit 15      pose verified (hint from the camera input side, not proof)
    /// Bits 12..15 travel on the wire but the simulation ignores them for now.
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
        /// Dash, gesture and PoseVerified bits are kept.</summary>
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
    }

    /// <summary>
    /// Wire format (bits, LSB first):
    ///   8  magic 0xC7                        24 lastFrame
    ///   24 ackFrame + 1                      4  advantage + 8
    ///   4  count - 1
    ///   n  inputs: first = 16 raw bits; each next = 1 bit "same as previous" (1) or 0 + 16 raw bits
    ///   1  hasChecksum; if set: 24 frame, 32 checksum
    /// Typical size when held inputs repeat: 64 + 16 + 7 + 1 = 88 bits = 11 bytes.
    /// Worst case (16 inputs, no repeats, checksum): header 8+24+24+4+4 = 64, first input 16,
    /// 15 x (1+16) = 255, checksum 1+24+32 = 57 -> 392 bits = 49 bytes. See <see cref="MaxPacketBytes"/>.
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
        /// unacked resend window, control channel 0xC9.
        /// </summary>
        public const int ProtocolVersion = 2;
        /// <summary>
        /// Capacity of every send/receive BitBuffer. Worst case is 392 bits = 49 bytes; 64 leaves headroom for a later
        /// field without another wire break, and stays far below Relay's 1400-byte limit.
        /// Received datagrams larger than this are rejected (BitBuffer.LoadFrom flags Overrun, TryRead returns false).
        /// </summary>
        public const int MaxPacketBytes = 64;
        const int CountBits = 4;
        const int FrameBits = 24;
        const uint FrameMask = (1u << FrameBits) - 1u;

        /// <summary>Serialise <paramref name="m"/> into <paramref name="b"/>.</summary>
        /// <exception cref="InvalidOperationException">The message did not fit the buffer (never sent truncated).</exception>
        public static void Write(BitBuffer b, ChibiNetMessage m)
        {
            b.Write(Magic, 8);
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

            if (b.Overrun)
                throw new InvalidOperationException(
                    "ChibiPacketCodec.Write: message overran " + b.Bytes.Length + "-byte packet (Count=" + m.Count +
                    ", HasChecksum=" + m.HasChecksum + "); refusing to send a truncated datagram.");
        }

        /// <summary>Returns false for anything that is not a well-formed message (wrong magic, truncated).</summary>
        public static bool TryRead(BitBuffer b, ChibiNetMessage m)
        {
            if (b.Read(8) != Magic) return false;
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

            return !b.Overrun && m.LastFrame - m.Count + 1 >= 0;
        }
    }
}
