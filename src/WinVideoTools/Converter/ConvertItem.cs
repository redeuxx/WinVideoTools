using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinVideoTools.Converter;

public enum ConvertStatus { Queued, Converting, Done, Failed, Cancelled, Skipped }

public sealed class ConvertItem(string path, long inputSize) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);

    public ConvertStatus Status
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(IsConverting));
                OnPropertyChanged(nameof(SizeChangeText));
            }
        }
    }

    public string Summary { get; set => Set(ref field, value); } = "";
    public string Details { get; set => Set(ref field, value); } = "";
    public string Decision { get; set => Set(ref field, value); } = "";
    public double Progress { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(PercentText)); } }

    public string PercentText => $"{Progress:0%}";

    /// <summary>When the current conversion started; drives the elapsed time shown while converting.</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Called every second by the page so the elapsed time counts up.</summary>
    public void Tick() => OnPropertyChanged(nameof(SizeChangeText));

    /// <summary>The converted file while it exists; null before conversion or once deleted.</summary>
    public string? OutputPath { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(HasOutput)); } }
    public bool HasOutput => OutputPath is not null;

    public bool OriginalDeleted { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(HasOriginal)); } }
    public bool HasOriginal => !OriginalDeleted;

    public long InputSize { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(SizesText)); } } = inputSize;

    /// <summary>Output bytes; null until a conversion finishes.</summary>
    public long? OutputSize { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(SizesText)); } }

    public string SizesText => OutputSize is { } o
        ? $"{PresetConverter.FormatSize(InputSize)} → {PresetConverter.FormatSize(o)}"
        : PresetConverter.FormatSize(InputSize);

    /// <summary>Output size divided by source size; null until a conversion finishes.</summary>
    public double? SizeRatio
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(SizeChangeText));
                OnPropertyChanged(nameof(SizeChangeBrush));
            }
        }
    }

    // While converting there is no size change yet, so the column shows elapsed time instead.
    public string SizeChangeText => IsConverting
        ? "Elapsed " + (DateTime.UtcNow - StartedUtc) switch
        {
            { TotalHours: >= 1 } t => t.ToString(@"h\:mm\:ss"),
            var t => t.ToString(@"m\:ss"),
        }
        : SizeRatio switch
    {
        null => "",
        < 1 and var r => $"{(1 - r) * 100:0.#}% smaller",
        > 1 and var r => $"{(r - 1) * 100:0.#}% larger",
        _ => "Same size",
    };

    public Brush SizeChangeBrush => (Brush)Application.Current.Resources[SizeRatio switch
    {
        < 1 => "SystemFillColorSuccessBrush",
        > 1 => "SystemFillColorCriticalBrush",
        _ => "TextFillColorSecondaryBrush",
    }];

    public bool IsConverting => Status == ConvertStatus.Converting;

    public string StatusText => Status.ToString();

    public Brush StatusBrush => (Brush)Application.Current.Resources[Status switch
    {
        ConvertStatus.Done => "SystemFillColorSuccessBrush",
        ConvertStatus.Failed => "SystemFillColorCriticalBrush",
        ConvertStatus.Converting => "AccentTextFillColorPrimaryBrush",
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
