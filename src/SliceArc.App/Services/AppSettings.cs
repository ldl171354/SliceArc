using System.IO;
using System.Text.Json;

namespace SliceArc.App.Services;

/// <summary>
/// Portable preferences, stored next to the executable so nothing lands on the
/// system drive. Failure to read or write is never fatal.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string? LastDirectory { get; set; }

    public bool DeleteSpentVolumes { get; set; }

    public int KeepLastVolumes { get; set; } = 1;

    public bool Overwrite { get; set; } = true;

    public double WindowWidth { get; set; } = 960;

    public double WindowHeight { get; set; } = 600;

    public string Theme { get; set; } = "Light";

    /// <summary>Pop a confirmation box when an extract/compress run finishes.</summary>
    public bool ShowCompletionDialog { get; set; } = true;

    /// <summary>Play a system sound when a run finishes (background runs are heard).</summary>
    public bool PlaySoundOnComplete { get; set; } = true;

    /// <summary>One of CompletionNotice.SoundNames; the success sound.</summary>
    public string CompletionSound { get; set; } = "Asterisk";

    /// <summary>The first-run guide has been shown (and dismissed) once.</summary>
    public bool WelcomeShown { get; set; }

    /// <summary>Extensions the user asked to bind to this app, for re-binding later.</summary>
    public string[]? BoundExtensions { get; set; }

    /// <summary>The release version the user chose「跳过此版本」for; the banner stays quiet for it.</summary>
    public string? SkippedUpdateVersion { get; set; }

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "SliceArc.settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取设置失败，使用默认值: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"保存设置失败: {ex.Message}");
        }
    }
}
