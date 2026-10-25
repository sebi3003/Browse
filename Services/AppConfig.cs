using System.Reflection;

namespace Browse.Services
{
    public static class AppConfig
    {
        // GitHub repo used as the Velopack release feed (see scripts/release.ps1). The repo is private,
        // so installed copies carry a read-only fine-grained token (Contents: Read, this repo only),
        // injected at publish time as assembly metadata. Never use a write-capable token here.
        public const string GitHubRepoUrl = "https://github.com/sebi3003/Browse";

        public static string? GitHubAccessToken { get; } = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitHubToken")?.Value is { Length: > 0 } t ? t : null;
    }
}
