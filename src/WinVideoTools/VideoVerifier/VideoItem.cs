using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinVideoTools.VideoVerifier;

public enum VideoStatus { Pending, Checking, Valid, ValidWithErrors, Invalid, Failed }

public sealed class VideoItem(string path, string relativePath, long size, DateTime modifiedUtc) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string RelativePath { get; } = relativePath;
    // As listed when the item was created, so a refresh can tell whether the file changed since.
    public long Size { get; } = size;
    public DateTime ModifiedUtc { get; } = modifiedUtc;

    public VideoStatus Status
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(IsChecking));
                OnPropertyChanged(nameof(IsValid));
            }
        }
    }

    public string Summary { get; set => Set(ref field, value); } = "";
    public string Details { get; set => Set(ref field, value); } = "";
    public double Progress { get; set => Set(ref field, value); }

    public bool IsChecking => Status == VideoStatus.Checking;

    public bool IsValid => Status is VideoStatus.Valid or VideoStatus.ValidWithErrors;

    public string StatusText => Status switch
    {
        VideoStatus.ValidWithErrors => "Valid, errors",
        VideoStatus.Failed => "Error",
        _ => Status.ToString(),
    };

    public Brush StatusBrush => (Brush)Application.Current.Resources[Status switch
    {
        VideoStatus.Valid => "SystemFillColorSuccessBrush",
        VideoStatus.ValidWithErrors => "SystemFillColorCautionBrush",
        VideoStatus.Invalid or VideoStatus.Failed => "SystemFillColorCriticalBrush",
        _ => "TextFillColorSecondaryBrush",
    }];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
