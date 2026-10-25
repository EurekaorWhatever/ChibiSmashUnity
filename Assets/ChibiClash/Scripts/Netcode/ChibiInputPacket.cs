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
    }

    /// <summary>
    /// One player's input for one simulation frame, packed into 12 bits:
    ///
    ///   bit  0..3   direction (ChibiDir, 0..8; 9..15 are illegal and sanitised to neutral)
    ///   bit  4      light attack        bit  8   skill 1
    ///   bit  5      heavy attack        bit  9   skill 2
    ///   bit  6      jump                bit 10   skill 3
    ///   bit  7      shield              bit 11   ultimate
    ///   bit 12..15  reserved (future extension, e.g. a camera gesture id). They are NOT sent today:
    ///               raise <see cref="WireBits"/> and the codec widens automatically.
    ///
    /// The struct holds *state* (what is held), never events. Press edges are derived inside the simulation
    /// from the previous frame's value, so nothing extra travels over the network and rollback stays exact.
    /// </summary>
    public struct ChibiInputPacket : IEquatable<ChibiInputPacket>
    {
        public const int WireBits = 12;
        public const ushort WireMask = (1 << WireBits) - 1;
        public const ushort DirMask = 0xF;

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

        public static ChibiInputPacket Make(ChibiDir dir, ChibiButtons buttons)
        {
            return new ChibiInputPacket { Raw = (ushort)(((ushort)dir & DirMask) | (ushort)buttons) };
        }

        /// <summary>Quantise a stick/dpad (each axis already reduced to -1, 0, +1) into the 8-way direction.</summary>
        public static ChibiDir DirFromAxes(int x, int y)
        {
            x = x < 0 ? -1 : (x > 0 ? 1 : 0);
            y = y < 0 ? -1 : (y > 0 ? 1 : 0);
            return (ChibiDir)AxesToDir[(y + 1) * 3 + (x + 1)];
        }

        /// <summary>Drop reserved bits and illegal direction values (never trust the wire).</summary>
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
    /// One UDP datagram. Every datagram carries the newest local inputs again (up to 8 frames, delta coded),
    /// so a lost packet is repaired by the very next one with no retransmit timer.
    /// </summary>
    public sealed class ChibiNetMessage
    {
        public const int MaxInputs = 8;

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
    ///   3  count - 1
    ///   n  inputs: first = 12 raw bits; each next = 1 bit "same as previous" (1) or 0 + 12 raw bits
    ///   1  hasChecksum; if set: 24 frame, 32 checksum
    /// Typical size when held inputs repeat: 63 + 12 + 7 = 82 bits = 11 bytes. Worst case 28 bytes.
    /// Frame counters are 24 bit (about 77 hours at 60 Hz).
    /// </summary>
    public static class ChibiPacketCodec
    {
        public const byte Magic = 0xC7;
        public const int MaxPacketBytes = 32;
        const int FrameBits = 24;
        const uint FrameMask = (1u << FrameBits) - 1u;

        public static void Write(BitBuffer b, ChibiNetMessage m)
        {
            b.Write(Magic, 8);
            b.Write((uint)m.LastFrame & FrameMask, FrameBits);
            b.Write((uint)(m.AckFrame + 1) & FrameMask, FrameBits);
            int adv = m.Advantage < -8 ? -8 : (m.Advantage > 7 ? 7 : m.Advantage);
            b.Write((uint)(adv + 8), 4);
            b.Write((uint)(m.Count - 1), 3);

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
        }

        /// <summary>Returns false for anything that is not a well-formed message (wrong magic, truncated).</summary>
        public static bool TryRead(BitBuffer b, ChibiNetMessage m)
        {
            if (b.Read(8) != Magic) return false;
            m.LastFrame = (int)b.Read(FrameBits);
            m.AckFrame = (int)b.Read(FrameBits) - 1;
            m.Advantage = (int)b.Read(4) - 8;
            m.Count = (int)b.Read(3) + 1;

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
