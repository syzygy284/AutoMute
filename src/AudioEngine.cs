using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AutoMute
{
    public sealed class EngineSnapshot
    {
        public float LevelDb;
        public float HighDb;
        public float DominantHz;
        public bool MutedByUs;
        public float RemainingSeconds;
        public string Device;
        public string Status;
    }

    /// <summary>
    /// Owns all audio work on one dedicated background thread: loopback
    /// capture, analysis, and muting/unmuting. The UI only swaps in new
    /// Settings objects, sets request flags, and reads snapshots.
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        private const int ManualUnmuteGraceMs = 5000;   // don't re-mute right after the user unmutes

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public readonly ConcurrentQueue<string> Events = new ConcurrentQueue<string>();

        private volatile Settings settings;
        private volatile bool stopRequested;
        private volatile bool restartRequested;
        private volatile bool unmuteNowRequested;
        private volatile bool wantSpectrum;
        private volatile bool mutedByUs;
        private volatile float remainingSeconds;
        private volatile string deviceName = "";
        private volatile string status = "Starting...";

        private readonly object displayLock = new object();
        private float peakLevel = -100f;
        private float peakHigh = -100f;
        private float dominant;

        private Thread thread;

        // Only touched on the audio thread:
        private IAudioEndpointVolume volume;
        private LevelAnalyzer analyzer;
        private long unmuteAtMs;
        private long suppressUntilMs;
        private bool silencePublished;
        private float[] fbuf = new float[0];
        private short[] sbuf = new short[0];
        private int[] ibuf = new int[0];
        private byte[] bbuf = new byte[0];

        public AudioEngine(Settings initial)
        {
            settings = initial;
        }

        public void Start()
        {
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "AutoMute audio";
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Apply(Settings s) { settings = s; }
        public void SetWantSpectrum(bool want) { wantSpectrum = want; }
        public void RequestUnmute() { unmuteNowRequested = true; }
        public void RequestRestart() { restartRequested = true; }

        /// <summary>Stops the audio thread. If we muted the device, it is unmuted first.</summary>
        public void Dispose()
        {
            stopRequested = true;
            if (thread != null) thread.Join(3000);
        }

        public EngineSnapshot Snapshot(bool resetPeaks)
        {
            EngineSnapshot s = new EngineSnapshot();
            lock (displayLock)
            {
                s.LevelDb = peakLevel;
                s.HighDb = peakHigh;
                s.DominantHz = dominant;
                if (resetPeaks)
                {
                    peakLevel = -100f;
                    peakHigh = -100f;
                }
            }
            s.MutedByUs = mutedByUs;
            s.RemainingSeconds = remainingSeconds;
            s.Device = deviceName;
            s.Status = status;
            return s;
        }

        private void Log(string msg)
        {
            Events.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "   " + msg);
        }

        private static long NowMs()
        {
            return Clock.ElapsedMilliseconds;
        }

        // ------------------------------------------------------------------
        // Thread main loop: (re)open the default device until asked to stop.
        // ------------------------------------------------------------------
        private void Run()
        {
            string lastError = null;
            while (!stopRequested)
            {
                bool failed = false;
                try
                {
                    RunSession();
                    lastError = null;
                }
                catch (Exception ex)
                {
                    failed = true;
                    string msg = ex.Message;
                    status = "Audio error - retrying: " + msg;
                    if (msg != lastError) Log("Audio error (will retry): " + msg);
                    lastError = msg;
                }
                int waitMs = failed ? 2000 : 300;
                for (int t = 0; t < waitMs && !stopRequested && !(failed && restartRequested); t += 50)
                    Thread.Sleep(50);
            }
            status = "Stopped";
        }

        private void RunSession()
        {
            restartRequested = false;
            IMMDeviceEnumerator enumerator = null;
            DeviceNotifier notifier = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            IntPtr fmtPtr = IntPtr.Zero;
            bool started = false;

            try
            {
                enumerator = CoreAudio.CreateEnumerator();
                notifier = new DeviceNotifier(this);
                enumerator.RegisterEndpointNotificationCallback(notifier);

                try
                {
                    enumerator.GetDefaultAudioEndpoint(CoreAudio.eRender, CoreAudio.eMultimedia, out device);
                }
                catch (COMException)
                {
                    throw new InvalidOperationException("No audio output device found");
                }
                deviceName = CoreAudio.GetFriendlyName(device);

                client = (IAudioClient)CoreAudio.Activate(device, CoreAudio.IID_IAudioClient);
                volume = (IAudioEndpointVolume)CoreAudio.Activate(device, CoreAudio.IID_IAudioEndpointVolume);

                client.GetMixFormat(out fmtPtr);
                MixFormat fmt = MixFormat.Read(fmtPtr);
                // 200 ms buffer (units of 100 ns); we poll far more often than that.
                client.Initialize(CoreAudio.AUDCLNT_SHAREMODE_SHARED, CoreAudio.AUDCLNT_STREAMFLAGS_LOOPBACK,
                                  2000000, 0, fmtPtr, IntPtr.Zero);

                object o;
                Guid iid = CoreAudio.IID_IAudioCaptureClient;
                client.GetService(ref iid, out o);
                capture = (IAudioCaptureClient)o;

                analyzer = new LevelAnalyzer(fmt.SampleRate);
                client.Start();
                started = true;
                status = "Monitoring";
                Log("Listening to " + deviceName + " (" + fmt + ")");

                long lastDataMs = NowMs();
                while (!stopRequested && !restartRequested)
                {
                    Thread.Sleep(5);
                    int packet;
                    capture.GetNextPacketSize(out packet);
                    while (packet > 0)
                    {
                        IntPtr data;
                        int frames, flags;
                        long devPos, qpcPos;
                        capture.GetBuffer(out data, out frames, out flags, out devPos, out qpcPos);
                        try
                        {
                            Process(data, frames, flags, fmt);
                        }
                        finally
                        {
                            capture.ReleaseBuffer(frames);
                        }
                        lastDataMs = NowMs();
                        silencePublished = false;
                        capture.GetNextPacketSize(out packet);
                    }

                    // Windows stops delivering loopback packets when nothing is playing.
                    if (!silencePublished && NowMs() - lastDataMs > 250)
                    {
                        analyzer.Reset();
                        lock (displayLock) dominant = 0f;
                        silencePublished = true;
                    }

                    UpdateMuteTimer();
                }
            }
            finally
            {
                if (mutedByUs && volume != null)
                {
                    try
                    {
                        volume.SetMute(false, IntPtr.Zero);
                        Log(stopRequested ? "Unmuted (AutoMute closing)" : "Unmuted (output device changed)");
                    }
                    catch { }
                }
                mutedByUs = false;
                remainingSeconds = 0f;
                if (started) { try { client.Stop(); } catch { } }
                if (enumerator != null && notifier != null)
                {
                    try { enumerator.UnregisterEndpointNotificationCallback(notifier); } catch { }
                }
                if (fmtPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(fmtPtr);
                CoreAudio.Release(capture);
                CoreAudio.Release(client);
                CoreAudio.Release(volume);
                volume = null;
                CoreAudio.Release(device);
                CoreAudio.Release(enumerator);
                if (restartRequested && !stopRequested)
                {
                    status = "Switching output device...";
                    Log("Default output device changed - switching");
                }
            }
        }

        // ------------------------------------------------------------------
        // Sample conversion + analysis
        // ------------------------------------------------------------------
        private void Process(IntPtr data, int frames, int flags, MixFormat fmt)
        {
            int ch = fmt.Channels;
            int n = frames * ch;
            if (n <= 0 || ch <= 0) return;
            if (fbuf.Length < n) fbuf = new float[n];

            bool silent = (flags & CoreAudio.AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero;
            if (silent)
            {
                Array.Clear(fbuf, 0, n);
            }
            else if (fmt.IsFloat && fmt.BitsPerSample == 32)
            {
                Marshal.Copy(data, fbuf, 0, n);
            }
            else if (!fmt.IsFloat && fmt.BitsPerSample == 16)
            {
                if (sbuf.Length < n) sbuf = new short[n];
                Marshal.Copy(data, sbuf, 0, n);
                for (int i = 0; i < n; i++) fbuf[i] = sbuf[i] / 32768f;
            }
            else if (!fmt.IsFloat && fmt.BitsPerSample == 32)
            {
                if (ibuf.Length < n) ibuf = new int[n];
                Marshal.Copy(data, ibuf, 0, n);
                for (int i = 0; i < n; i++) fbuf[i] = ibuf[i] / 2147483648f;
            }
            else if (!fmt.IsFloat && fmt.BitsPerSample == 24)
            {
                int bytes = n * 3;
                if (bbuf.Length < bytes) bbuf = new byte[bytes];
                Marshal.Copy(data, bbuf, 0, bytes);
                for (int i = 0; i < n; i++)
                {
                    int v = (bbuf[3 * i] << 8) | (bbuf[3 * i + 1] << 16) | (bbuf[3 * i + 2] << 24);
                    fbuf[i] = v / 2147483648f;
                }
            }
            else
            {
                throw new NotSupportedException("Unsupported audio format: " + fmt);
            }

            Settings s = settings;
            bool spectrum = wantSpectrum || s.FreqEnabled;
            for (int f = 0; f < frames; f++)
            {
                double sq = 0, sum = 0;
                int b = f * ch;
                for (int c = 0; c < ch; c++)
                {
                    float v = fbuf[b + c];
                    sq += v * v;
                    sum += v;
                }
                if (analyzer.AddFrame(sq / ch, (float)(sum / ch)))
                {
                    OnBlock(s, spectrum);
                    s = settings;   // pick up live changes between blocks
                }
            }
        }

        private void OnBlock(Settings s, bool spectrum)
        {
            double level = analyzer.LevelDb(s.WindowMs);
            double high = -100.0, dom = 0.0;
            if (spectrum) analyzer.Spectrum(s.FreqCutoffHz, out high, out dom);

            lock (displayLock)
            {
                if (level > peakLevel) peakLevel = (float)level;
                if (high > peakHigh) peakHigh = (float)high;
                dominant = (float)dom;
            }

            if (!s.Enabled || volume == null) return;

            string reason = null;
            if (level >= s.ThresholdDb)
                reason = "loud (" + level.ToString("0.0") + " dBFS)";
            if (s.FreqEnabled && high >= s.FreqThresholdDb)
                reason = (reason == null ? "" : reason + " + ") + "high band (" + high.ToString("0.0") + " dBFS)";
            if (reason != null) Trigger(s, reason);
        }

        // ------------------------------------------------------------------
        // Mute control (audio thread only)
        // ------------------------------------------------------------------
        private void Trigger(Settings s, string reason)
        {
            long now = NowMs();
            long duration = (long)(s.MuteSeconds * 1000.0);
            if (mutedByUs)
            {
                if (s.ExtendWhileLoud && now + duration > unmuteAtMs) unmuteAtMs = now + duration;
                return;
            }
            if (now < suppressUntilMs) return;

            bool alreadyMuted;
            volume.GetMute(out alreadyMuted);
            if (alreadyMuted) return;   // the user muted it themselves - leave it alone

            volume.SetMute(true, IntPtr.Zero);
            mutedByUs = true;
            unmuteAtMs = now + duration;
            remainingSeconds = duration / 1000f;
            Log("Muted: " + reason);
        }

        private void UpdateMuteTimer()
        {
            if (!mutedByUs || volume == null)
            {
                unmuteNowRequested = false;
                remainingSeconds = 0f;
                return;
            }

            long now = NowMs();
            bool stillMuted;
            volume.GetMute(out stillMuted);
            if (!stillMuted)
            {
                mutedByUs = false;
                remainingSeconds = 0f;
                suppressUntilMs = now + ManualUnmuteGraceMs;
                Log("Unmuted by you - pausing triggers for " + (ManualUnmuteGraceMs / 1000) + " s");
                return;
            }

            Settings s = settings;
            if (unmuteNowRequested || !s.Enabled || now >= unmuteAtMs)
            {
                volume.SetMute(false, IntPtr.Zero);
                mutedByUs = false;
                remainingSeconds = 0f;
                if (unmuteNowRequested)
                {
                    suppressUntilMs = now + ManualUnmuteGraceMs;
                    Log("Unmuted on request - pausing triggers for " + (ManualUnmuteGraceMs / 1000) + " s");
                }
                else if (!s.Enabled)
                {
                    Log("Unmuted (monitoring paused)");
                }
                else
                {
                    Log("Unmuted automatically");
                }
                unmuteNowRequested = false;
                return;
            }
            remainingSeconds = (unmuteAtMs - now) / 1000f;
        }
    }
}
