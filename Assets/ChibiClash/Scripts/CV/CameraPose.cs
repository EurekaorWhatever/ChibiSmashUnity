using System;
using Unity.Collections;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;

namespace ChibiClash
{
    /// <summary>
    /// Real body tracking from the webcam: Google's MoveNet "Lightning" single-person pose model
    /// (Apache-2.0, 17 keypoints) executed by Unity's Inference Engine. Runs at ~20 Hz on the GPU.
    /// One shared instance serves the menus and the match.
    /// <para>
    /// Never stalls a frame (camera lag fix, user report 2026-11-26): one sample is a small pipeline spread over
    /// several frames. (1) The camera image is drawn into a 192x192 render texture and copied back with
    /// <see cref="AsyncGPUReadback"/>. (2) When those pixels arrive they are uploaded, the model is scheduled, and an
    /// async readback of its output is requested. (3) When that arrives the keypoints are applied. The old code waited
    /// for the GPU twice per sample (ReadPixels, then DownloadToArray), which froze the main thread 20 times a second.
    /// Platforms without async readback (WebGL) keep the blocking path.
    /// </para>
    /// </summary>
    public sealed class MoveNetPoseSource : IPoseSource, IDisposable
    {
        const int Size = 192;                 // model input is 192x192 RGB, int32 0..255
        const float Interval = 1f / 20f;
        /// <summary>A joint whose model score is below this keeps its last position (no wild jumps when an arm is hidden).</summary>
        public const float JointMinScore = 0.15f;
        /// <summary>A weak joint holds its place at most this long (seconds); after that it follows the model again.</summary>
        const float JointHoldMax = 0.5f;
        /// <summary>A pipeline step waiting longer than this (seconds) is dropped and the next sample starts fresh.</summary>
        const float StageTimeout = 1f;
        // MoveNet keypoint order -> our PoseFrame landmarks
        const int KpNose = 0, KpLShoulder = 5, KpRShoulder = 6, KpLElbow = 7, KpRElbow = 8,
                  KpLWrist = 9, KpRWrist = 10, KpLHip = 11, KpRHip = 12;
        static readonly int[] Map9 = { KpNose, KpLShoulder, KpRShoulder, KpLElbow, KpRElbow, KpLWrist, KpRWrist, KpLHip, KpRHip };

        static MoveNetPoseSource shared;
        public static MoveNetPoseSource Shared => shared ?? (shared = new MoveNetPoseSource());

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { shared = null; }

        enum Stage { Idle, WaitPixels, WaitOutput }

        Worker worker;
        RenderTexture rt;
        Texture2D readTex;                    // blocking fallback only
        Tensor<int> inputTensor;              // reused every sample
        Tensor<float> outputTensor;           // owned by the worker; valid until the next Schedule
        readonly int[] input = new int[Size * Size * 3];
        readonly float[] weakSince = new float[PoseFrame.Count];   // when each joint's score dropped below JointMinScore (-1 = strong)

        MoveNetPoseSource()
        {
            for (int i = 0; i < weakSince.Length; i++) weakSince[i] = -1f;
        }
        PoseFrame current = new PoseFrame { p = new Vector3[PoseFrame.Count], confidence = 0f, aspect = 4f / 3f };
        bool ready, failed, haveFrame, asyncReadback;
        float timer, lastSampleTime, prevSampleTime, stageSince;
        Stage stage;
        AsyncGPUReadbackRequest pixelRequest;
        // Crop of the frame being processed (fractions of the camera image) and the frame's aspect.
        float cropW = 1f, cropH = 1f, cropX, cropY, frameAspect = 4f / 3f;

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
                rt.Create();
                inputTensor = new Tensor<int>(new TensorShape(1, Size, Size, 3));
                asyncReadback = SystemInfo.supportsAsyncGPUReadback;
                if (!asyncReadback) readTex = new Texture2D(Size, Size, TextureFormat.RGB24, false);
                stage = Stage.Idle;
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
            if (!WebcamFeed.Running || !EnsureLoaded()) { DrainPipeline(); haveFrame = false; return; }
            timer += dt;
            try
            {
                switch (stage)
                {
                    case Stage.Idle:
                        if (timer < Interval) return;
                        timer = 0f;
                        if (!Capture(WebcamFeed.Texture)) return;
                        if (asyncReadback)
                        {
                            pixelRequest = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
                            Enter(Stage.WaitPixels);
                        }
                        else InferBlocking();
                        break;

                    case Stage.WaitPixels:
                        if (!pixelRequest.done)
                        {
                            if (Time.unscaledTime - stageSince > StageTimeout) Enter(Stage.Idle);
                            return;
                        }
                        if (pixelRequest.hasError) { Enter(Stage.Idle); return; }
                        var px = pixelRequest.GetData<Color32>();
                        if (px.Length < Size * Size) { Enter(Stage.Idle); return; }
                        FillInput(px);
                        ScheduleModel();
                        outputTensor.ReadbackRequest();
                        Enter(Stage.WaitOutput);
                        break;

                    case Stage.WaitOutput:
                        if (!outputTensor.IsReadbackRequestDone() && Time.unscaledTime - stageSince <= StageTimeout) return;
                        Apply(outputTensor.DownloadToArray());   // no wait once the readback is done
                        Enter(Stage.Idle);
                        break;
                }
            }
            catch (Exception e)
            {
                failed = true; ready = false; stage = Stage.Idle; Error = e.Message;
                Debug.LogWarning("ChibiClash: pose inference failed: " + e.Message);
            }
        }

