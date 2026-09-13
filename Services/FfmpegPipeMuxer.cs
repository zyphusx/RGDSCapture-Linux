using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RGDSCapture.Services
{
    /// <summary>One ffmpeg input: its demuxer arguments and a start offset.</summary>
    /// <param name="FormatArgs">Input options placed before -i (e.g. "-f h264 -framerate 30").</param>
    /// <param name="OffsetSeconds">-itsoffset applied to this input for track alignment.</param>
    public sealed record MuxInput(string FormatArgs, double OffsetSeconds);

    /// <summary>Shared ffmpeg argument fragments.</summary>
    public static class FfmpegArgs
    {
        /// <summary>
        /// setts bitstream filter forcing exact 30 fps CFR timestamps on a
        /// video output stream, with an optional alignment offset folded in.
        /// Necessary because the raw h264 demuxer trusts SPS VUI timing when
        /// present, which can mis-time the stream regardless of -framerate.
        /// Note setts overrides -itsoffset, so the offset must live inside the
        /// expression.
        /// </summary>
        public static string CfrSetts(int videoStreamIndex, double offsetSeconds)
        {
            string expr = "N/(30*TB)";
            if (offsetSeconds > 0.0005)
                expr += "+" + offsetSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "/TB";
            return $"-bsf:v:{videoStreamIndex} setts=ts={expr} ";
        }
    }

    /// <summary>
    /// Runs one ffmpeg process with N inputs fed through FIFOs. stdin can only
    /// carry a single stream, so multi-track output (two video tracks + audio
    /// in one MP4) requires a pipe per input. Each input gets a bounded queue
    /// drained by its own writer task, so producers never block the
    /// receive/decode threads.
    ///
    /// The Windows build uses named pipes for this. A .NET NamedPipeServerStream
    /// on Linux is a Unix domain socket, which ffmpeg cannot open as an input
    /// file, so real FIFOs are used instead. That difference is not only
    /// mechanical: closing a Windows pipe discards whatever the client has not
    /// read yet, which is why the Windows code has to drain explicitly before
    /// closing. A FIFO keeps its buffered bytes readable after the writer
    /// closes, so here the close itself is the clean end-of-stream.
    /// </summary>
    public sealed class FfmpegPipeMuxer : IDisposable
    {
        /// <summary>Raised (background thread) if ffmpeg exits before Complete.</summary>
        public event Action? Failed;

        public string OutputFile { get; }

        private sealed class Feed
        {
            public string Path = string.Empty;
            public readonly BlockingCollection<byte[]> Queue = new(boundedCapacity: 8192);
            public Task Writer = Task.CompletedTask;
        }

        private readonly Process _proc;
        private readonly Feed[] _feeds;
        private readonly string _fifoDir;
        private readonly CancellationTokenSource _connectCts = new();
        private volatile bool _stopping;

        public FfmpegPipeMuxer(
            string outputFile,
            IReadOnlyList<MuxInput> inputs,
            string outputArgs,
            Action<string, bool> log)
        {
            OutputFile = outputFile;
            _feeds = new Feed[inputs.Count];

            // XDG_RUNTIME_DIR is a tmpfs private to the user, which is the
            // right home for transient FIFOs; /tmp is the fallback for a
            // session that has none (a bare ssh login, say).
            string runtimeRoot = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
                is { Length: > 0 } r && Directory.Exists(r) ? r : "/tmp";

            _fifoDir = Path.Combine(runtimeRoot, $"rgdscapture-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_fifoDir);

            var args = new StringBuilder("-hide_banner -loglevel error ");
            for (int i = 0; i < inputs.Count; i++)
            {
                _feeds[i] = new Feed { Path = Path.Combine(_fifoDir, $"in{i}") };
                Fifo.Create(_feeds[i].Path);

                if (inputs[i].OffsetSeconds > 0.0005)
                    args.Append("-itsoffset ")
                        .Append(inputs[i].OffsetSeconds.ToString("0.###", CultureInfo.InvariantCulture))
                        .Append(' ');
                args.Append(inputs[i].FormatArgs)
                    .Append(" -i \"").Append(_feeds[i].Path).Append("\" ");
            }
            args.Append(outputArgs).Append(" -y \"").Append(outputFile).Append('"');

            var psi = new ProcessStartInfo
            {
                FileName = Core.AppPaths.FfmpegExe,
                Arguments = args.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            try
            {
                _proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start ffmpeg.");
            }
            catch
            {
                CleanupFifos();
                throw;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    string? line;
                    while ((line = _proc.StandardError.ReadLine()) != null)
                        log($"[MUX] ffmpeg: {line}", true);
                }
                catch { }
            });

            _proc.EnableRaisingEvents = true;
            _proc.Exited += (_, _) =>
            {
                // Unblock writers still waiting for ffmpeg to open their FIFO.
                try { _connectCts.Cancel(); } catch { }
                if (!_stopping) Failed?.Invoke();
            };

            foreach (var feed in _feeds)
            {
                var f = feed;
                f.Writer = Task.Run(() =>
                {
                    FileStream? stream = null;
                    try
                    {
                        stream = Fifo.OpenWrite(f.Path, _connectCts.Token);
                        foreach (var chunk in f.Queue.GetConsumingEnumerable())
                            stream.Write(chunk, 0, chunk.Length);
                        stream.Flush();
                    }
                    catch
                    {
                        // Cancelled open (ffmpeg died) or broken pipe; the
                        // Exited handler reports the failure.
                    }
                    finally
                    {
                        // Closing the write end is ffmpeg's end-of-stream.
                        // Anything already in the FIFO stays readable.
                        try { stream?.Dispose(); } catch { }
                    }
                });
            }
        }

        /// <summary>Non-blocking enqueue for live capture paths; drops under backpressure.</summary>
        public bool TryWrite(int input, byte[] data)
            => !_feeds[input].Queue.IsAddingCompleted && _feeds[input].Queue.TryAdd(data);

        /// <summary>Blocking enqueue for bulk writes from background tasks.</summary>
        public void Write(int input, byte[] data)
        {
            try { _feeds[input].Queue.Add(data); }
            catch (InvalidOperationException) { }   // completed during shutdown
        }

        /// <summary>
        /// Closes all inputs, lets ffmpeg finalize the MP4 and waits for exit.
        /// Returns true if ffmpeg exited cleanly.
        /// </summary>
        public async Task<bool> CompleteAsync(int timeoutMs)
        {
            _stopping = true;
            foreach (var f in _feeds) f.Queue.CompleteAdding();
            await Task.WhenAll(_feeds.Select(f => f.Writer));

            bool clean = await Task.Run(() =>
            {
                if (!_proc.WaitForExit(timeoutMs))
                {
                    try
                    {
                        _proc.Kill(entireProcessTree: true);
                        _proc.WaitForExit(2000);
                    }
                    catch { }
                    return false;
                }
                return _proc.ExitCode == 0;
            });

            CleanupFifos();
            return clean;
        }

        public void Dispose()
        {
            _stopping = true;
            try { _connectCts.Cancel(); } catch { }
            foreach (var f in _feeds)
            {
                try { f.Queue.CompleteAdding(); } catch { }
            }
            try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
            _proc.Dispose();
            foreach (var f in _feeds) f.Queue.Dispose();
            _connectCts.Dispose();
            CleanupFifos();
        }

        private void CleanupFifos()
        {
            try
            {
                if (Directory.Exists(_fifoDir)) Directory.Delete(_fifoDir, recursive: true);
            }
            catch
            {
                // A leftover FIFO in a tmpfs is harmless; it goes at logout.
            }
        }
    }

    /// <summary>
    /// FIFO creation and a cancellable open, neither of which the BCL exposes.
    /// </summary>
    internal static class Fifo
    {
        private const int OWronly = 0x0001;
        private const int ONonblock = 0x0800;
        private const int FGetfl = 3;
        private const int FSetfl = 4;

        /// <summary>ENXIO — a write-only open of a FIFO that no reader has opened yet.</summary>
        private const int Enxio = 6;

        [DllImport("libc", SetLastError = true)]
        private static extern int mkfifo(string pathname, uint mode);

        [DllImport("libc", SetLastError = true, EntryPoint = "open")]
        private static extern int sys_open(string pathname, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int fcntl(int fd, int cmd, int arg);

        /// <summary>Creates a FIFO readable and writable only by the current user.</summary>
        internal static void Create(string path)
        {
            if (mkfifo(path, 0b110_000_000) != 0)   // 0600
                throw new IOException(
                    $"mkfifo({path}) failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }

        /// <summary>
        /// Opens the write end, waiting for ffmpeg to open the read end.
        ///
        /// A blocking write-only open of a FIFO parks in the kernel until a
        /// reader arrives and cannot be interrupted, which would strand the
        /// writer task if ffmpeg died on startup. Opening non-blocking instead
        /// fails fast with ENXIO while there is no reader, so the wait becomes
        /// an ordinary cancellable poll; O_NONBLOCK is then cleared so writes
        /// behave normally once the pipe is up.
        /// </summary>
        internal static FileStream OpenWrite(string path, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                int fd = sys_open(path, OWronly | ONonblock);
                if (fd >= 0)
                {
                    int flags = fcntl(fd, FGetfl, 0);
                    if (flags >= 0) fcntl(fd, FSetfl, flags & ~ONonblock);

                    return new FileStream(
                        new SafeFileHandle((IntPtr)fd, ownsHandle: true),
                        FileAccess.Write,
                        bufferSize: 1 << 16);
                }

                if (Marshal.GetLastPInvokeError() != Enxio)
                    throw new IOException(
                        $"open({path}) failed: {Marshal.GetLastPInvokeErrorMessage()}");

                token.WaitHandle.WaitOne(20);
            }
        }
    }
}
