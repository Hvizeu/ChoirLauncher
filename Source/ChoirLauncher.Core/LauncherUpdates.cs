using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChoirLauncher.Core;

public enum LauncherUpdateChannel
{
    Stable,
    Preview
}

public enum LauncherUpdateStatus
{
    Skipped,
    NotModified,
    UpToDate,
    UpdateAvailable,
    UpdateAvailableWithoutPackage,
    Failed
}

public enum LauncherUpdatePackageKind
{
    WindowsSetup,
    WindowsPortable,
    LinuxPortable,
    MacOSAppBundle
}

public sealed record LauncherUpdatePreferences(
    int SchemaVersion = 1,
    bool CheckOnStartup = true,
    LauncherUpdateChannel Channel = LauncherUpdateChannel.Preview,
    DateTimeOffset? LastCheckedUtc = null,
    string? StableETag = null,
    string? PreviewETag = null);

public sealed record GitHubReleaseAssetInfo(
    string Name,
    string DownloadUrl,
    long Size,
    string? Digest);

public sealed record GitHubReleaseInfo(
    string TagName,
    string Name,
    string HtmlUrl,
    string? Body,
    bool Draft,
    bool Prerelease,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? PublishedUtc,
    IReadOnlyList<GitHubReleaseAssetInfo> Assets);

public sealed record GitHubReleaseListResult(
    bool NotModified,
    string? ETag,
    IReadOnlyList<GitHubReleaseInfo> Releases);

public sealed record LauncherUpdatePackageInfo(
    LauncherUpdatePackageKind Kind,
    string FileName,
    string DownloadUrl,
    long Bytes,
    string? Sha256);

public sealed record LauncherUpdateCandidate(
    string Version,
    string TagName,
    string Name,
    string ReleasePageUrl,
    string? ReleaseNotes,
    DateTimeOffset? PublishedUtc,
    bool GitHubPrerelease,
    LauncherUpdatePackageInfo? Package);

public sealed record LauncherUpdateCheckResult(
    LauncherUpdateStatus Status,
    LauncherUpdateChannel Channel,
    DateTimeOffset CheckedUtc,
    LauncherUpdatePreferences Preferences,
    LauncherUpdateCandidate? Candidate,
    string Message)
{
    public bool ShouldNotify => Status is LauncherUpdateStatus.UpdateAvailable or LauncherUpdateStatus.UpdateAvailableWithoutPackage;
}

public interface IUpdateReleaseClient
{
    Task<GitHubReleaseListResult> ListReleasesAsync(string? eTag, CancellationToken token = default);
}

public sealed class LauncherUpdatePreferencesStore
{
    private readonly string path;

    public LauncherUpdatePreferencesStore(ManagerStoragePaths paths)
    {
        paths.EnsureCreated();
        path = paths.UpdatePreferences;
    }

    public LauncherUpdatePreferences Load()
    {
        try
        {
            if (!File.Exists(path)) return new();
            var loaded = JsonSerializer.Deserialize<LauncherUpdatePreferences>(File.ReadAllText(path, Encoding.UTF8), ManagerJson.Options);
            return loaded is { SchemaVersion: 1 } ? loaded : new();
        }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
    }

    public void Save(LauncherUpdatePreferences preferences) => AtomicFile.WriteValidated(path,
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(preferences with { SchemaVersion = 1 }, ManagerJson.Options) + Environment.NewLine),
        bytes => JsonSerializer.Deserialize<LauncherUpdatePreferences>(bytes, ManagerJson.Options)?.SchemaVersion == 1,
        null, 0);
}

public sealed class GitHubReleaseClient : IUpdateReleaseClient, IDisposable
{
    private const int MaxReleaseListBytes = 1024 * 1024;
    private readonly HttpClient http;
    private readonly bool ownsHttpClient;
    private readonly Uri releasesEndpoint;

