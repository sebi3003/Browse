using Velopack;
using Velopack.Sources;

namespace Browse.Services
{
    public sealed class UpdateService(string repoUrl, string? accessToken)
    {
        private readonly UpdateManager _mgr = new(new GithubSource(repoUrl, accessToken, prerelease: false));
        private UpdateInfo? _pending;

        /// <summary>Checks for and downloads a newer release. Returns true once one is ready to apply.</summary>
        public async Task<bool> CheckAsync()
        {
            if (!_mgr.IsInstalled) return false;
            if (_pending == null)
            {
                var info = await _mgr.CheckForUpdatesAsync();
                if (info == null) return false;
                await _mgr.DownloadUpdatesAsync(info);
                _pending = info;
            }
            return true;
        }

        public void ApplyAndRestart() { if (_pending != null) _mgr.ApplyUpdatesAndRestart(_pending.TargetFullRelease); }
    }
}
