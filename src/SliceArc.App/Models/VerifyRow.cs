using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SliceArc.Core.Extraction;

namespace SliceArc.App.Models;

/// <summary>What a queued archive is doing in the check tool.</summary>
public enum VerifyState
{
    Pending,
    Running,
    Passed,
    Corrupt,
    NeedsPassword,
    Unverifiable,
    Cancelled,
}

/// <summary>
/// One row of the archive-check list. Wraps the verifier's report so the grid can
/// bind straight to it; the counters and the issue list update live while the
/// check runs, which is why every displayed member raises change notifications.
/// </summary>
public sealed class VerifyRow : INotifyPropertyChanged
{
    public VerifyRow(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        try { SizeText = BrowserRow.FormatSize(new System.IO.FileInfo(path).Length); }
        catch { SizeText = "-"; }
    }

    public string Path { get; }

    public string Name { get; }

    /// <summary>Issues found so far; filled in as members are decoded.</summary>
    public ObservableCollection<VerifyIssue> Issues { get; } = new();

    private string _format = "unknown";
    public string Format
    {
        get => _format;
        set { _format = value; OnPropertyChanged(); }
    }

    private string _sizeText = "-";
    public string SizeText
    {
        get => _sizeText;
        set { _sizeText = value; OnPropertyChanged(); }
    }

    private int _entryCount;
    public int EntryCount
    {
        get => _entryCount;
        set { _entryCount = value; OnPropertyChanged(); }
    }

    private int _passed;
    public int Passed
    {
        get => _passed;
        set { _passed = value; OnPropertyChanged(); }
    }

    private int _failed;
    public int Failed
    {
        get => _failed;
        set { _failed = value; OnPropertyChanged(); }
    }

    private string _status = "待校验";
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public VerifyState State { get; set; } = VerifyState.Pending;

    public ArchiveVerifyReport? Report { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
