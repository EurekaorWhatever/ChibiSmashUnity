using System;
using System.Collections.Generic;
using System.IO;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Collects the confirmed (final) inputs of every fighter in frame order. Hook it to
    /// ChibiRollbackSession.OnFrameConfirmed (1v1) or ChibiLocalSession.OnFrameAll. Because the simulation is
    /// deterministic, config + inputs is a complete replay: playback just re-runs ChibiSimulation.Step.
    /// </summary>
    public sealed class ChibiReplayRecorder
    {
        public readonly ChibiMatchConfig Config;
        /// <summary>Inputs of slot 0 / 1 (1v1 callers); <see cref="Lanes"/>[i] is the list of slot i.</summary>
        public readonly List<ushort> P0 = new List<ushort>();
        public readonly List<ushort> P1 = new List<ushort>();
        /// <summary>Rules v25: one input list per fighter (length = <see cref="ChibiMatchConfig.EffectiveCount"/>; lanes 0 / 1 are P0 / P1).</summary>
        public readonly List<ushort>[] Lanes;
        public int FrameCount => P0.Count;

        public ChibiReplayRecorder(in ChibiMatchConfig cfg)
        {
            Config = cfg;
            int n = cfg.EffectiveCount;
            Lanes = new List<ushort>[n];
            Lanes[0] = P0; Lanes[1] = P1;
            for (int i = 2; i < n; i++) Lanes[i] = new List<ushort>();
        }

        /// <summary>Adds a 1v1 frame (slots 2..3, if any, get no input).</summary>
        public void Add(int frame, ChibiInputPacket p0, ChibiInputPacket p1)
        {
            if (frame != P0.Count) return;   // confirmed frames arrive in order; ignore anything else
            P0.Add(p0.Raw);
            P1.Add(p1.Raw);
            for (int i = 2; i < Lanes.Length; i++) Lanes[i].Add(0);
        }

        /// <summary>Rules v25: adds a frame with one input per fighter (<paramref name="inputs"/>.Length &gt;= lane count).</summary>
        public void Add(int frame, ChibiInputPacket[] inputs)
        {
            if (frame != P0.Count || inputs == null || inputs.Length < Lanes.Length) return;
            for (int i = 0; i < Lanes.Length; i++) Lanes[i].Add(inputs[i].Raw);
        }

        public void Save(string path)
        {
            using (var fs = File.Create(path)) ChibiReplay.Write(fs, Config, Lanes);
        }
    }

    /// <summary>
    /// Replay file format "CCRP" v4 (little endian; v2 rules v10, v3 flags, v4 rules v25 team play):
    ///   4  magic 'C','C','R','P'      1  version (4)
    ///   4  seed                       1  format (0 1v1, 1 FFA, 2 2v2)   1  count n (2..4)
    ///   n x 3  class, passive, team (1 byte each, slot order)
    ///   4  stage (int)                1  flags (bit 0 Intro, bit 1 Training, bit 2 HazardsOff)
    ///   4  config hash (ChibiMatchConfig.ComputeHash: RulesVersion + every config field)
    ///   4  frame count (real ticks)   4  final checksum (state after the last frame)
    ///   n  run-length records: n x ushort input, byte run (1..255)
    /// TryRead rejects other versions (v3 and older) and any file whose stored config hash differs from the hash of the
    /// config it read; every header byte is range-checked first (REP-01: the hash is computed from the same bytes, so it
    /// cannot vouch for them). Held inputs dominate real play, so RLE shrinks a match to a few KB.
    /// </summary>
    public static class ChibiReplay
    {
        /// <summary>v4 (rules v25): format, fighter count and per-slot class / passive / team; n inputs per record.</summary>
        const byte Version = 4;
        /// <summary>
        /// FlagHazardsOff (2026-11-16): HazardsOff is in the config hash but was never stored, so every hazards-off replay
        /// failed the hash check on load. It reuses a flags bit old readers rejected anyway.
        /// </summary>
        const int FlagIntro = 1, FlagTraining = 2, FlagHazardsOff = 4;

        /// <summary>Re-simulates a 1v1 replay from its two input lists.</summary>
        public static ChibiSimState Simulate(in ChibiMatchConfig cfg, IList<ushort> p0, IList<ushort> p1) =>
            Simulate(cfg, new[] { p0, p1 });

        /// <summary>Rules v25: re-simulates a replay from one input list per fighter (at least EffectiveCount lists).</summary>
        public static ChibiSimState Simulate(in ChibiMatchConfig cfg, IList<ushort>[] lanes)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            int n = s.Count, frames = lanes[0].Count;
            var ins = new ChibiInputPacket[ChibiSimConst.MaxFighters];
            for (int f = 0; f < frames; f++)
            {
                for (int i = 0; i < n; i++) ins[i] = new ChibiInputPacket { Raw = lanes[i][f] };
                ChibiSimulation.Step(s, ins);
            }
            return s;
        }

        /// <summary>Writes a 1v1 replay.</summary>
        public static void Write(Stream stream, in ChibiMatchConfig cfg, IList<ushort> p0, IList<ushort> p1) =>
            Write(stream, cfg, new[] { p0, p1 });

        /// <summary>Rules v25: writes a replay with one input list per fighter (at least EffectiveCount lists, equal lengths).</summary>
        /// <exception cref="ArgumentException">Fewer lanes than fighters.</exception>
        public static void Write(Stream stream, in ChibiMatchConfig cfg, IList<ushort>[] lanes)
        {
            int n = cfg.EffectiveCount;
            if (lanes == null || lanes.Length < n) throw new ArgumentException("one input list per fighter is required", nameof(lanes));
            uint finalChecksum = Simulate(cfg, lanes).ComputeChecksum();
            int frames = lanes[0].Count;
            using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                w.Write(new[] { (byte)'C', (byte)'C', (byte)'R', (byte)'P' });
                w.Write(Version);
                w.Write(cfg.Seed);
                w.Write((byte)cfg.EffectiveFormat);
                w.Write((byte)n);
                for (int i = 0; i < n; i++)
                {
                    w.Write((byte)cfg.ClassOf(i)); w.Write((byte)cfg.PassiveOf(i)); w.Write((byte)cfg.TeamOf(i));
                }
                w.Write(cfg.Stage);
                w.Write((byte)((cfg.Intro ? FlagIntro : 0) | (cfg.Training ? FlagTraining : 0) |
                               (cfg.HazardsOff ? FlagHazardsOff : 0)));
                w.Write(cfg.ComputeHash());
                w.Write((uint)frames);
                w.Write(finalChecksum);

                int f = 0;
                while (f < frames)
                {
                    int run = 1;
                    while (f + run < frames && run < 255 && SameFrame(lanes, n, f, f + run)) run++;
                    for (int i = 0; i < n; i++) w.Write(lanes[i][f]);
                    w.Write((byte)run);
                    f += run;
                }
            }
        }

        static bool SameFrame(IList<ushort>[] lanes, int n, int a, int b)
        {
            for (int i = 0; i < n; i++) if (lanes[i][a] != lanes[i][b]) return false;
            return true;
        }

        /// <summary>Parse a 1v1 replay. Returns false for a corrupt or foreign file, or one with more than two fighters.</summary>
        public static bool TryRead(Stream stream, out ChibiMatchConfig cfg, out List<ushort> p0, out List<ushort> p1, out uint finalChecksum)
        {
            p0 = new List<ushort>(); p1 = new List<ushort>();
            if (!TryRead(stream, out cfg, out var lanes, out finalChecksum) || lanes.Length != 2) return false;
            p0 = lanes[0]; p1 = lanes[1];
            return true;
        }

        /// <summary>
        /// Rules v25: parse a replay into one input list per fighter. Returns false for a corrupt or foreign file: wrong
        /// magic or version, a format / count / class / passive / team / stage / flag out of range, a config hash
        /// mismatch, more than an hour of frames or a truncated body.
        /// </summary>
        public static bool TryRead(Stream stream, out ChibiMatchConfig cfg, out List<ushort>[] lanes, out uint finalChecksum)
        {
            cfg = default; lanes = Array.Empty<List<ushort>>(); finalChecksum = 0;
            try
            {
                using (var r = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
                {
                    if (r.ReadByte() != 'C' || r.ReadByte() != 'C' || r.ReadByte() != 'R' || r.ReadByte() != 'P') return false;
                    if (r.ReadByte() != Version) return false;
                    cfg.Seed = r.ReadUInt32();
                    int format = r.ReadByte(), n = r.ReadByte();
                    // REP-01: the config hash below is computed from these same bytes, so it cannot vouch for them.
                    if (format > ChibiMatchConfig.Format2v2) return false;
                    cfg.Format = format;
                    cfg.Count = n;
                    if (n < 2 || n > ChibiSimConst.MaxFighters || n != cfg.EffectiveCount) return false;
                    for (int i = 0; i < n; i++)
                    {
                        var cls = (FighterClass)r.ReadByte();
                        int passive = r.ReadByte(), team = r.ReadByte();
                        if (!ChibiControlCodec.ValidClass(cls) || passive > 1) return false;
                        switch (i)
                        {
                            case 0: cfg.Class0 = cls; cfg.Passive0 = passive; cfg.Team0 = team; break;
                            case 1: cfg.Class1 = cls; cfg.Passive1 = passive; cfg.Team1 = team; break;
                            case 2: cfg.Class2 = cls; cfg.Passive2 = passive; cfg.Team2 = team; break;
                            default: cfg.Class3 = cls; cfg.Passive3 = passive; cfg.Team3 = team; break;
                        }
                        if (format != ChibiMatchConfig.Format2v2 && team != i) return false;   // 1v1 / FFA: own team
                    }
                    if (format != ChibiMatchConfig.Format2v2) { cfg.Team0 = cfg.Team1 = cfg.Team2 = cfg.Team3 = 0; }
                    else if (!cfg.TeamsValid) return false;
                    if (format == ChibiMatchConfig.Format1v1) cfg.Count = 0;   // the default 1v1 config (hash unchanged)
                    cfg.Stage = r.ReadInt32();
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

                    var read = new List<ushort>[n];
                    for (int i = 0; i < n; i++) read[i] = new List<ushort>();
                    var rec = new ushort[ChibiSimConst.MaxFighters];
                    while (read[0].Count < frames)
                    {
                        for (int i = 0; i < n; i++) rec[i] = r.ReadUInt16();
                        int run = r.ReadByte();
                        if (run == 0) return false;
                        for (int k = 0; k < run && read[0].Count < frames; k++)
                            for (int i = 0; i < n; i++) read[i].Add(rec[i]);
                    }
                    lanes = read;
                }
                return true;
            }
            catch (IOException) { return false; }            // truncated (EndOfStreamException) or unreadable
            catch (ObjectDisposedException) { return false; }
        }

        /// <summary>Re-simulate a replay and confirm it reproduces the recorded final state (determinism check).</summary>
        public static bool Verify(Stream stream)
        {
            if (!TryRead(stream, out var cfg, out List<ushort>[] lanes, out uint expected)) return false;
            // A file is untrusted input: anything the simulation chokes on is "not a valid replay", never a crash.
            try { return Simulate(cfg, lanes).ComputeChecksum() == expected; }
            catch (Exception) { return false; }
        }
    }
}
