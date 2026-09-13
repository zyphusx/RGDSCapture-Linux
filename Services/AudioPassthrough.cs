namespace RGDSCapture.Services
{
    /// <summary>
    /// Low-latency Line-In passthrough: capture thread → ring buffer →
    /// playback thread, both talking to PulseAudio (and so to PipeWire).
    ///
    /// Audio stays 48 kHz 16-bit stereo PCM end to end (no format
    /// conversion). Volume is applied in-place on the capture thread. Drift
    /// correction keeps the buffer near the 80 ms target: too full → drop
    /// oldest, too empty → insert silence. Prevents both latency creep and
    /// starve-clicks.
    ///
    /// The Windows build gets its cadence from WMME calling back with each
    /// captured buffer. There is no callback here — pa_simple is a blocking
    /// API — so capture and playback each own a thread and the ring buffer
    /// between them is what decouples the two clocks. The correction logic is
    /// unchanged; only what drives it differs.
    /// </summary>
    public sealed class AudioPassthrough : IDisposable
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const int BytesPerFrame = Channels * 2;   // 16-bit
        private const int BytesPerMs = SampleRate * BytesPerFrame / 1000;

        private const int CaptureBufMs = 20;   // one capture fragment
        private const int TargetFillMs = 80;   // ideal buffer ahead of playback
        private const int MaxFillMs = 160;     // above this → drop oldest
        private const int MinFillMs = 20;      // below this → insert silence
        private const int RingBufMs = 2000;    // total ring capacity

        private const int CaptureChunk = CaptureBufMs * BytesPerMs;

        public bool IsRunning { get; private set; }

        private float _volume = 0.85f;
        public float Volume
        {
            get => _volume;
            set => _volume = Math.Clamp(value, 0f, 1f);
        }

        private float _levelLeft, _levelRight;
        public float LevelLeft => Volatile.Read(ref _levelLeft);
        public float LevelRight => Volatile.Read(ref _levelRight);

        private PulseAudio.Stream? _input;
        private PulseAudio.Stream? _output;
        private RingBuffer? _ring;
        private CancellationTokenSource? _cts;
        private Thread? _captureThread;
        private Thread? _playbackThread;

        private readonly int _targetFillBytes = TargetFillMs * BytesPerMs;
        private readonly int _maxFillBytes = MaxFillMs * BytesPerMs;
        private readonly int _minFillBytes = MinFillMs * BytesPerMs;

        // ── Device enumeration ────────────────────────────────────────
        public static List<AudioDeviceInfo> GetInputDevices() => PulseAudio.Sources();

        public static List<AudioDeviceInfo> GetOutputDevices()
        {
            // A null id follows whatever the user picked in their desktop's
            // sound settings, which is the right default for monitoring.
            var list = new List<AudioDeviceInfo> { new(null, "System Default") };
            list.AddRange(PulseAudio.Sinks());
            return list;
        }

        // ─────────────────────────────────────────────────────────────
        /// <param name="inputDevice">PulseAudio source name, or null for the default.</param>
        /// <param name="outputDevice">PulseAudio sink name, or null for the default.</param>
        public void Start(string? inputDevice, string? outputDevice)
        {
            if (IsRunning) return;

            _ring = new RingBuffer(RingBufMs * BytesPerMs);
            _ring.PrimeSilence(_targetFillBytes);

            var spec = new PulseAudio.SampleSpec
            {
                Format = PulseAudio.SampleS16Le,
                Rate = SampleRate,
                Channels = Channels
            };

            try
            {
                // Capture: fragsize is the only field the record path reads.
                _input = PulseAudio.Stream.Open(
                    record: true, inputDevice, spec,
                    new PulseAudio.BufferAttr
                    {
                        MaxLength = PulseAudio.BufferAttr.Unset,
                        TargetLength = PulseAudio.BufferAttr.Unset,
                        PreBuffer = PulseAudio.BufferAttr.Unset,
                        MinRequest = PulseAudio.BufferAttr.Unset,
                        FragmentSize = (uint)CaptureChunk
                    },
                    "Line-In capture");

                // Playback: tlength sets the server-side latency target.
                // prebuf 0 means "start immediately" — the ring is already
                // primed, so waiting for the server to pre-buffer as well
                // would just add latency on top of ours.
                _output = PulseAudio.Stream.Open(
                    record: false, outputDevice, spec,
                    new PulseAudio.BufferAttr
                    {
                        MaxLength = PulseAudio.BufferAttr.Unset,
                        TargetLength = (uint)(TargetFillMs * BytesPerMs),
                        PreBuffer = 0,
                        MinRequest = (uint)CaptureChunk,
                        FragmentSize = PulseAudio.BufferAttr.Unset
                    },
                    "Line-In monitor");
            }
            catch
            {
                // Roll back partial initialization so a failed Start doesn't
                // leak an open device.
                TearDown();
                throw;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _captureThread = StartThread("RGDS audio capture", () => CaptureLoop(token));
            _playbackThread = StartThread("RGDS audio playback", () => PlaybackLoop(token));

            IsRunning = true;
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            TearDown();
        }

        public void Dispose() => Stop();

        private static Thread StartThread(string name, Action body)
        {
            // Above-normal priority for the same reason the Windows build
            // raises the process class: a scheduling delay longer than the
            // buffer is an audible dropout.
            var thread = new Thread(() => body())
            {
                IsBackground = true,
                Name = name,
                Priority = ThreadPriority.AboveNormal
            };
            thread.Start();
            return thread;
        }

        private void TearDown()
        {
            _cts?.Cancel();

            // Closing the streams unblocks whichever thread is parked inside
            // a blocking read or write.
            _input?.Dispose();
            _output?.Dispose();

            _captureThread?.Join(500);
            _playbackThread?.Join(500);

            _cts?.Dispose();
            _cts = null;
            _captureThread = null;
            _playbackThread = null;
            _input = null;
            _output = null;
            _ring = null;

            Volatile.Write(ref _levelLeft, 0f);
            Volatile.Write(ref _levelRight, 0f);
        }

        // ── Capture ───────────────────────────────────────────────────
        private void CaptureLoop(CancellationToken token)
        {
            var buffer = new byte[CaptureChunk];
            var input = _input;
            var ring = _ring;
            if (input is null || ring is null) return;

            while (!token.IsCancellationRequested)
            {
                if (!input.Read(buffer, buffer.Length)) break;

                float vol = _volume;
                if (Math.Abs(vol - 1.0f) > 0.001f)
                    ApplyVolume(buffer, buffer.Length, vol);

                int fill = ring.BufferedBytes;
                if (fill > _maxFillBytes)
                    ring.DropOldest(fill - _targetFillBytes);
                else if (fill < _minFillBytes)
                    ring.InsertSilence(_targetFillBytes - fill);

                ring.Write(buffer, 0, buffer.Length);
                ComputeLevels(buffer, buffer.Length);
            }
        }

        // ── Playback ──────────────────────────────────────────────────
        private void PlaybackLoop(CancellationToken token)
        {
            var buffer = new byte[CaptureChunk];
            var output = _output;
            var ring = _ring;
            if (output is null || ring is null) return;

            while (!token.IsCancellationRequested)
            {
                // Read always returns a full buffer, padding with silence
                // rather than starving the device.
                ring.Read(buffer, 0, buffer.Length);
                if (!output.Write(buffer, buffer.Length)) break;
            }
        }

        /// <summary>Scale 16-bit PCM samples in place, clamped to prevent wrap.</summary>
        private static void ApplyVolume(byte[] buf, int count, float vol)
        {
            for (int i = 0; i < count - 1; i += 2)
            {
                short s = (short)(buf[i] | (buf[i + 1] << 8));
                int v = Math.Clamp((int)(s * vol), short.MinValue, short.MaxValue);
                buf[i] = (byte)(v & 0xFF);
                buf[i + 1] = (byte)((v >> 8) & 0xFF);
            }
        }

        /// <summary>Per-channel RMS, written lock-free for UI reads.</summary>
        private void ComputeLevels(byte[] buf, int count)
        {
            if (count < 4) return;
            double sumL = 0, sumR = 0;
            int pairs = count / 4;
            for (int i = 0; i < count - 3; i += 4)
            {
                short sL = (short)(buf[i] | (buf[i + 1] << 8));
                short sR = (short)(buf[i + 2] | (buf[i + 3] << 8));
                sumL += (double)sL * sL;
                sumR += (double)sR * sR;
            }
            if (pairs > 0)
            {
                Volatile.Write(ref _levelLeft, (float)Math.Sqrt(sumL / pairs) / 32768f);
                Volatile.Write(ref _levelRight, (float)Math.Sqrt(sumR / pairs) / 32768f);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Single-writer / single-reader ring buffer sitting between the capture
    /// and playback threads.
    /// </summary>
    internal sealed class RingBuffer
    {
        private readonly byte[] _buf;
        private readonly int _capacity;
        private readonly object _lock = new();
        private int _writePos, _readPos;

        public RingBuffer(int capacityBytes)
        {
            _capacity = capacityBytes;
            _buf = new byte[capacityBytes];
        }

        public int BufferedBytes
        {
            get
            {
                lock (_lock)
                    return (_writePos - _readPos + _capacity) % _capacity;
            }
        }

        public void PrimeSilence(int bytes)
        {
            lock (_lock)
            {
                bytes = Math.Min(bytes, _capacity - 1);
                _writePos = (_writePos + bytes) % _capacity;
            }
        }

        public void Write(byte[] data, int offset, int count)
        {
            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                    _buf[(_writePos + i) % _capacity] = data[offset + i];
                _writePos = (_writePos + count) % _capacity;
            }
        }

        public void InsertSilence(int bytes)
        {
            lock (_lock)
            {
                int buffered = (_writePos - _readPos + _capacity) % _capacity;
                int space = _capacity - buffered - 1;
                bytes = Math.Min(bytes, space);
                if (bytes <= 0) return;
                for (int i = 0; i < bytes; i++)
                    _buf[(_writePos + i) % _capacity] = 0;
                _writePos = (_writePos + bytes) % _capacity;
            }
        }

        public void DropOldest(int bytes)
        {
            lock (_lock)
            {
                int buffered = (_writePos - _readPos + _capacity) % _capacity;
                bytes = Math.Min(bytes, buffered);
                _readPos = (_readPos + bytes) % _capacity;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                int avail = (_writePos - _readPos + _capacity) % _capacity;
                int toRead = Math.Min(count, avail);

                for (int i = 0; i < toRead; i++)
                    buffer[offset + i] = _buf[(_readPos + i) % _capacity];
                _readPos = (_readPos + toRead) % _capacity;

                // Pad shortfall with silence rather than starving the device.
                if (toRead < count)
                    Array.Clear(buffer, offset + toRead, count - toRead);

                return count;
            }
        }
    }
}
