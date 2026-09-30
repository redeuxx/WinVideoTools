using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WinVideoTools.SimilarVideos;

public sealed class DuplicateFile(string path, long size, VideoFingerprint fingerprint) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public long Size { get; } = size;
    private readonly VideoFingerprint _fingerprint = fingerprint;

    public bool IsSelected
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }

    public long Pixels => (long)_fingerprint.Width * _fingerprint.Height;
    public double Duration => _fingerprint.Duration;

    public string Dimensions => $"{_fingerprint.Width}x{_fingerprint.Height}";

    // Container from the extension, codec as ffmpeg names it, e.g. "MP4 (h264)".
    public string Format => $"{System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant()} ({_fingerprint.Codec})";

    // Video stream rate when the container reports it, else "~" and the whole file's rate (audio included).
    public string Bitrate => _fingerprint.VideoBitrate > 0 ? $"{_fingerprint.VideoBitrate} kb/s"
        : _fingerprint.Duration > 0 ? $"~{Size * 8 / _fingerprint.Duration / 1000:0} kb/s"
        : "";

    // Not "Info": a template property sharing a name with a page element (the InfoBar) crashes the XAML compiler (WMC9999).
    public string Description => $"{TimeSpan.FromSeconds(Math.Round(_fingerprint.Duration)):g}   {FormatSize(Size)}";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Files judged to be the same video, best copy (highest resolution, then smallest) first.</summary>
public sealed class DuplicateGroup(IEnumerable<DuplicateFile> files) : ObservableCollection<DuplicateFile>(files)
{
    public string Header => $"{Count} similar files";

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnCollectionChanged(e);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Header)));
    }
}
