using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using RGDSCapture.Core;
using RGDSCapture.Services;

namespace RGDSCapture.ViewModels
{
    /// <summary>
    /// Line-In passthrough controls: device selection, start/stop,
    /// volume and VU meters. Independent of the SSH connection — audio
    /// arrives over a physical 3.5mm cable, not the network.
    /// </summary>
    public sealed class AudioViewModel : ObservableObject, IDisposable
    {
        private static readonly IBrush VuGreen =
            new ImmutableSolidColorBrush(Color.FromRgb(0x13, 0xA1, 0x0E));
        private static readonly IBrush VuOrange =
            new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
        private static readonly IBrush VuRed =
            new ImmutableSolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));

        private readonly AudioPassthrough _engine = new();
        private readonly DispatcherTimer _vuTimer;
        private readonly Action<string, bool> _log;
        private readonly AppSettings _settings;

        public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = new();
        public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();

        private AudioDeviceInfo? _selectedInput;
        public AudioDeviceInfo? SelectedInput
        {
            get => _selectedInput;
            set => SetProperty(ref _selectedInput, value);
        }

        private AudioDeviceInfo? _selectedOutput;
        public AudioDeviceInfo? SelectedOutput
        {
            get => _selectedOutput;
            set => SetProperty(ref _selectedOutput, value);
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetProperty(ref _isRunning, value))
                    OnPropertyChanged(nameof(ToggleText));
            }
        }

        public string ToggleText => IsRunning ? "■  Audio" : "▶  Audio";

        private double _volume;
        public double Volume
        {
            get => _volume;
            set
            {
                if (SetProperty(ref _volume, value))
                {
                    _engine.Volume = (float)value;
                    _settings.Volume = value;
                    OnPropertyChanged(nameof(VolumeLabel));
                }
            }
        }

        public string VolumeLabel => $"{(int)(Volume * 100)}%";

        private double _leftLevel, _rightLevel;
        public double LeftLevel
        {
            get => _leftLevel;
            private set => SetProperty(ref _leftLevel, value);
        }
        public double RightLevel
        {
            get => _rightLevel;
            private set => SetProperty(ref _rightLevel, value);
        }

        private IBrush _vuBrush = VuGreen;
        public IBrush VuBrush
        {
            get => _vuBrush;
            private set => SetProperty(ref _vuBrush, value);
        }

        public RelayCommand ToggleCommand { get; }

        public AudioViewModel(AppSettings settings, Action<string, bool> log)
        {
            _settings = settings;
            _log = log;
            _volume = Math.Clamp(settings.Volume, 0.0, 1.0);

            _vuTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            _vuTimer.Tick += (_, _) => UpdateVu();

            ToggleCommand = new RelayCommand(Toggle);
            PopulateDevices();
        }

        private void PopulateDevices()
        {
            foreach (var d in AudioPassthrough.GetInputDevices()) InputDevices.Add(d);
            foreach (var d in AudioPassthrough.GetOutputDevices()) OutputDevices.Add(d);

            // Restore by saved name first; otherwise guess the Line-In jack.
            //
            // Monitor sources are excluded from the guess and used only as a
            // last resort: PulseAudio exposes one per sink, they sort early,
            // and defaulting to one would silently record the desktop's own
            // output back into the capture instead of the console.
            SelectedInput =
                InputDevices.FirstOrDefault(d => d.Name == _settings.AudioInputName)
                ?? InputDevices.FirstOrDefault(d => !IsMonitor(d) && LooksLikeLineIn(d))
                ?? InputDevices.FirstOrDefault(d => !IsMonitor(d))
                ?? InputDevices.FirstOrDefault();

            SelectedOutput =
                OutputDevices.FirstOrDefault(d => d.Name == _settings.AudioOutputName)
                ?? OutputDevices.FirstOrDefault();

            if (InputDevices.Count == 0)
                _log("[AUDIO] No input devices found — is PipeWire or PulseAudio running?", true);
        }

        /// <summary>A sink's loopback source (desktop audio), not a real input.</summary>
        private static bool IsMonitor(AudioDeviceInfo d) =>
            d.Id?.EndsWith(".monitor", StringComparison.Ordinal) == true
            || d.Name.Contains("Monitor of", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeLineIn(AudioDeviceInfo d)
        {
            string n = d.Name.ToLowerInvariant();
            string id = d.Id?.ToLowerInvariant() ?? string.Empty;

            // "line" covers both the description ("Line In") and the ALSA port
            // name PulseAudio builds ids from ("analog-input-linein"); "aux"
            // catches the cards that label the same jack differently.
            return n.Contains("line") || id.Contains("line")
                || n.Contains("aux") || id.Contains("aux");
        }

        private void Toggle()
        {
            if (IsRunning) Stop();
            else Start();
        }

        private void Start()
        {
            if (SelectedInput == null)
            {
                _log("[AUDIO] No input device selected.", true);
                return;
            }

            try
            {
                _engine.Start(SelectedInput.Id, SelectedOutput?.Id);
                _engine.Volume = (float)Volume;
                IsRunning = true;
                _vuTimer.Start();

                _settings.AudioInputName = SelectedInput.Name;
                _settings.AudioOutputName = SelectedOutput?.Name;

                _log($"[AUDIO] Line-In started — {SelectedInput.Name} → " +
                     $"{SelectedOutput?.Name ?? "System Default"}", false);
            }
            catch (Exception ex)
            {
                _log($"[AUDIO] Failed to start: {ex.Message}", true);
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _vuTimer.Stop();
            _engine.Stop();
            IsRunning = false;
            LeftLevel = 0;
            RightLevel = 0;
            _log("[AUDIO] Line-In monitoring stopped.", false);
        }

        private void UpdateVu()
        {
            if (!IsRunning) return;

            // RMS levels are perceptually small; ×3 gives a useful meter range.
            LeftLevel = Math.Clamp(_engine.LevelLeft * 3.0, 0.0, 1.0);
            RightLevel = Math.Clamp(_engine.LevelRight * 3.0, 0.0, 1.0);

            float peak = Math.Max(_engine.LevelLeft, _engine.LevelRight);
            VuBrush = peak < 0.6f ? VuGreen : peak < 0.85f ? VuOrange : VuRed;
        }

        public void Dispose()
        {
            _vuTimer.Stop();
            _engine.Dispose();
        }
    }
}
