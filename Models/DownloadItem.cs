using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Browse.Models
{
    /// <summary>One row in the download progress strip at the bottom of the window.</summary>
    public sealed class DownloadItem(string fileName, Action cancel) : INotifyPropertyChanged
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _received, _total;
        private bool _isDone, _isClosing;
        private string _status = "Downloading";

        public string FileName { get; } = fileName;
        public string Status { get => _status; private set => Set(ref _status, value); }

        public bool IsDone
        {
            get => _isDone;
            private set { if (Set(ref _isDone, value)) { OnChanged(nameof(IsRunning)); OnChanged(nameof(EtaText)); OnChanged(nameof(SpeedText)); } }
        }
        public bool IsRunning => !_isDone;

        // Set just before removal so the row can slide out first.
        public bool IsClosing { get => _isClosing; set => Set(ref _isClosing, value); }

        public void Cancel() => cancel();

        private long _lastBytes, _lastMs;
        private double _speed; // bytes per second, smoothed

        public void Update(long received, long total)
        {
            _received = received;
            _total = total;

            // Speed: bytes since the last sample over the time since then, sampled at most every half second and smoothed
            // (30% new, 70% old) so the number doesn't jump with every burst.
            long now = _clock.ElapsedMilliseconds, dt = now - _lastMs;
            if (dt >= 500)
            {
                double instant = (received - _lastBytes) * 1000.0 / dt;
                _speed = _speed <= 0 ? instant : _speed * 0.7 + instant * 0.3;
                _lastBytes = received;
                _lastMs = now;
            }
            OnChanged(nameof(SpeedText));

            OnChanged(nameof(Fraction));
            OnChanged(nameof(PercentText));
            OnChanged(nameof(SizeText));
            OnChanged(nameof(EtaText));
        }

        public void Complete()
        {
            Status = "Downloaded";
            if (_total > 0) _received = _total;
            IsDone = true;
            Update(_received, _total);
        }

        public void Fail(string reason)
        {
            Status = reason;
            IsDone = true;
        }

        // 0..1; stays 0 when the server did not announce a size.
        public double Fraction => _total > 0 ? Math.Clamp((double)_received / _total, 0, 1) : 0;

        public string SpeedText => _isDone || _speed <= 0 ? "" : $"{FormatSize((long)_speed)}/s";

        public string PercentText => _total > 0 ? $"{Fraction:P0}" : "";
        public string SizeText => _total > 0 ? $"{FormatSize(_received)}/{FormatSize(_total)}" : FormatSize(_received);

        // Remaining time extrapolated from the average rate so far; blank until the estimate means something.
        public string EtaText
        {
            get
            {
                double f = Fraction;
                var elapsed = _clock.Elapsed;
                if (_isDone || f >= 1 || f < 0.01 || elapsed.TotalSeconds < 2) return "";
                var left = TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - f) / f);
                return left.TotalHours >= 1
                    ? $"~{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}"
                    : $"~{left.Minutes}:{left.Seconds:00}";
            }
        }

        private static string FormatSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes}B" : $"{v:0.#}{units[u]}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new(name));

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnChanged(name!);
            return true;
        }
    }
}
