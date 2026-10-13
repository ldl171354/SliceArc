using System.Media;

namespace SliceArc.App.Services;

/// <summary>
/// User-facing product name. Internal identifiers (exe name, Inno AppId,
/// registry verb keys, signing certificate, settings file name) stay
/// "SliceArc" so upgrades and the uninstall chain keep working.
/// </summary>
internal static class Brand
{
    public const string Name = "超速解压缩";

    /// <summary>One-line description written into the file-association capabilities.</summary>
    public const string Description = "分卷压缩包解压 / 压缩工具，基于 7-Zip 引擎。";
}

/// <summary>
/// Completion notification sound: the whole point is that a run launched from
/// the Explorer menu and left in the background is *heard* when it finishes.
/// Best-effort - a machine without a sound device must never fail a run.
/// </summary>
internal static class CompletionNotice
{
    /// <summary>Values accepted by <see cref="AppSettings.CompletionSound"/>.</summary>
    public static readonly string[] SoundNames = { "Asterisk", "Exclamation", "Beep", "Hand", "Question" };

    public static void Play(AppSettings settings, bool success)
    {
        if (!settings.PlaySoundOnComplete) return;
        try
        {
            if (!success)
            {
                SystemSounds.Hand.Play();
                return;
            }

            var sound = settings.CompletionSound switch
            {
                "Exclamation" => SystemSounds.Exclamation,
                "Beep" => SystemSounds.Beep,
                "Hand" => SystemSounds.Hand,
                "Question" => SystemSounds.Question,
                _ => SystemSounds.Asterisk,
            };
            sound.Play();
        }
        catch
        {
            // No audio device / sounds scheme locked down: fine, stay silent.
        }
    }
}
