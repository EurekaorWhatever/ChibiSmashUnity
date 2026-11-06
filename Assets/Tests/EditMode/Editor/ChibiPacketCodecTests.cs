using System;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Story type: Logic (wire codec). Evidence: automated EditMode unit test. Gate: BLOCKING.
    ///
    /// Regression tests for the 12 -> 16 bit input widening and the silent-truncation bug
    /// (BitBuffer.Write sets Overrun and skips; ChibiPacketCodec.Write never checked it).
    /// Contract: WireBits = 16, Write throws InvalidOperationException on overrun, TryRead never throws.
    /// </summary>
    public class ChibiPacketCodecTests
    {
        // 24-bit frame counters (ChibiPacketCodec.FrameBits is private; value documented in its summary).
        const int MaxFrame = 0xFFFFFF;
        // AckFrame travels as AckFrame + 1 inside 24 bits, so its largest valid value is one lower.
        const int MaxAckFrame = MaxFrame - 1;
        // Advantage is clamped to -8..7 on the wire; 7 encodes as all four bits set.
        const int MaxAdvantage = 7;
        const uint MaxChecksum = 0xFFFFFFFF;
        const int TooSmallBufferBytes = 4;

        // All 16 bits set except the direction nibble, which must be a legal ChibiDir (0..8):
        // Sanitized() zeroes any direction > 8, so raw 0xFFFF can never round-trip, before or after the fix.
        // Bits 12..15 are set, so these values only survive once WireBits is 16.
        const ushort WideRawA = 0xFFF0 | (ushort)ChibiDir.NW; // 0xFFF8
        const ushort WideRawB = 0xFFF0 | (ushort)ChibiDir.W;  // 0xFFF7

        /// <summary>Worst-case wire size: every field at its widest, and no two consecutive inputs equal
        /// (so delta coding can never collapse one to a single "same" bit).</summary>
        static ChibiNetMessage WorstCaseMessage()
        {
            var m = new ChibiNetMessage
            {
                LastFrame = MaxFrame,
                AckFrame = MaxAckFrame,
                Advantage = MaxAdvantage,
                Count = ChibiNetMessage.MaxInputs,
                HasChecksum = true,
                ChecksumFrame = MaxFrame,
                Checksum = MaxChecksum,
            };
            for (int i = 0; i < m.Count; i++)
                m.Inputs[i] = new ChibiInputPacket { Raw = WorstCaseRaw(i) };
            return m;
        }

        static ushort WorstCaseRaw(int index) => index % 2 == 0 ? WideRawA : WideRawB;

        /// <summary>A normal mid-match message: inputs use only the low 12 bits, one repeat, no checksum.</summary>
        static ChibiNetMessage TypicalMessage()
        {
            var m = new ChibiNetMessage
            {
                LastFrame = 1200,
                AckFrame = 1197,
                Advantage = -2,
                Count = 3,
                HasChecksum = false,
            };
            m.Inputs[0] = ChibiInputPacket.Make(ChibiDir.E, ChibiButtons.Light);
            m.Inputs[1] = ChibiInputPacket.Make(ChibiDir.E, ChibiButtons.Light);
            m.Inputs[2] = ChibiInputPacket.Make(ChibiDir.N, ChibiButtons.Jump | ChibiButtons.Shield);
            return m;
        }

        static ChibiNetMessage RoundTrip(ChibiNetMessage sent, out bool ok)
        {
            var buffer = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            ChibiPacketCodec.Write(buffer, sent);
            buffer.BeginRead();
            var received = new ChibiNetMessage();
            ok = ChibiPacketCodec.TryRead(buffer, received);
            return received;
        }

        static void AssertHeaderEqual(ChibiNetMessage expected, ChibiNetMessage actual)
        {
            Assert.AreEqual(expected.LastFrame, actual.LastFrame, "LastFrame");
            Assert.AreEqual(expected.AckFrame, actual.AckFrame, "AckFrame");
            Assert.AreEqual(expected.Advantage, actual.Advantage, "Advantage");
            Assert.AreEqual(expected.Count, actual.Count, "Count");
            Assert.AreEqual(expected.HasChecksum, actual.HasChecksum, "HasChecksum");
            Assert.AreEqual(expected.ChecksumFrame, actual.ChecksumFrame, "ChecksumFrame");
            Assert.AreEqual(expected.Checksum, actual.Checksum, "Checksum");
        }

        // ------------------------------------------------------------------ overrun must not be silent

        [Test]
        public void Write_BufferTooSmall_Throws()
        {
            // Arrange
            var buffer = new BitBuffer(TooSmallBufferBytes);
            var message = WorstCaseMessage();

            // Act + Assert
            Assert.Throws<InvalidOperationException>(() => ChibiPacketCodec.Write(buffer, message));
        }

        // ------------------------------------------------------------------ round trips

        [Test]
        public void RoundTrip_WorstCaseMessage_AllFieldsPreserved()
        {
            // Arrange
            var sent = WorstCaseMessage();

            // Act
            var received = RoundTrip(sent, out bool ok);

            // Assert
            Assert.IsTrue(ok, "TryRead rejected a worst-case message written into MaxPacketBytes");
            AssertHeaderEqual(sent, received);
            for (int i = 0; i < sent.Count; i++)
                Assert.AreEqual(WorstCaseRaw(i), received.Inputs[i].Raw, $"Inputs[{i}].Raw (bits 12..15 lost?)");
        }

        [Test]
        public void RoundTrip_TypicalMessage_AllFieldsPreserved()
        {
            // Arrange
            var sent = TypicalMessage();

            // Act
            var received = RoundTrip(sent, out bool ok);

            // Assert
            Assert.IsTrue(ok, "TryRead rejected a typical message");
            AssertHeaderEqual(sent, received);
            for (int i = 0; i < sent.Count; i++)
                Assert.AreEqual(sent.Inputs[i].Raw, received.Inputs[i].Raw, $"Inputs[{i}].Raw");
        }
    }
}
