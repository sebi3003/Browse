namespace Browse.Services
{
    public static class AppConfig
    {
        // GitHub repo used as the Velopack release feed. The repo is private, so installed copies read Releases through the
        // GitHub API with a read-only token that ships inside the exe. The token itself lives in the gitignored
        // AppConfig.Secrets.cs, which the shared Scripts\release.ps1 writes from GIT_TOKEN.txt (the "Embedded" entry) -
        // never hardcode it here. A fresh clone needs that file once (an empty-token stub is fine) to build.
        // Where the bookmark sync file is pre-filled to live (menu: Bookmark sync). The folder is shared through OneDrive between PCs.
        public const string DefaultSyncFile = @"C:\Users\sebastian\OneDrive\Install Kits\2026\Browser_Bookmarks\Browse bookmarks.json";

        public static string GitHubRepoUrl { get; } = "https://github.com/sebi3003/Browse";
        public static string GitHubAccessToken { get; } = AppSecrets.GitHubAccessToken;
    }
}