    public GitHubReleaseClient(HttpClient? httpClient = null, Uri? releasesEndpoint = null)
    {
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        ownsHttpClient = httpClient is null;
        this.releasesEndpoint = releasesEndpoint ?? new Uri("https://api.github.com/repos/Hvizeu/ChoirLauncher/releases?per_page=20");
    }

    public async Task<GitHubReleaseListResult> ListReleasesAsync(string? eTag, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, releasesEndpoint);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(BuildInfo.ProductName, BuildInfo.Version));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");
        if (!string.IsNullOrWhiteSpace(eTag)) request.Headers.TryAddWithoutValidation("If-None-Match", eTag);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var responseETag = ReadETag(response);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return new(true, responseETag ?? eTag, []);
        if (response.Content.Headers.ContentLength is > MaxReleaseListBytes)
            throw new InvalidDataException("GitHub release response exceeded the update-check size limit.");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(stream, MaxReleaseListBytes, token).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize<GitHubReleaseDto[]>(bytes, ManagerJson.Options) ?? [];
        return new(false, responseETag, releases.Select(ToInfo).ToArray());
    }

    public void Dispose()
    {
        if (ownsHttpClient) http.Dispose();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maxBytes)
                throw new InvalidDataException("GitHub release response exceeded the update-check size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static GitHubReleaseInfo ToInfo(GitHubReleaseDto dto) => new(
        dto.TagName ?? "",
        dto.Name ?? dto.TagName ?? "",
        dto.HtmlUrl ?? "",
        dto.Body,
        dto.Draft,
        dto.Prerelease,
        dto.CreatedAt,
        dto.PublishedAt,
        (dto.Assets ?? []).Select(asset => new GitHubReleaseAssetInfo(
            asset.Name ?? "",
            asset.BrowserDownloadUrl ?? "",
            asset.Size,
            asset.Digest)).ToArray());

    private static string? ReadETag(HttpResponseMessage response)
    {
        if (response.Headers.ETag?.Tag is { Length: > 0 } tag) return tag;
        return response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("assets")] public GitHubReleaseAssetDto[]? Assets { get; set; }
    }

    private sealed class GitHubReleaseAssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}

public sealed class LauncherUpdateService
{
    public static readonly TimeSpan StartupCheckInterval = TimeSpan.FromHours(24);
    private readonly IUpdateReleaseClient client;
    private readonly DesktopPlatform platform;
    private readonly Architecture architecture;

    public LauncherUpdateService(IUpdateReleaseClient? client = null, DesktopPlatform? platform = null, Architecture? architecture = null)
    {
        this.client = client ?? new GitHubReleaseClient();
        this.platform = platform ?? HostPlatform.Current;
        this.architecture = architecture ?? RuntimeInformation.ProcessArchitecture;
    }

    public static bool ShouldCheckOnStartup(LauncherUpdatePreferences preferences, DateTimeOffset now)
    {
        if (!preferences.CheckOnStartup) return false;
        return preferences.LastCheckedUtc is null || now - preferences.LastCheckedUtc.Value >= StartupCheckInterval || now < preferences.LastCheckedUtc.Value;
    }

