using System.ComponentModel;
using System.Runtime.CompilerServices;
using SliceArc.Core.Shell;

namespace SliceArc.App.Models;

/// <summary>
/// One archive extension in the first-run guide or the association settings:
/// a checkbox plus - in the settings view - the handler Windows currently uses,
/// so the user can see at a glance whether a binding was taken over by another
/// program and needs redoing.
/// </summary>
public sealed class AssociationRow : INotifyPropertyChanged
{
    public AssociationRow(ArchiveType type)
    {
        Type = type;
    }

    public ArchiveType Type { get; }

    public string Extension => Type.Extension;

    public string Description => Type.Description;

    private bool _isChecked = true;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    private string _currentDefault = "未绑定";
    public string CurrentDefault
    {
        get => _currentDefault;
        set
        {
            if (_currentDefault == value) return;
            _currentDefault = value;
            OnPropertyChanged();
        }
    }

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
