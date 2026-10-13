using System.IO;

namespace SliceArc.App.Services;

/// <summary>
/// The "what do I do about it" half of an error dialog. The dialog already shows
/// the exception message; this turns the common failure types into one
/// actionable sentence so a user is never left with a bare system error.
///
/// Shared by the window paths (MainWindow) and the headless / right-click paths
/// (App), which is why it lives here rather than on either window.
/// </summary>
public static class FailureAdvice
{
    /// <summary>Advice for <paramref name="ex"/>, or null when we have nothing useful to add.</summary>
    public static string? For(Exception ex) => ex switch
    {
        UnauthorizedAccessException =>
            "没有访问权限。请换一个目标文件夹，或以管理员身份运行后重试。",
        PathTooLongException =>
            "路径过长。请把压缩包移到路径更短的文件夹后再试。",
        DirectoryNotFoundException =>
            "目标文件夹不存在或已被移动，请重新选择目标位置。",
        FileNotFoundException =>
            "文件不存在，可能已被移动或删除。",
        InvalidDataException =>
            "该文件不是有效的压缩包，或已损坏、不完整；若是分卷，请确认所有分卷齐全。",
        IOException =>
            "文件或磁盘不可写：可能正被其他程序占用、磁盘已满，或目标路径无效。",
        OutOfMemoryException =>
            "内存不足。请关闭其他程序后重试，或分次解压。",
        _ => null,
    };
}
