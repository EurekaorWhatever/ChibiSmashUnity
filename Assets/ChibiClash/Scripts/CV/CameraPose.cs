using System;
using Unity.InferenceEngine;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// Real body tracking from the webcam: Google's MoveNet "Lightning" single-person pose model
    /// (Apache-2.0, 17 keypoints) executed by Unity's Inference Engine. Runs at ~20 Hz on the GPU.
    /// One shared instance serves the menus and the match.
    /// </summary>
    public sealed class MoveNetPoseSource : IPoseSource, IDisposable
    {
        const int Size = 192;                 // model input is 192x192 RGB, int32 0..255
        const float Interval = 1f / 20f;
        // MoveNet keypoint order -> our PoseFrame landmarks
        const int KpNose = 0, KpLShoulder = 5, KpRShoulder = 6, KpLElbow = 7, KpRElbow = 8,
                  KpLWrist = 9, KpRWrist = 10, KpLHip = 11, KpRHip = 12;

        static MoveNetPoseSource shared;
        public static MoveNetPoseSource Shared => shared ?? (shared = new MoveNetPoseSource());

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { shared = null; }

        Worker worker;
        RenderTexture rt;
        Texture2D readTex;
        readonly int[] input = new int[Size * Size * 3];
        PoseFrame current = new PoseFrame { p = new Vector3[PoseFrame.Count], confidence = 0f, aspect = 4f / 3f };
        bool ready, failed, haveFrame;
        float timer, lastSampleTime, prevSampleTime;

        public string Name => "MoveNet (camera)";
        public string Error { get; private set; }
        public bool Ready => ready;
        public bool Failed => failed;
        public PoseFrame Current => current;
        public int SampleId { get; private set; }
        public float SampleDt { get; private set; } = Interval;
        public float Fps { get; private set; }
        public bool HasPose => ready && haveFrame && Time.unscaledTime - lastSampleTime < 0.6f && current.confidence >= GestureClassifier.MinConfidence;

        public bool EnsureLoaded()
        {
            if (ready) return true;
            if (failed) return false;
            try
            {
                var asset = Resources.Load<ModelAsset>("Models/movenet_lightning");
                if (asset == null) throw new Exception("pose model Resources/Models/movenet_lightning.onnx not found");
                var model = ModelLoader.Load(asset);
                var backend = SystemInfo.supportsComputeShaders ? BackendType.GPUCompute : BackendType.CPU;
                worker = new Worker(model, backend);
                rt = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                readTex = new Texture2D(Size, Size, TextureFormat.RGB24, false);
                ready = true;
            }
            catch (Exception e)
            {
                failed = true;
                Error = e.Message;
                Debug.LogWarning("ChibiClash: pose tracker unavailable: " + e.Message);
            }
            return ready;
        }

        public void Tick(float dt)
        {
            if (!WebcamFeed.Running || !EnsureLoaded()) { haveFrame = false; return; }
            timer += dt;
            if (timer < Interval) return;
            timer = 0f;
            try { Infer(WebcamFeed.Texture); }
            catch (Exception e) { failed = true; ready = false; Error = e.Message; Debug.LogWarning("ChibiClash: pose inference failed: " + e.Message); }
        }

        void Infer(WebCamTexture cam)
        {
            float w = cam.width, h = cam.height;
            float side = Mathf.Min(w, h);
            float cropW = side / w, cropH = side / h;           // centred square crop, as fractions of the frame
            float ox = (1f - cropW) * 0.5f, oy = (1f - cropH) * 0.5f;
            var scale = new Vector2(cropW, cropH);
            var offset = new Vector2(ox, oy);
            if (cam.videoVerticallyMirrored) { scale.y = -scale.y; offset.y = 1f - oy; }
            Graphics.Blit(cam, rt, scale, offset);

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            readTex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);
            RenderTexture.active = prev;

            var px = readTex.GetPixels32();                      // bottom row first; the model wants the top row first
            int k = 0;
            for (int y = Size - 1; y >= 0; y--)
                for (int x = 0; x < Size; x++)
                {
                    var c = px[y * Size + x];
                    input[k++] = c.r; input[k++] = c.g; input[k++] = c.b;
                }

            float[] kp;
            using (var t = new Tensor<int>(new TensorShape(1, Size, Size, 3), input))
            {
                worker.Schedule(t);
                kp = (worker.PeekOutput() as Tensor<float>).DownloadToArray();   // [1,1,17,3] = (y, x, score)
            }

            float aspect = w / h;
            Vector3 Map(int i)
            {
                float yy = kp[i * 3], xx = kp[i * 3 + 1];
                float xf = ox + xx * cropW;                       // position in the full camera frame
                float yf = oy + yy * cropH;
                return new Vector3(1f - xf, 1f - yf, 0f);         // mirror (so it behaves like a mirror) and flip y up
            }

            const float smooth = 0.65f;
            int[] map = { KpNose, KpLShoulder, KpRShoulder, KpLElbow, KpRElbow, KpLWrist, KpRWrist, KpLHip, KpRHip };
            bool first = !haveFrame;
            for (int i = 0; i < PoseFrame.Count; i++)
            {
                var np = Map(map[i]);
                current.p[i] = first ? np : Vector3.Lerp(current.p[i], np, smooth);
            }
            current.confidence = (kp[KpNose * 3 + 2] + kp[KpLShoulder * 3 + 2] + kp[KpRShoulder * 3 + 2]) / 3f;
            current.aspect = aspect;

            prevSampleTime = lastSampleTime;
            lastSampleTime = Time.unscaledTime;
            SampleDt = haveFrame ? Mathf.Max(lastSampleTime - prevSampleTime, 1e-3f) : Interval;
            Fps = Mathf.Lerp(Fps, 1f / SampleDt, 0.2f);
            haveFrame = true;
            SampleId++;
        }

        public void Dispose()
        {
            worker?.Dispose(); worker = null;
            if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); rt = null; }
            if (readTex != null) { UnityEngine.Object.Destroy(readTex); readTex = null; }
            ready = false;
        }
    }

    /// <summary>
    /// What a human player's tracker actually reads: the live camera when the webcam setting is on and the pose
    /// model is running, otherwise the keyboard-driven simulator. Switches automatically when settings change.
    /// </summary>
    public sealed class AutoPoseSource : IPoseSource
    {
        readonly SimulatedPoseSource sim;
        readonly Func<bool> wantCamera;
        readonly int playerIndex;

        public AutoPoseSource(int playerIndex, SimulatedPoseSource sim, Func<bool> wantCamera)
        {
            this.playerIndex = playerIndex;
            this.sim = sim;
            this.wantCamera = wantCamera;
        }

        /// <summary>Only player 1 owns the camera; a second human always uses the simulator.</summary>
        public bool UsingReal => playerIndex == 0 && wantCamera() && WebcamFeed.Running && MoveNetPoseSource.Shared.Ready;

        IPoseSource Active => UsingReal ? (IPoseSource)MoveNetPoseSource.Shared : sim;

        public string Name => UsingReal ? MoveNetPoseSource.Shared.Name : sim.Name;
        public bool HasPose => Active.HasPose;
        public PoseFrame Current => Active.Current;
        public int SampleId => Active.SampleId;
        public float SampleDt => Active.SampleDt;

        public void Tick(float dt)
        {
            if (playerIndex == 0 && wantCamera() && WebcamFeed.Running) MoveNetPoseSource.Shared.Tick(dt);
            sim.Tick(dt);
        }

        /// <summary>One line describing what the tracker is doing, for the setup screen.</summary>
        public string Status()
        {
            if (playerIndex != 0) return Loc.T("pose.simulated");
            if (!wantCamera()) return Loc.T("pose.camera_off");
            if (!WebcamFeed.Running) return WebcamFeed.Status;
            var m = MoveNetPoseSource.Shared;
            if (m.Failed) return Loc.T("pose.model_failed", m.Error);
            if (!m.Ready) return Loc.T("pose.loading");
            return m.HasPose ? Loc.T("pose.live", Mathf.RoundToInt(m.Fps)) : Loc.T("pose.step_back");
        }
    }
}
