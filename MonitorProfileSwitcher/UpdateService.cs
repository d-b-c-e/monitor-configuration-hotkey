using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace MonitorProfileSwitcher;

/// <summary>
/// Service for checking and downloading updates from GitHub Releases.
/// Checks on startup with a 24-hour rate limit.
/// </summary>
internal class UpdateService
{
    private const string GitHubApiUrl = "https://api.github.com/repos/d-b-c-e/monitor-configuration-hotkey/releases";
    private const string UserAgent = "MonitorProfileSwitcher-UpdateChecker";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly string LastCheckFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorProfileSwitcher", "last-update-check.txt");

    static UpdateService()
    {
        HttpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        HttpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
    }

    /// <summary>
    /// Returns true if running from a build output directory (skip update checks in dev).
    /// </summary>
    public static bool IsDevBuild()
    {
        var exePath = Environment.ProcessPath ?? "";
        return exePath.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase)
            || exePath.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the current application version from the assembly.
    /// </summary>
    public static Version GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrEmpty(infoVersion))
        {
            // Strip build metadata (+sha) and pre-release suffix for comparison
            var versionPart = infoVersion.Split('+')[0].Split('-')[0];
            if (Version.TryParse(versionPart, out var parsed))
                return parsed;
        }

        return assembly.GetName().Version ?? new Version(0, 0, 1);
    }

    /// <summary>
    /// Returns true if enough time has passed since the last check.
    /// </summary>
    public static bool ShouldCheck()
    {
        if (IsDevBuild()) return false;

        try
        {
            if (!File.Exists(LastCheckFile)) return true;
            var lastCheck = DateTime.Parse(File.ReadAllText(LastCheckFile).Trim());
            return DateTime.UtcNow - lastCheck > CheckInterval;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Records that a check was performed right now.
    /// </summary>
    private static void RecordCheck()
    {
        try
        {
            var dir = Path.GetDirectoryName(LastCheckFile)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(LastCheckFile, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // Non-critical
        }
    }

    /// <summary>
    /// Checks GitHub for a newer release. Returns release info if available, null otherwise.
    /// </summary>
    public static async Task<ReleaseInfo?> CheckForUpdateAsync()
    {
        try
        {
            RecordCheck();

            var response = await HttpClient.GetStringAsync(GitHubApiUrl);
            var releases = JsonSerializer.Deserialize<JsonElement[]>(response);

            if (releases == null || releases.Length == 0)
                return null;

            var currentVersion = GetCurrentVersion();
            ReleaseInfo? best = null;
            Version? bestVersion = null;

            foreach (var release in releases)
            {
                if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                    continue;

                var tagName = release.GetProperty("tag_name").GetString();
                if (string.IsNullOrEmpty(tagName))
                    continue;

                // Parse version from tag (e.g., "v1.2.0" -> "1.2.0")
                var versionStr = tagName.TrimStart('v', 'V').Split('-')[0];
                if (!Version.TryParse(versionStr, out var releaseVersion))
                    continue;

                if (bestVersion != null && releaseVersion <= bestVersion)
                    continue;

                // Find the installer asset
                if (!release.TryGetProperty("assets", out var assets))
                    continue;

                string? downloadUrl = null;
                string? assetName = null;
                long assetSize = 0;

                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        assetName = name;
                        assetSize = asset.GetProperty("size").GetInt64();
                        break;
                    }
                }

                if (downloadUrl == null) continue;

                bestVersion = releaseVersion;
                best = new ReleaseInfo
                {
                    Version = releaseVersion,
                    TagName = tagName!,
                    Name = release.GetProperty("name").GetString() ?? tagName!,
                    Body = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                    DownloadUrl = downloadUrl,
                    AssetName = assetName!,
                    AssetSize = assetSize,
                    HtmlUrl = release.GetProperty("html_url").GetString() ?? ""
                };
            }

            // Only return if newer than current
            if (best != null && bestVersion != null && bestVersion > currentVersion)
                return best;

            return null;
        }
        catch
        {
            // Silently fail — update checks are non-critical
            return null;
        }
    }

    /// <summary>
    /// Downloads the installer to a temp file and returns the path.
    /// </summary>
    public static async Task<string?> DownloadInstallerAsync(ReleaseInfo release)
    {
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), release.AssetName);

            using var response = await HttpClient.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await contentStream.CopyToAsync(fileStream);

            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Launches the downloaded installer and exits the app.
    /// </summary>
    public static void LaunchInstallerAndExit(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true
        });

        Thread.Sleep(500);
        Environment.Exit(0);
    }
}

/// <summary>
/// Information about a GitHub release.
/// </summary>
internal class ReleaseInfo
{
    public required Version Version { get; init; }
    public required string TagName { get; init; }
    public required string Name { get; init; }
    public required string Body { get; init; }
    public required string DownloadUrl { get; init; }
    public required string AssetName { get; init; }
    public required long AssetSize { get; init; }
    public required string HtmlUrl { get; init; }

    public string FormattedSize => AssetSize switch
    {
        < 1024 => $"{AssetSize} B",
        < 1024 * 1024 => $"{AssetSize / 1024.0:F1} KB",
        _ => $"{AssetSize / (1024.0 * 1024.0):F1} MB"
    };
}
