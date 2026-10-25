using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// LSB-first bit packer over a fixed byte[] (no allocation after construction).
    ///
    /// Layout: bit N of the stream lives in byte (N >> 3) at bit position (N &amp; 7). A value written with
    /// Write(v, bits) occupies <c>bits</c> consecutive stream bits, least significant bit first, so a
    /// reader calling Read(bits) with the same widths gets the same value back, independent of host endianness.
    ///
    /// Safety: writers and readers never throw on bad data. A write past capacity or a read past the end
    /// sets <see cref="Overrun"/> and returns 0, so a malformed UDP datagram can be rejected by checking one flag.
    /// </summary>
    public sealed class BitBuffer
    {
        readonly byte[] _buf;
        int _bitPos;
        int _bitLimit;

        public bool Overrun { get; private set; }
        public byte[] Bytes => _buf;
        public int BitPosition => _bitPos;
        public int ByteLength => (_bitPos + 7) >> 3;

        public BitBuffer(int capacityBytes)
        {
            _buf = new byte[capacityBytes];
            _bitLimit = capacityBytes << 3;
        }

        /// <summary>Zero the buffer and rewind for writing (zeroing matters: Write ORs into bytes).</summary>
        public void Clear()
        {
            Array.Clear(_buf, 0, _buf.Length);
            _bitPos = 0;
            _bitLimit = _buf.Length << 3;
            Overrun = false;
        }

        /// <summary>Load a received datagram for reading. Oversized datagrams are flagged as Overrun.</summary>
        public void LoadFrom(byte[] src, int length)
        {
            Clear();
            int n = Math.Min(length, _buf.Length);
            Buffer.BlockCopy(src, 0, _buf, 0, n);
            _bitLimit = n << 3;
            if (length > _buf.Length) Overrun = true;
        }

        /// <summary>Switch from writing to reading the bytes just written (round-trip tests).</summary>
        public void BeginRead()
        {
            _bitLimit = ByteLength << 3;
            _bitPos = 0;
        }

        public void Write(uint value, int bits)
        {
            if (bits < 1 || bits > 32) throw new ArgumentOutOfRangeException(nameof(bits));
            if (_bitPos + bits > _bitLimit) { Overrun = true; return; }
            if (bits < 32) value &= (1u << bits) - 1u;

            while (bits > 0)
            {
                int offset = _bitPos & 7;            // first free bit inside the current byte
                int room = 8 - offset;               // free bits left in the current byte
                int take = bits < room ? bits : room;
                uint chunk = value & ((1u << take) - 1u);
                _buf[_bitPos >> 3] |= (byte)(chunk << offset);
                value >>= take;
                bits -= take;
                _bitPos += take;
            }
        }

        public uint Read(int bits)
        {
            if (bits < 1 || bits > 32) throw new ArgumentOutOfRangeException(nameof(bits));
            if (_bitPos + bits > _bitLimit) { Overrun = true; return 0; }

            uint result = 0;
            int shift = 0;
            while (bits > 0)
            {
                int offset = _bitPos & 7;
                int room = 8 - offset;
                int take = bits < room ? bits : room;
                uint chunk = ((uint)_buf[_bitPos >> 3] >> offset) & ((1u << take) - 1u);
                result |= chunk << shift;
                shift += take;
                bits -= take;
                _bitPos += take;
            }
            return result;
        }

        public void WriteBool(bool v) => Write(v ? 1u : 0u, 1);
        public bool ReadBool() => Read(1) != 0;

        /// <summary>Two's-complement signed value in <paramref name="bits"/> bits.</summary>
        public void WriteSigned(int value, int bits) => Write((uint)value, bits);

        public int ReadSigned(int bits)
        {
            uint u = Read(bits);
            if (bits < 32 && (u & (1u << (bits - 1))) != 0) u |= ~((1u << bits) - 1u); // sign-extend
            return (int)u;
        }
    }
}
