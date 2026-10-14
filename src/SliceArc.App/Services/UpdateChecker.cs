using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace SliceArc.App.Services;

/// <summary>
/// Update check against the project's own release feeds. The version comes from the
/// <c>releases/latest</c> redirect (302 → /releases/tag/vX.Y.Z) on Gitee first, GitHub
/// as fallback - the same two hosts the landing page's download buttons point at, so a
/// version that is announced is also a version that is downloadable.
/// <para>
/// No telemetry, no auto-install: the check is one HEAD-shaped request, the result is
/// a banner the user clicks, and clicking it opens the landing page's download section.
/// A failed check is silent by design - a mirror being slow must never nag the user.
/// </para>
/// </summary>
public static class UpdateChecker
{
    /// <summary>Landing page download section (both buttons verified live).</summary>
    public const string DownloadUrl = "https://ldl171354.github.io/SliceArc/#download";

    private const string GiteeLatest = "https://gitee.com/ldl171/SliceArc/releases/latest";
    private const string GitHubLatest = "https://github.com/ldl171354/SliceArc/releases/latest";

    public sealed record UpdateInfo(string LatestVersion, string CurrentVersion, Uri PageUrl);

    /// <summary>Current assembly version, e.g. "0.4.2".</summary>
    public static string CurrentVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>
    /// Ask both feeds for the latest tag. Returns null when nothing could be reached -
    /// callers treat that as "no update", never as an error to show.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        foreach (var url in new[] { GiteeLatest, GitHubLatest })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                {
                    Timeout = TimeSpan.FromSeconds(8),
                };
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                var location = response.Headers.Location?.ToString();
                if (string.IsNullOrEmpty(location))
                {
                    // Some hosts answer 200 with the page inline; fall back to the body.
                    if (!response.IsSuccessStatusCode) continue;
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var fromBody = ParseTag(body);
                    if (fromBody is null) continue;
                    return MakeInfo(fromBody);
                }
                var tag = ParseTag(location) ?? ParseTag(response.Headers.Location?.AbsolutePath ?? "");
                if (tag is not null) return MakeInfo(tag);
            }
            catch
            {
                // Mirror down, proxy off, timeout: try the next source, then stay silent.
            }
        }
        return null;
    }

    private static UpdateInfo MakeInfo(string tag)
    {
        var latest = NormalizeVersion(tag);
        var current = CurrentVersion();
        return IsNewer(latest, current)
            ? new UpdateInfo(latest, current, new Uri(DownloadUrl))
            : null!;   // null means "up to date or unreachable"
    }

    private static string? ParseTag(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            text, @"/releases/tag/v?(\d+\.\d+(\.\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string NormalizeVersion(string tag) =>
        tag.Trim().TrimStart('v', 'V');

    /// <summary>True when <paramref name="latest"/> is strictly newer than current.</summary>
    public static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(latest, out var a)) return false;
        if (!Version.TryParse(current, out var b)) return false;
        return a > b;
    }
}
