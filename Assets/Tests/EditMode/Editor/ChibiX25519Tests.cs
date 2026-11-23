using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Review N1: the pure-C# X25519 used for per-pair link keys, against RFC 7748 test vectors.</summary>
    public class ChibiX25519Tests
    {
        static byte[] Hex(string h)
        {
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = System.Convert.ToByte(h.Substring(2 * i, 2), 16);
            return b;
        }

        static string ToHex(byte[] b)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        // RFC 7748 section 6.1 (Diffie-Hellman)
        const string AlicePriv = "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a";
        const string AlicePub = "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a";
        const string BobPriv = "5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb";
        const string BobPub = "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f";
        const string Shared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";

        [Test]
        public void Rfc7748_6_1_PublicKeys()
        {
            Assert.AreEqual(AlicePub, ToHex(ChibiX25519.PublicKey(Hex(AlicePriv))));
            Assert.AreEqual(BobPub, ToHex(ChibiX25519.PublicKey(Hex(BobPriv))));
        }

        [Test]
        public void Rfc7748_6_1_SharedSecretBothWays()
        {
            Assert.AreEqual(Shared, ToHex(ChibiX25519.SharedSecret(Hex(AlicePriv), Hex(BobPub))));
            Assert.AreEqual(Shared, ToHex(ChibiX25519.SharedSecret(Hex(BobPriv), Hex(AlicePub))));
        }

        [Test]
        public void Rfc7748_5_2_IteratedOnceAndThousandTimes()
        {
            // k = u = 9; each round: (k, u) = (X25519(k, u), k).
            var k = new byte[32]; k[0] = 9;
            var u = new byte[32]; u[0] = 9;
            for (int i = 1; i <= 1000; i++)
            {
                var r = ChibiX25519.ScalarMult(k, u);
                u = k;
                k = r;
                if (i == 1) Assert.AreEqual("422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079", ToHex(k));
            }
            Assert.AreEqual("684cf59ba83309552800ef566f2f4d3c1c3887c49360e3875f2eb94d99532c51", ToHex(k));
        }

        [Test]
        public void LowOrderPeerKey_YieldsNoSharedSecret()
        {
            Assert.IsNull(ChibiX25519.SharedSecret(Hex(AlicePriv), new byte[32]), "the all-zero point must be refused");
        }

        [Test]
        public void PairKey_IsSymmetricInSlotOrder_AndBoundToTheLobby()
        {
            var s = Hex(Shared);
            Assert.AreEqual(ToHex(ChibiX25519.PairKey(s, "L1", 0, 2)), ToHex(ChibiX25519.PairKey(s, "L1", 2, 0)));
            Assert.AreNotEqual(ToHex(ChibiX25519.PairKey(s, "L1", 0, 2)), ToHex(ChibiX25519.PairKey(s, "L2", 0, 2)));
            Assert.AreNotEqual(ToHex(ChibiX25519.PairKey(s, "L1", 0, 2)), ToHex(ChibiX25519.PairKey(s, "L1", 0, 3)));
        }
    }
}
