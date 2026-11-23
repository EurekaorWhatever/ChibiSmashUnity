using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// HMAC-SHA256 (RFC 2104 / FIPS 180-4) with a fixed key, written for the per-packet path: the key's inner and outer
    /// pad states are precomputed once, every buffer is preallocated, so <see cref="Compute"/> allocates nothing (review
    /// N1; the BCL HMACSHA256 allocates its result array on every call). Not thread-safe: one instance per link direction.
    /// </summary>
    public sealed class ChibiHmacSha256
    {
        static readonly uint[] K =
        {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
        };
        static readonly uint[] H0 = { 0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19 };

        readonly uint[] _innerInit = new uint[8], _outerInit = new uint[8];
        readonly uint[] _h = new uint[8];
        readonly uint[] _w = new uint[64];
        readonly byte[] _block = new byte[64];
        readonly byte[] _innerDigest = new byte[32];
        int _blockLen;
        long _total;

        /// <summary>Key of any length (longer than 64 bytes is hashed first, per RFC 2104).</summary>
        /// <example><code>var mac = new ChibiHmacSha256(pairKey);</code></example>
        public ChibiHmacSha256(byte[] key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            var k = new byte[64];
            if (key.Length > 64)
            {
                var d = new byte[32];
                Reset(H0); Update(key, 0, key.Length); Final(d, 0);
                Buffer.BlockCopy(d, 0, k, 0, 32);
            }
            else Buffer.BlockCopy(key, 0, k, 0, key.Length);
            var pad = new byte[64];
            for (int i = 0; i < 64; i++) pad[i] = (byte)(k[i] ^ 0x36);
            Reset(H0); Update(pad, 0, 64); Array.Copy(_h, _innerInit, 8);
            for (int i = 0; i < 64; i++) pad[i] = (byte)(k[i] ^ 0x5c);
            Reset(H0); Update(pad, 0, 64); Array.Copy(_h, _outerInit, 8);
            Array.Clear(k, 0, 64);
            Array.Clear(pad, 0, 64);
        }

        /// <summary>
        /// Write HMAC(key, prefix || data[0..length)) into <paramref name="mac"/> (32 bytes from <paramref name="macOffset"/>).
        /// <paramref name="prefix"/> is one byte mixed in front (the link direction). No allocation.
        /// </summary>
        public void Compute(byte prefix, byte[] data, int offset, int length, byte[] mac, int macOffset)
        {
            Resume(_innerInit, 64);
            _block[_blockLen++] = prefix; _total++;
            Update(data, offset, length);
            Final(_innerDigest, 0);
            Resume(_outerInit, 64);
            Update(_innerDigest, 0, 32);
            Final(mac, macOffset);
        }

        void Reset(uint[] init) { Resume(init, 0); }

        void Resume(uint[] init, long already)
        {
            Array.Copy(init, _h, 8);
            _blockLen = 0;
            _total = already;
        }

        void Update(byte[] data, int offset, int length)
        {
            for (int i = 0; i < length; i++)
            {
                _block[_blockLen++] = data[offset + i];
                if (_blockLen == 64) { Compress(); _blockLen = 0; }
            }
            _total += length;
        }

        void Final(byte[] o, int off)
        {
            long bits = _total * 8;
            _block[_blockLen++] = 0x80;
            if (_blockLen > 56)
            {
                while (_blockLen < 64) _block[_blockLen++] = 0;
                Compress();
                _blockLen = 0;
            }
            while (_blockLen < 56) _block[_blockLen++] = 0;
            for (int i = 7; i >= 0; i--) _block[_blockLen++] = (byte)(bits >> (8 * i));
            Compress();
            _blockLen = 0;
            for (int i = 0; i < 8; i++)
            {
                o[off + 4 * i] = (byte)(_h[i] >> 24);
                o[off + 4 * i + 1] = (byte)(_h[i] >> 16);
                o[off + 4 * i + 2] = (byte)(_h[i] >> 8);
                o[off + 4 * i + 3] = (byte)_h[i];
            }
        }

        static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));

        void Compress()
        {
            var w = _w;
            for (int i = 0; i < 16; i++)
                w[i] = (uint)(_block[4 * i] << 24 | _block[4 * i + 1] << 16 | _block[4 * i + 2] << 8 | _block[4 * i + 3]);
            for (int i = 16; i < 64; i++)
            {
                uint s0 = Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
                uint s1 = Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }
            uint a = _h[0], b = _h[1], c = _h[2], d = _h[3], e = _h[4], f = _h[5], g = _h[6], h = _h[7];
            for (int i = 0; i < 64; i++)
            {
                uint S1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
                uint ch = (e & f) ^ (~e & g);
                uint t1 = h + S1 + ch + K[i] + w[i];
                uint S0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
                uint maj = (a & b) ^ (a & c) ^ (b & c);
                uint t2 = S0 + maj;
                h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
            }
            _h[0] += a; _h[1] += b; _h[2] += c; _h[3] += d; _h[4] += e; _h[5] += f; _h[6] += g; _h[7] += h;
        }
    }

    /// <summary>
    /// Authenticated wrapper for one team-play mesh link (review N1, protocol 6). Every datagram in either direction gets an
    /// 8-byte tag = first 8 bytes of HMAC-SHA256(pairKey, senderSlot || datagram) appended, so it covers Hello, PeerHello,
    /// Start, Ready, Leave and every input datagram including its sender slot. The direction byte makes A-to-B tags useless
    /// B-to-A. The pair key comes from an X25519 exchange through the lobby (<see cref="ChibiX25519.PairKey"/>): someone
    /// holding the Relay join code but not a roster player's private key cannot produce a valid tag.
    ///
    /// Datagrams that are too short or carry a wrong tag are counted in <see cref="Rejected"/> and dropped (never thrown).
    ///
    /// Replay (second review, decision: no counter). The tag has no sequence number, so someone on the path could replay
    /// an old datagram of the same direction. That is harmless by construction of everything above this link: input
    /// windows are idempotent (frames already held are ignored, ChibiRollbackSession.Process / RollbackInputBuffer.AddRemote),
    /// acks, holds and seen frames only ever move forward (max), votes and agreed drop frames are fixed at their first
    /// value, checksums are compared once per frame, and control messages are idempotent and resent anyway. A replayed
    /// datagram can therefore only repeat what the peer already said; it cannot add, change or roll back anything. Relay
    /// itself runs over DTLS, so an off-path attacker cannot see the datagrams to replay them in the first place. Keep
    /// that invariant when adding a field (or add a counter to the MAC input).
    /// Forwards <see cref="IChibiPumpedTransport"/>, <see cref="IChibiLinkStatus"/> and <see cref="IDisposable"/> to the
    /// wrapped link. No allocation per datagram.
    ///
    /// 1v1 links stay untagged (decision): 1v1 has no lobby key exchange; a quick-match 1v1 lobby's Relay code is
    /// member-only and the handshake binds the match, which protocol 5 already reviewed.
    /// </summary>
    public sealed class ChibiAuthLink : IChibiTransport, IChibiPumpedTransport, IChibiLinkStatus, IDisposable
    {
        /// <summary>Tag bytes appended to each datagram.</summary>
        public const int TagBytes = 8;
        const int MaxDatagram = ChibiChannelMux.MaxDatagram + TagBytes;
        /// <summary>Most bad datagrams skipped in one Receive call before returning "nothing" (a flood cannot spin us).</summary>
        public const int MaxSkipsPerReceive = 64;

        readonly IChibiTransport _inner;
        readonly ChibiHmacSha256 _mac;
        readonly byte _mySlot, _peerSlot;
        readonly byte[] _tx = new byte[MaxDatagram];
        readonly byte[] _rx = new byte[MaxDatagram];
        readonly byte[] _tag = new byte[32];

        /// <summary>Datagrams dropped for a missing or wrong tag.</summary>
        public int Rejected { get; private set; }

        /// <summary>The wrapped raw link.</summary>
        public IChibiTransport Inner => _inner;

        /// <example><code>var link = new ChibiAuthLink(raw, pairKey, mySlot: 0, peerSlot: 2);</code></example>
        public ChibiAuthLink(IChibiTransport inner, byte[] pairKey, int mySlot, int peerSlot)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (pairKey == null || pairKey.Length < 16) throw new ArgumentException("pair key of at least 16 bytes required", nameof(pairKey));
            if (mySlot == peerSlot || mySlot < 0 || mySlot > 3 || peerSlot < 0 || peerSlot > 3) throw new ArgumentOutOfRangeException(nameof(peerSlot));
            _mac = new ChibiHmacSha256(pairKey);
            _mySlot = (byte)mySlot;
            _peerSlot = (byte)peerSlot;
        }

        public void Send(byte[] data, int length)
        {
            if (length <= 0 || length > ChibiChannelMux.MaxDatagram) return;
            Buffer.BlockCopy(data, 0, _tx, 0, length);
            _mac.Compute(_mySlot, _tx, 0, length, _tag, 0);
            Buffer.BlockCopy(_tag, 0, _tx, length, TagBytes);
            _inner.Send(_tx, length + TagBytes);
        }

        public int Receive(byte[] buffer)
        {
            for (int skip = 0; skip < MaxSkipsPerReceive; skip++)
            {
                int n = _inner.Receive(_rx);
                if (n <= 0) return 0;
                int body = n - TagBytes;
                if (body <= 0 || n > MaxDatagram || body > buffer.Length) { Rejected++; continue; }
                _mac.Compute(_peerSlot, _rx, 0, body, _tag, 0);
                int diff = 0;
                for (int i = 0; i < TagBytes; i++) diff |= _tag[i] ^ _rx[body + i];   // constant time
                if (diff != 0) { Rejected++; continue; }
                Buffer.BlockCopy(_rx, 0, buffer, 0, body);
                return body;
            }
            return 0;
        }

        public void Pump() => (_inner as IChibiPumpedTransport)?.Pump();
        public void Flush() => (_inner as IChibiPumpedTransport)?.Flush();
        public ChibiLinkState LinkState => _inner is IChibiLinkStatus s ? s.LinkState : ChibiLinkState.Connected;
        public void Dispose() => (_inner as IDisposable)?.Dispose();
    }
}