    public async Task<LauncherUpdateCheckResult> CheckAsync(LauncherUpdatePreferences preferences, bool force, DateTimeOffset now, CancellationToken token = default)
    {
        if (!force && !ShouldCheckOnStartup(preferences, now))
            return new(LauncherUpdateStatus.Skipped, preferences.Channel, now, preferences, null, "Update check skipped; the last check is still recent.");

        var eTag = preferences.Channel == LauncherUpdateChannel.Stable ? preferences.StableETag : preferences.PreviewETag;
        try
        {
            var releases = await client.ListReleasesAsync(eTag, token).ConfigureAwait(false);
            var updatedPreferences = StoreCache(preferences, releases.ETag, now);
            if (releases.NotModified)
                return new(LauncherUpdateStatus.NotModified, preferences.Channel, now, updatedPreferences, null, "No newer ChoirLauncher release was found since the last check.");

            var candidate = SelectCandidate(releases.Releases, preferences.Channel, platform, architecture);
            if (candidate is null)
                return new(LauncherUpdateStatus.UpToDate, preferences.Channel, now, updatedPreferences, null, "ChoirLauncher is up to date.");
            if (candidate.Package is null)
                return new(LauncherUpdateStatus.UpdateAvailableWithoutPackage, preferences.Channel, now, updatedPreferences, candidate,
                    $"ChoirLauncher {candidate.Version} is available, but this release has no package for {DisplayPlatform(platform, architecture)}.");
            return new(LauncherUpdateStatus.UpdateAvailable, preferences.Channel, now, updatedPreferences, candidate,
                $"ChoirLauncher {candidate.Version} is available for {DisplayPlatform(platform, architecture)}.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(LauncherUpdateStatus.Failed, preferences.Channel, now, preferences with { LastCheckedUtc = now }, null, "The update check timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
        {
            return new(LauncherUpdateStatus.Failed, preferences.Channel, now, preferences with { LastCheckedUtc = now }, null, "Update check failed: " + ex.Message);
        }
    }

    public LauncherUpdateCandidate? SelectCandidate(IReadOnlyList<GitHubReleaseInfo> releases, LauncherUpdateChannel channel, DesktopPlatform packagePlatform, Architecture packageArchitecture)
    {
        if (!LauncherUpdateVersion.TryParse(BuildInfo.Version, out var current)) return null;
        return releases
            .Where(release => !release.Draft && !string.IsNullOrWhiteSpace(release.HtmlUrl))
            .Select(release => (release, version: ParseReleaseVersion(release)))
            .Where(item => item.version is not null && item.version.CompareTo(current) > 0)
            .Where(item => channel == LauncherUpdateChannel.Preview || (!item.release.Prerelease && item.version!.PreRelease is null))
            .OrderByDescending(item => item.version)
            .Select(item =>
            {
                var package = LauncherUpdatePackageSelector.Select(item.release.Assets, packagePlatform, packageArchitecture);
                return new LauncherUpdateCandidate(
                    item.version!.Original,
                    item.release.TagName,
                    string.IsNullOrWhiteSpace(item.release.Name) ? item.release.TagName : item.release.Name,
                    item.release.HtmlUrl,
                    item.release.Body,
                    item.release.PublishedUtc ?? item.release.CreatedUtc,
                    item.release.Prerelease,
                    package);
            })
            .FirstOrDefault();
    }

    private static LauncherUpdateVersion? ParseReleaseVersion(GitHubReleaseInfo release)
    {
        if (LauncherUpdateVersion.TryParse(release.TagName, out var tag)) return tag;
        return LauncherUpdateVersion.TryParse(release.Name, out var name) ? name : null;
    }

    private static LauncherUpdatePreferences StoreCache(LauncherUpdatePreferences preferences, string? eTag, DateTimeOffset now) =>
        preferences.Channel == LauncherUpdateChannel.Stable
            ? preferences with { LastCheckedUtc = now, StableETag = eTag ?? preferences.StableETag }
            : preferences with { LastCheckedUtc = now, PreviewETag = eTag ?? preferences.PreviewETag };

    private static string DisplayPlatform(DesktopPlatform platform, Architecture architecture) =>
        platform switch
        {
            DesktopPlatform.Windows => "Windows x64",
            DesktopPlatform.Linux => "Linux x64",
            DesktopPlatform.MacOS when architecture == Architecture.Arm64 => "macOS Apple Silicon",
            DesktopPlatform.MacOS => "macOS Intel",
            _ => platform.ToString()
        };
}

public static class LauncherUpdatePackageSelector
{
    public static LauncherUpdatePackageInfo? Select(IReadOnlyList<GitHubReleaseAssetInfo> assets, DesktopPlatform platform, Architecture architecture)
    {
        var names = PreferredNames(platform, architecture);
        foreach (var preferred in names)
        {
            var asset = assets.FirstOrDefault(candidate => string.Equals(candidate.Name, preferred.Name, StringComparison.Ordinal));
            if (asset is not null && Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps)
                return new(preferred.Kind, asset.Name, asset.DownloadUrl, asset.Size, NormalizeSha256(asset.Digest));
        }
        return null;
    }

    private static IReadOnlyList<(string Name, LauncherUpdatePackageKind Kind)> PreferredNames(DesktopPlatform platform, Architecture architecture) =>
        platform switch
        {
            DesktopPlatform.Windows =>
            [
                ("ChoirLauncher-Setup-win-x64.exe", LauncherUpdatePackageKind.WindowsSetup),
                ("ChoirLauncher-Desktop-win-x64-self-contained.zip", LauncherUpdatePackageKind.WindowsPortable)
            ],
            DesktopPlatform.Linux =>
            [
                ("ChoirLauncher-Desktop-linux-x64-self-contained.tar.gz", LauncherUpdatePackageKind.LinuxPortable)
            ],
            DesktopPlatform.MacOS when architecture == Architecture.Arm64 =>
            [
                ("ChoirLauncher-Desktop-osx-arm64-self-contained.zip", LauncherUpdatePackageKind.MacOSAppBundle)
            ],
            DesktopPlatform.MacOS =>
            [
                ("ChoirLauncher-Desktop-osx-x64-self-contained.zip", LauncherUpdatePackageKind.MacOSAppBundle)
            ],
            _ => []
        };

    private static string? NormalizeSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var value = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : digest;
        return value.Length == 64 && value.All(IsHex) ? value.ToLowerInvariant() : null;
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}

public sealed record LauncherUpdateVersion(int Major, int Minor, int Patch, string? PreRelease, string Original) : IComparable<LauncherUpdateVersion>
{
    public static bool TryParse(string? value, out LauncherUpdateVersion version)
    {
        version = default!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var original = value.Trim();
        var text = original.StartsWith('v') || original.StartsWith('V') ? original[1..] : original;
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) text = text[..plus];
        string? preRelease = null;
        var hyphen = text.IndexOf('-', StringComparison.Ordinal);
        if (hyphen >= 0)
        {
            preRelease = text[(hyphen + 1)..];
            text = text[..hyphen];
            if (string.IsNullOrWhiteSpace(preRelease)) return false;
        }

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 3) return false;
        if (!TryPart(parts[0], out var major)) return false;
        var minor = parts.Length > 1 && TryPart(parts[1], out var minorPart) ? minorPart : 0;
        var patch = parts.Length > 2 && TryPart(parts[2], out var patchPart) ? patchPart : 0;
        if (parts.Length > 1 && !TryPart(parts[1], out _)) return false;
        if (parts.Length > 2 && !TryPart(parts[2], out _)) return false;
        version = new(major, minor, patch, preRelease, original.TrimStart('v', 'V'));
        return true;
    }

    public int CompareTo(LauncherUpdateVersion? other)
    {
        if (other is null) return 1;
        var numeric = Major.CompareTo(other.Major);
        if (numeric != 0) return numeric;
        numeric = Minor.CompareTo(other.Minor);
        if (numeric != 0) return numeric;
        numeric = Patch.CompareTo(other.Patch);
        if (numeric != 0) return numeric;
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static bool TryPart(string value, out int part) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out part) && part >= 0;

    private static int ComparePreRelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
        {
            var leftNumeric = int.TryParse(leftParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(rightParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            int comparison;
            if (leftNumeric && rightNumeric) comparison = leftNumber.CompareTo(rightNumber);
            else if (leftNumeric) comparison = -1;
            else if (rightNumeric) comparison = 1;
            else comparison = StringComparer.OrdinalIgnoreCase.Compare(leftParts[index], rightParts[index]);
            if (comparison != 0) return comparison;
        }
        return leftParts.Length.CompareTo(rightParts.Length);
    }
}
