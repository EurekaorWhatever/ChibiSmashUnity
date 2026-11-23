using System;
using System.Security.Cryptography;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// X25519 Diffie-Hellman (RFC 7748) in pure C#, for the per-pair link keys of team play (review N1, protocol 6).
    /// Field arithmetic mod 2^255 - 19 on sixteen 16-bit limbs held in <c>long</c>, after TweetNaCl: a Montgomery ladder of
    /// 255 steps with constant-time conditional swaps and no secret-dependent branches or table lookups.
    ///
    /// Used only at match setup (once per pair per queue), so it allocates freely; nothing here runs per packet.
    /// </summary>
    public static class ChibiX25519
    {
        /// <summary>Key and shared-secret length in bytes.</summary>
        public const int KeyBytes = 32;

        static readonly byte[] BasePoint = Make9();
        static readonly long[] A24 = { 0xDB41, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };   // 121665

        static byte[] Make9() { var b = new byte[KeyBytes]; b[0] = 9; return b; }

        /// <summary>A fresh random private key (32 bytes from the OS CSPRNG; clamping happens inside the ladder).</summary>
        public static byte[] NewPrivateKey()
        {
            var k = new byte[KeyBytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(k);
            return k;
        }

        /// <summary>Public key of <paramref name="privateKey"/>: X25519(k, 9).</summary>
        /// <example><code>byte[] pub = ChibiX25519.PublicKey(priv);</code></example>
        public static byte[] PublicKey(byte[] privateKey) => ScalarMult(privateKey, BasePoint);

        /// <summary>
        /// Shared secret X25519(k, u). Returns null when the result is all zero (a low-order peer key: RFC 7748 section 6.1
        /// says to abort), so a hostile peer cannot force a known key.
        /// </summary>
        /// <example><code>byte[] shared = ChibiX25519.SharedSecret(myPriv, theirPub);</code></example>
        public static byte[] SharedSecret(byte[] privateKey, byte[] peerPublic)
        {
            var s = ScalarMult(privateKey, peerPublic);
            int acc = 0;
            for (int i = 0; i < KeyBytes; i++) acc |= s[i];
            return acc == 0 ? null : s;
        }

        /// <summary>Raw X25519 function (RFC 7748 section 5): scalar <paramref name="k"/> times u-coordinate <paramref name="u"/>.</summary>
        /// <exception cref="ArgumentException">Either input is not 32 bytes.</exception>
        public static byte[] ScalarMult(byte[] k, byte[] u)
        {
            if (k == null || k.Length != KeyBytes) throw new ArgumentException("32-byte scalar required", nameof(k));
            if (u == null || u.Length != KeyBytes) throw new ArgumentException("32-byte u-coordinate required", nameof(u));
            var z = new byte[KeyBytes];
            Array.Copy(k, z, KeyBytes);
            z[31] = (byte)((k[31] & 127) | 64);
            z[0] &= 248;

            long[] x = new long[16], a = new long[16], b = new long[16], c = new long[16], d = new long[16], e = new long[16], f = new long[16];
            Unpack(x, u);
            for (int i = 0; i < 16; i++) { b[i] = x[i]; d[i] = a[i] = c[i] = 0; }
            a[0] = d[0] = 1;
            for (int i = 254; i >= 0; --i)
            {
                int r = (z[i >> 3] >> (i & 7)) & 1;
                Sel(a, b, r); Sel(c, d, r);
                Add(e, a, c); Sub(a, a, c); Add(c, b, d); Sub(b, b, d);
                Mul(d, e, e); Mul(f, a, a); Mul(a, c, a); Mul(c, b, e);
                Add(e, a, c); Sub(a, a, c); Mul(b, a, a); Sub(c, d, f);
                Mul(a, c, A24); Add(a, a, d); Mul(c, c, a); Mul(a, d, f);   // z2 = E * (AA + a24 * E), RFC 7748 section 5
                Mul(d, b, x); Mul(b, e, e);
                Sel(a, b, r); Sel(c, d, r);
            }
            Inv(c, c);
            Mul(a, a, c);
            var o = new byte[KeyBytes];
            Pack(o, a);
            Array.Clear(z, 0, z.Length);
            return o;
        }

        // ------------------------------------------------------------------ field arithmetic (TweetNaCl layout)

        static void Carry(long[] o)
        {
            for (int i = 0; i < 16; i++)
            {
                o[i] += 1L << 16;
                long c = o[i] >> 16;
                if (i < 15) o[i + 1] += c - 1;
                else o[0] += 38 * (c - 1);
                o[i] -= c << 16;
            }
        }

        static void Sel(long[] p, long[] q, int bit)
        {
            long c = ~(bit - 1L);
            for (int i = 0; i < 16; i++)
            {
                long t = c & (p[i] ^ q[i]);
                p[i] ^= t;
                q[i] ^= t;
            }
        }

        static void Pack(byte[] o, long[] n)
        {
            long[] m = new long[16], t = new long[16];
            for (int i = 0; i < 16; i++) t[i] = n[i];
            Carry(t); Carry(t); Carry(t);
            for (int j = 0; j < 2; j++)
            {
                m[0] = t[0] - 0xffed;
                for (int i = 1; i < 15; i++)
                {
                    m[i] = t[i] - 0xffff - ((m[i - 1] >> 16) & 1);
                    m[i - 1] &= 0xffff;
                }
                m[15] = t[15] - 0x7fff - ((m[14] >> 16) & 1);
                int b = (int)((m[15] >> 16) & 1);
                m[14] &= 0xffff;
                Sel(t, m, 1 - b);
            }
            for (int i = 0; i < 16; i++)
            {
                o[2 * i] = (byte)(t[i] & 0xff);
                o[2 * i + 1] = (byte)(t[i] >> 8);
            }
        }

        static void Unpack(long[] o, byte[] n)
        {
            for (int i = 0; i < 16; i++) o[i] = n[2 * i] + ((long)n[2 * i + 1] << 8);
            o[15] &= 0x7fff;
        }

        static void Add(long[] o, long[] a, long[] b) { for (int i = 0; i < 16; i++) o[i] = a[i] + b[i]; }
        static void Sub(long[] o, long[] a, long[] b) { for (int i = 0; i < 16; i++) o[i] = a[i] - b[i]; }

        static void Mul(long[] o, long[] a, long[] b)
        {
            var t = new long[31];
            for (int i = 0; i < 16; i++)
                for (int j = 0; j < 16; j++)
                    t[i + j] += a[i] * b[j];
            for (int i = 0; i < 15; i++) t[i] += 38 * t[i + 16];
            for (int i = 0; i < 16; i++) o[i] = t[i];
            Carry(o);
            Carry(o);
        }

        static void Inv(long[] o, long[] inp)
        {
            var c = new long[16];
            for (int i = 0; i < 16; i++) c[i] = inp[i];
            for (int a = 253; a >= 0; a--)
            {
                Mul(c, c, c);
                if (a != 2 && a != 4) Mul(c, c, inp);
            }
            for (int i = 0; i < 16; i++) o[i] = c[i];
        }

        // ------------------------------------------------------------------ pair key

        /// <summary>
        /// Link key of the pair (<paramref name="slotA"/>, <paramref name="slotB"/>) in lobby <paramref name="lobbyId"/>:
        /// HMAC-SHA256(shared, "chibi-pair" || lobbyId || minSlot || maxSlot). Both ends derive the same 32 bytes.
        /// </summary>
        /// <example><code>byte[] key = ChibiX25519.PairKey(shared, lobby.Id, 0, 2);</code></example>
        public static byte[] PairKey(byte[] shared, string lobbyId, int slotA, int slotB)
        {
            if (shared == null || shared.Length != KeyBytes) throw new ArgumentException("32-byte shared secret required", nameof(shared));
            string label = "chibi-pair" + (lobbyId ?? "") + "|" + Math.Min(slotA, slotB) + "|" + Math.Max(slotA, slotB);
            using (var h = new HMACSHA256(shared)) return h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(label));
        }
    }
}
