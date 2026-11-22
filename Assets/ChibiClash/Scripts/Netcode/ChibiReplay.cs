using System;
using System.Collections.Generic;
using System.IO;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Collects the confirmed (final) inputs of both players in frame order. Hook it to
    /// ChibiRollbackSession.OnFrameConfirmed. Because the simulation is deterministic, config + inputs is a
    /// complete replay: playback just re-runs ChibiSimulation.Step.
    /// </summary>
    public sealed class ChibiReplayRecorder
    {
        public readonly ChibiMatchConfig Config;
        public readonly List<ushort> P0 = new List<ushort>();
        public readonly List<ushort> P1 = new List<ushort>();
        public int FrameCount => P0.Count;

        public ChibiReplayRecorder(in ChibiMatchConfig cfg) { Config = cfg; }

        public void Add(int frame, ChibiInputPacket p0, ChibiInputPacket p1)
        {
            if (frame != P0.Count) return;   // confirmed frames arrive in order; ignore anything else
            P0.Add(p0.Raw);
            P1.Add(p1.Raw);
        }

        public void Save(string path)
        {
            using (var fs = File.Create(path)) ChibiReplay.Write(fs, Config, P0, P1);
        }
    }

    /// <summary>
    /// Replay file format "CCRP" v3 (little endian; v2 rules v10, timescale-conversion.md section 8.3 item 3; v3 adds flags):
    ///   4  magic 'C','C','R','P'      1  version (3)
    ///   4  seed                       4  class0,class1,passive0,passive1 (1 byte each)
    ///   4  stage (int)                1  flags (bit 0 Intro, bit 1 Training, bit 2 HazardsOff; v3)
    ///   4  config hash (ChibiMatchConfig.ComputeHash: RulesVersion + every config field)
    ///   4  frame count (real ticks)   4  final checksum (state after the last frame)
    ///   n  run-length records: ushort p0, ushort p1, byte run (1..255)
    /// TryRead rejects v1 files and any file whose stored config hash differs from the hash of the config it read
    /// (other RulesVersion or a corrupt header), so an old replay can no longer re-simulate silently under new rules.
    /// Held inputs dominate real play, so RLE shrinks a 99 s match to a few KB.
    /// </summary>
    public static class ChibiReplay
    {
        /// <summary>v3 (rules v16): adds a flags byte after the stage (bit 0 Intro, bit 1 Training, bit 2 HazardsOff).</summary>
        const byte Version = 3;
        /// <summary>
        /// FlagHazardsOff (2026-11-16): HazardsOff is in the config hash but was never stored, so every hazards-off replay
        /// failed the hash check on load. It reuses a flags bit old readers rejected anyway, so the version stays 3.
        /// </summary>
        const int FlagIntro = 1, FlagTraining = 2, FlagHazardsOff = 4;

        public static ChibiSimState Simulate(in ChibiMatchConfig cfg, IList<ushort> p0, IList<ushort> p1)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            for (int i = 0; i < p0.Count; i++)
                ChibiSimulation.Step(s, new ChibiInputPacket { Raw = p0[i] }, new ChibiInputPacket { Raw = p1[i] });
            return s;
        }

        public static void Write(Stream stream, in ChibiMatchConfig cfg, IList<ushort> p0, IList<ushort> p1)
        {
            uint finalChecksum = Simulate(cfg, p0, p1).ComputeChecksum();
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                w.Write(new[] { (byte)'C', (byte)'C', (byte)'R', (byte)'P' });
                w.Write(Version);
                w.Write(cfg.Seed);
                w.Write((byte)cfg.Class0); w.Write((byte)cfg.Class1);
                w.Write((byte)cfg.Passive0); w.Write((byte)cfg.Passive1);
                w.Write(cfg.Stage);
                w.Write((byte)((cfg.Intro ? FlagIntro : 0) | (cfg.Training ? FlagTraining : 0) |
                               (cfg.HazardsOff ? FlagHazardsOff : 0)));   // v3
                w.Write(cfg.ComputeHash());
                w.Write((uint)p0.Count);
                w.Write(finalChecksum);

                int i = 0;
                while (i < p0.Count)
                {
                    int run = 1;
                    while (i + run < p0.Count && run < 255 && p0[i + run] == p0[i] && p1[i + run] == p1[i]) run++;
                    w.Write(p0[i]); w.Write(p1[i]); w.Write((byte)run);
                    i += run;
                }
            }
        }

        /// <summary>Parse a replay. Returns false for a corrupt or foreign file.</summary>
        public static bool TryRead(Stream stream, out ChibiMatchConfig cfg, out List<ushort> p0, out List<ushort> p1, out uint finalChecksum)
        {
            cfg = default; p0 = new List<ushort>(); p1 = new List<ushort>(); finalChecksum = 0;
            try
            {
                using (var r = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
                {
                    if (r.ReadByte() != 'C' || r.ReadByte() != 'C' || r.ReadByte() != 'R' || r.ReadByte() != 'P') return false;
                    if (r.ReadByte() != Version) return false;
                    cfg.Seed = r.ReadUInt32();
                    cfg.Class0 = (FighterClass)r.ReadByte(); cfg.Class1 = (FighterClass)r.ReadByte();
                    cfg.Passive0 = r.ReadByte(); cfg.Passive1 = r.ReadByte();
                    cfg.Stage = r.ReadInt32();
                    // REP-01: the config hash below is computed from these same bytes, so it cannot vouch for them.
                    if (!ChibiControlCodec.ValidClass(cfg.Class0) || !ChibiControlCodec.ValidClass(cfg.Class1)) return false;
                    if (cfg.Passive0 > 1 || cfg.Passive1 > 1) return false;
                    if (cfg.Stage < 0 || cfg.Stage >= ChibiStageTable.Count) return false;
                    int flags = r.ReadByte();
                    if ((flags & ~(FlagIntro | FlagTraining | FlagHazardsOff)) != 0) return false;
                    cfg.Intro = (flags & FlagIntro) != 0;
                    cfg.Training = (flags & FlagTraining) != 0;
                    cfg.HazardsOff = (flags & FlagHazardsOff) != 0;
                    uint configHash = r.ReadUInt32();
                    if (configHash != cfg.ComputeHash()) return false;   // other RulesVersion or corrupt header
                    uint frames = r.ReadUInt32();
                    finalChecksum = r.ReadUInt32();
                    if (frames > 60u * 60u * 60u) return false;   // > 1 hour: reject

                    while (p0.Count < frames)
                    {
                        ushort a = r.ReadUInt16(), b = r.ReadUInt16();
                        int run = r.ReadByte();
                        if (run == 0) return false;
                        for (int k = 0; k < run && p0.Count < frames; k++) { p0.Add(a); p1.Add(b); }
                    }
                }
                return true;
            }
            catch (IOException) { return false; }            // truncated (EndOfStreamException) or unreadable
            catch (ObjectDisposedException) { return false; }
        }

        /// <summary>Re-simulate a replay and confirm it reproduces the recorded final state (determinism check).</summary>
        public static bool Verify(Stream stream)
        {
            if (!TryRead(stream, out var cfg, out var p0, out var p1, out uint expected)) return false;
            // A file is untrusted input: anything the simulation chokes on is "not a valid replay", never a crash.
            try { return Simulate(cfg, p0, p1).ComputeChecksum() == expected; }
            catch (Exception) { return false; }
        }
    }
}