        /// <summary>
        /// Finish or drop the sample in flight before the pipeline goes idle (camera stopped, Dispose): the worker's output
        /// tensor must not be rescheduled while its readback is pending. Rare path, so it may wait for the GPU.
        /// </summary>
        void DrainPipeline()
        {
            try
            {
                if (stage == Stage.WaitPixels) pixelRequest.WaitForCompletion();
                else if (stage == Stage.WaitOutput && outputTensor != null) outputTensor.DownloadToArray();   // completes the readback
            }
            catch (Exception) { /* dropped sample */ }
            stage = Stage.Idle;
        }

        void Enter(Stage s)
        {
            stage = s;
            stageSince = Time.unscaledTime;
        }

        /// <summary>Draw the centred square crop of the camera image into the model texture (GPU only, no wait).</summary>
        bool Capture(WebCamTexture cam)
        {
            float w = cam.width, h = cam.height;
            if (w <= 16f || h <= 16f) return false;
            float side = Mathf.Min(w, h);
            cropW = side / w; cropH = side / h;           // centred square crop, as fractions of the frame
            cropX = (1f - cropW) * 0.5f; cropY = (1f - cropH) * 0.5f;
            frameAspect = w / h;
            var scale = new Vector2(cropW, cropH);
            var offset = new Vector2(cropX, cropY);
            if (cam.videoVerticallyMirrored) { scale.y = -scale.y; offset.y = 1f - cropY; }
            Graphics.Blit(cam, rt, scale, offset);
            return true;
        }

        /// <summary>Pixels arrive bottom row first; the model wants the top row first, RGB as int 0..255.</summary>
        void FillInput(NativeArray<Color32> px)
        {
            int k = 0;
            for (int y = Size - 1; y >= 0; y--)
            {
                int row = y * Size;
                for (int x = 0; x < Size; x++)
                {
                    var c = px[row + x];
                    input[k++] = c.r; input[k++] = c.g; input[k++] = c.b;
                }
            }
        }

        void ScheduleModel()
        {
            inputTensor.Upload(input);
            worker.Schedule(inputTensor);
            outputTensor = worker.PeekOutput() as Tensor<float>;   // [1,1,17,3] = (y, x, score)
        }

        /// <summary>WebGL and other platforms without async readback: the old one-frame path (waits for the GPU).</summary>
        void InferBlocking()
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            readTex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);
            RenderTexture.active = prev;
            var px = readTex.GetPixels32();
            int k = 0;
            for (int y = Size - 1; y >= 0; y--)
                for (int x = 0; x < Size; x++)
                {
                    var c = px[y * Size + x];
                    input[k++] = c.r; input[k++] = c.g; input[k++] = c.b;
                }
            ScheduleModel();
            Apply(outputTensor.DownloadToArray());
        }

        /// <summary>Map the 17 keypoints into our mirrored, y-up PoseFrame (smoothed; weak joints hold their place).</summary>
        void Apply(float[] kp)
        {
            if (kp == null || kp.Length < 17 * 3) return;
            const float smooth = 0.65f;
            bool first = !haveFrame;
            for (int i = 0; i < PoseFrame.Count; i++)
            {
                int m = Map9[i];
                float yy = kp[m * 3], xx = kp[m * 3 + 1], score = kp[m * 3 + 2];
                if (score >= JointMinScore) weakSince[i] = -1f;
                else if (!first)
                {
                    // Hidden / unsure joint: keep the last position for a moment (no wild jumps), but never for long, so a
                    // wrist that left the frame cannot hold a pose (block / ult) forever (review 2026-11-26).
                    if (weakSince[i] < 0f) weakSince[i] = Time.unscaledTime;
                    if (Time.unscaledTime - weakSince[i] < JointHoldMax) continue;
                }
                float xf = cropX + xx * cropW;                       // position in the full camera frame
                float yf = cropY + yy * cropH;
                var np = new Vector3(1f - xf, 1f - yf, 0f);         // mirror (so it behaves like a mirror) and flip y up
                current.p[i] = first ? np : Vector3.Lerp(current.p[i], np, smooth);
            }
            current.confidence = (kp[KpNose * 3 + 2] + kp[KpLShoulder * 3 + 2] + kp[KpRShoulder * 3 + 2]) / 3f;
            current.aspect = frameAspect;

            prevSampleTime = lastSampleTime;
            lastSampleTime = Time.unscaledTime;
            SampleDt = haveFrame ? Mathf.Max(lastSampleTime - prevSampleTime, 1e-3f) : Interval;
            Fps = Mathf.Lerp(Fps, 1f / SampleDt, 0.2f);
            haveFrame = true;
            SampleId++;
        }

        public void Dispose()
        {
            DrainPipeline();
            worker?.Dispose(); worker = null;
            inputTensor?.Dispose(); inputTensor = null;
            outputTensor = null;
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

        /// <summary>This tracker reads the camera when it runs: player 1 with the webcam setting on.</summary>
        public bool WantsCamera => playerIndex == 0 && wantCamera();

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
