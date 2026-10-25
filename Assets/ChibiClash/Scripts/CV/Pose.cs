using System;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// A subset of body landmarks, normalized to the mirrored camera image:
    /// x 0..1 left to right as the player sees themselves, y 0..1 bottom to top,
    /// z relative depth (negative = toward the camera; 0 when the source has no depth, e.g. MoveNet).
    /// </summary>
    public struct PoseFrame
    {
        public const int Nose = 0, LShoulder = 1, RShoulder = 2, LElbow = 3, RElbow = 4,
            LWrist = 5, RWrist = 6, LHip = 7, RHip = 8, Count = 9;

        public static readonly int[,] Bones =
        {
            { LShoulder, RShoulder }, { LShoulder, LElbow }, { LElbow, LWrist }, { RShoulder, RElbow },
            { RElbow, RWrist }, { LShoulder, LHip }, { RShoulder, RHip }, { LHip, RHip }
        };

        public Vector3[] p;
        public float confidence;
        public float aspect;     // image width / height (normalized x units are this much wider than y units); 0 = 1

        public PoseFrame Clone()
        {
            return new PoseFrame { p = (Vector3[])p.Clone(), confidence = confidence, aspect = aspect };
        }
    }

    /// <summary>Anything that produces body landmarks: the MoveNet camera tracker, or the built-in simulator.</summary>
    public interface IPoseSource
    {
        string Name { get; }
        bool HasPose { get; }
        PoseFrame Current { get; }
        /// <summary>Increases every time a genuinely new pose sample is produced (camera frames arrive slower than render frames).</summary>
        int SampleId { get; }
        /// <summary>Seconds between the last two new samples.</summary>
        float SampleDt { get; }
        void Tick(float dt);
    }

    /// <summary>Rule-based template matching of landmarks against the predefined gestures (GDD 4.1).</summary>
    public static class GestureClassifier
    {
        public const float MinConfidence = 0.3f;
        static readonly Vector3[] q = new Vector3[PoseFrame.Count];

        public static Gesture Classify(PoseFrame f)
        {
            // Work in square units so horizontal and vertical distances are comparable on a 4:3 camera image.
            float ax = f.aspect > 0f ? f.aspect : 1f;
            for (int i = 0; i < PoseFrame.Count; i++) q[i] = new Vector3(f.p[i].x * ax, f.p[i].y, f.p[i].z * ax);

            Vector3 ls = q[PoseFrame.LShoulder], rs = q[PoseFrame.RShoulder];
            Vector3 le = q[PoseFrame.LElbow], re = q[PoseFrame.RElbow];
            Vector3 lw = q[PoseFrame.LWrist], rw = q[PoseFrame.RWrist], nose = q[PoseFrame.Nose];
            float sw = Mathf.Abs(rs.x - ls.x);
            if (sw < 0.02f || f.confidence < MinConfidence) return Gesture.None;
            float shoulderY = (ls.y + rs.y) * 0.5f;
            float hipY = (q[PoseFrame.LHip].y + q[PoseFrame.RHip].y) * 0.5f;

            // both hands above the head
            if (lw.y > nose.y + 0.1f * sw && rw.y > nose.y + 0.1f * sw)
                return Gesture.RaiseHands;

            // punch: either a wrist thrust toward the camera (depth sources), or - for plain 2D cameras - an arm
            // held straight out at shoulder height (a jab out to the side).
            bool lPunch = lw.z - ls.z < -0.5f * sw && Mathf.Abs(lw.y - ls.y) < 0.6f * sw;
            bool rPunch = rw.z - rs.z < -0.5f * sw && Mathf.Abs(rw.y - rs.y) < 0.6f * sw;
            if (lPunch || rPunch || ArmOut(ls, le, lw, sw) || ArmOut(rs, re, rw, sw))
                return Gesture.PunchForward;

            // wrists swapped across the body at chest height
            bool crossed = Mathf.Sign(rw.x - lw.x) != Mathf.Sign(rs.x - ls.x) && Mathf.Abs(rw.x - lw.x) > 0.1f * sw;
            bool atChest = lw.y < shoulderY + 0.1f && lw.y > hipY && rw.y < shoulderY + 0.1f && rw.y > hipY;
            if (crossed && atChest)
                return Gesture.CrossArms;

            return Gesture.None;
        }

        static bool ArmOut(Vector3 s, Vector3 e, Vector3 w, float sw)
        {
            float upper = Vector2.Distance(s, e), fore = Vector2.Distance(e, w), total = Vector2.Distance(s, w);
            bool straight = total > 0.85f * (upper + fore);
            bool extended = total > 1.4f * sw;
            bool level = Mathf.Abs(w.y - s.y) < 0.4f * sw;
            return straight && extended && level;
        }

        /// <summary>0..1: level shoulders, head upright above the shoulders, not leaning sideways.</summary>
        public static float PostureScore(PoseFrame f)
        {
            float ax = f.aspect > 0f ? f.aspect : 1f;
            var p = f.p;
            Vector3 ls = p[PoseFrame.LShoulder], rs = p[PoseFrame.RShoulder], nose = p[PoseFrame.Nose];
            float sw = Mathf.Abs(rs.x - ls.x) * ax;
            if (sw < 0.02f) return 0f;
            float tilt = Mathf.Abs(ls.y - rs.y) / sw;
            float neck = (nose.y - (ls.y + rs.y) * 0.5f) / sw;
            float lateral = Mathf.Abs(nose.x - (ls.x + rs.x) * 0.5f) * ax / sw;
            return Mathf.Clamp01(1f - tilt * 2.5f) * Mathf.Clamp01((neck - 0.2f) / 0.45f) * Mathf.Clamp01(1f - lateral * 1.5f);
        }
    }

    /// <summary>
    /// Client-side CV telemetry checks (GDD 3, anti-cheat). Flags streams that look spoofed:
    /// frozen landmarks (replayed image), low confidence, or poses that move faster than a body can (macros).
    /// Evaluated once per NEW pose sample, using the real time between samples, so camera frame rate doesn't matter.
    /// In online play the same checks run again on the server against the uploaded telemetry.
    /// </summary>
    public class TelemetryValidator
    {
        public const float MaxJointSpeed = 14f;   // normalized units per second; a real arm swing is well below this
        PoseFrame prev;
        bool hasPrev;
        int frozenSamples;
        float lastSnap = -10f;
        float now;

        public bool Valid { get; private set; } = true;
        public string Reason { get; private set; } = "OK";

        public void Push(PoseFrame f, float dt)
        {
            now += dt;
            if (hasPrev)
            {
                float maxDelta = 0f;
                for (int i = 0; i < PoseFrame.LHip; i++)      // face, shoulders, elbows, wrists (hips are often off-camera)
                    maxDelta = Mathf.Max(maxDelta, (f.p[i] - prev.p[i]).magnitude);
                frozenSamples = maxDelta < 1e-5f ? frozenSamples + 1 : 0;
                if (maxDelta / Mathf.Max(dt, 1e-3f) > MaxJointSpeed) lastSnap = now;
            }
            prev = f.Clone();
            hasPrev = true;

            if (f.confidence < GestureClassifier.MinConfidence) { Valid = false; Reason = "Low tracking confidence"; }
            else if (frozenSamples > 20) { Valid = false; Reason = "Frozen feed (spoof?)"; }
            else if (now - lastSnap < 1f) { Valid = false; Reason = "Instant pose snap (macro?)"; }
            else { Valid = true; Reason = "OK"; }
        }
    }

    /// <summary>Per-player CV state: stable gesture detection, telemetry validation and posture tracking.</summary>
    public class GestureTracker
    {
        public readonly IPoseSource Source;
        public readonly TelemetryValidator Validator = new TelemetryValidator();

        public Gesture Raw { get; private set; }
        public Gesture Stable { get; private set; }
        public float StableTime { get; private set; }
        public Gesture JustStarted { get; private set; }
        public float Posture { get; private set; } = 1f;
        public bool TelemetryValid => Validator.Valid;
        /// <summary>True when a real camera is feeding this tracker (as opposed to the keyboard simulator).</summary>
        public bool IsLive => Source is AutoPoseSource a && a.UsingReal;

        Gesture candidate;
        float candidateTime;
        float postureSum, postureTime;
        int lastSample = -1;

        public float PostureAverage => postureTime > 0f ? postureSum / postureTime : 1f;

        public GestureTracker(IPoseSource source) { Source = source; }

        public void Tick(float dt)
        {
            JustStarted = Gesture.None;
            Source.Tick(dt);
            if (!Source.HasPose) { Raw = Gesture.None; Stable = Gesture.None; return; }

            var f = Source.Current;
            if (Source.SampleId != lastSample)
            {
                lastSample = Source.SampleId;
                Validator.Push(f, Source.SampleDt);
            }
            Raw = GestureClassifier.Classify(f);
            Posture = GestureClassifier.PostureScore(f);

            if (Raw == candidate) candidateTime += dt;
            else { candidate = Raw; candidateTime = 0f; }

            if (candidate != Stable && candidateTime >= 0.1f)
            {
                Stable = candidate;
                StableTime = 0f;
                if (Stable != Gesture.None) JustStarted = Stable;
            }
            StableTime += dt;
        }

        public void SamplePosture(float dt)
        {
            if (!Source.HasPose) return;
            postureSum += (Posture >= 0.6f ? 1f : 0f) * dt;
            postureTime += dt;
        }

        public void ResetPosture() { postureSum = 0f; postureTime = 0f; }
    }

    /// <summary>Stand-in landmark source that animates a skeleton toward gesture templates (keyboard-driven).</summary>
    public class SimulatedPoseSource : IPoseSource
    {
        readonly Func<Gesture> target;
        readonly Func<bool> slouch;
        PoseFrame current;

        public string Name { get; }
        public bool HasPose => true;
        public PoseFrame Current => current;
        public int SampleId { get; private set; }
        public float SampleDt { get; private set; } = 1f / 60f;

        public SimulatedPoseSource(string name, Func<Gesture> target, Func<bool> slouch = null)
        {
            Name = name;
            this.target = target;
            this.slouch = slouch;
            current = Template(Gesture.None, false);
        }

        public void Tick(float dt)
        {
            var goal = Template(target(), slouch != null && slouch());
            float k = 1f - Mathf.Exp(-dt * 14f);
            for (int i = 0; i < PoseFrame.Count; i++)
            {
                var jitter = new Vector3(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f), 0f) * 0.003f;
                current.p[i] = Vector3.Lerp(current.p[i], goal.p[i], k) + jitter;
            }
            current.confidence = 0.95f;
            current.aspect = 1f;
            SampleId++;
            SampleDt = Mathf.Max(dt, 1e-3f);
        }

        public static PoseFrame Template(Gesture g, bool slouched)
        {
            var p = new Vector3[PoseFrame.Count];
            p[PoseFrame.Nose] = new Vector3(0.5f, 0.72f, 0f);
            p[PoseFrame.LShoulder] = new Vector3(0.4f, 0.58f, 0f);
            p[PoseFrame.RShoulder] = new Vector3(0.6f, 0.58f, 0f);
            p[PoseFrame.LElbow] = new Vector3(0.37f, 0.43f, 0f);
            p[PoseFrame.RElbow] = new Vector3(0.63f, 0.43f, 0f);
            p[PoseFrame.LWrist] = new Vector3(0.38f, 0.3f, 0f);
            p[PoseFrame.RWrist] = new Vector3(0.62f, 0.3f, 0f);
            p[PoseFrame.LHip] = new Vector3(0.43f, 0.22f, 0f);
            p[PoseFrame.RHip] = new Vector3(0.57f, 0.22f, 0f);

            switch (g)
            {
                case Gesture.CrossArms:
                    p[PoseFrame.LElbow] = new Vector3(0.38f, 0.45f, -0.05f);
                    p[PoseFrame.RElbow] = new Vector3(0.62f, 0.45f, -0.05f);
                    p[PoseFrame.LWrist] = new Vector3(0.56f, 0.5f, -0.08f);
                    p[PoseFrame.RWrist] = new Vector3(0.44f, 0.5f, -0.08f);
                    break;
                case Gesture.PunchForward:
                    p[PoseFrame.RElbow] = new Vector3(0.6f, 0.56f, -0.12f);
                    p[PoseFrame.RWrist] = new Vector3(0.57f, 0.58f, -0.26f);
                    break;
                case Gesture.RaiseHands:
                    p[PoseFrame.LElbow] = new Vector3(0.35f, 0.75f, 0f);
                    p[PoseFrame.RElbow] = new Vector3(0.65f, 0.75f, 0f);
                    p[PoseFrame.LWrist] = new Vector3(0.37f, 0.9f, 0f);
                    p[PoseFrame.RWrist] = new Vector3(0.63f, 0.9f, 0f);
                    break;
            }

            if (slouched)
            {
                p[PoseFrame.Nose] = new Vector3(0.46f, 0.63f, -0.05f);
                p[PoseFrame.LShoulder] += new Vector3(0f, 0.02f, 0f);
                p[PoseFrame.RShoulder] += new Vector3(0f, -0.02f, 0f);
            }
            return new PoseFrame { p = p, confidence = 0.95f, aspect = 1f };
        }
    }

    /// <summary>The player's webcam (WebCamTexture) for the on-screen overlay, the lighting check and pose tracking.</summary>
    public static class WebcamFeed
    {
        static WebCamTexture tex;
        static Color32[] buffer;
        static float lastSample = -10f;
        static float brightness = -1f;
        static float startedAt;

        public static WebCamTexture Texture => tex;
        public static bool Running => tex != null && tex.isPlaying && tex.width > 16;

        /// <summary>Human-readable state for the UI.</summary>
        public static string Status
        {
            get
            {
                if (WebCamTexture.devices.Length == 0) return "No camera found";
                if (tex == null) return "Camera off";
                if (!tex.isPlaying) return "Camera starting...";
                if (tex.width <= 16) return Time.unscaledTime - startedAt > 4f
                        ? "Waiting for camera frames - allow camera access for Unity in System Settings > Privacy > Camera"
                        : "Camera starting...";
                return "Camera live (" + tex.width + "x" + tex.height + ")";
            }
        }

        public static void SetEnabled(bool on)
        {
            if (on && tex == null)
            {
                if (WebCamTexture.devices.Length == 0) { Debug.Log("ChibiClash: no webcam found"); return; }
                tex = new WebCamTexture(WebCamTexture.devices[0].name, 640, 480, 30);
                tex.Play();
                startedAt = Time.unscaledTime;
            }
            else if (!on && tex != null)
            {
                tex.Stop();
                UnityEngine.Object.Destroy(tex);
                tex = null;
            }
        }

        /// <summary>Average luminance 0..1 of the camera image, or -1 if no camera.</summary>
        public static float Brightness()
        {
            if (!Running) return -1f;
            if (Time.unscaledTime - lastSample < 0.5f) return brightness;
            lastSample = Time.unscaledTime;
            if (buffer == null || buffer.Length != tex.width * tex.height) buffer = new Color32[tex.width * tex.height];
            tex.GetPixels32(buffer);
            long sum = 0;
            int n = 0;
            for (int i = 0; i < buffer.Length; i += 97)
            {
                var c = buffer[i];
                sum += (c.r * 299 + c.g * 587 + c.b * 114) / 1000;
                n++;
            }
            brightness = n > 0 ? sum / (n * 255f) : -1f;
            return brightness;
        }
    }
}
