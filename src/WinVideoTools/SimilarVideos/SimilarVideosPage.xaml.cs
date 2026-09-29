using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace WinVideoTools.SimilarVideos;

public sealed partial class SimilarVideosPage : Page
{
    private readonly ObservableCollection<string> _folders = [];
    private readonly ObservableCollection<DuplicateGroup> _groups = [];
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _previewCts;
    private bool _busy;
    private bool _bulkSelecting;
    // Fingerprints from the last scan, by full path, reused by refresh for unchanged files.
    private Dictionary<string, (long Size, DateTime ModifiedUtc, VideoFingerprint Print)> _fingerprints = new(StringComparer.OrdinalIgnoreCase);

    public SimilarVideosPage()
    {
        InitializeComponent();
        Results.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = _groups }.View;
        FolderList.ItemsSource = _folders;
        _folders.CollectionChanged += (_, _) => SetBusy(_busy);
        SetBusy(false);
        // Fingerprinting only decodes a few frames per file, so it tolerates more parallelism than verifying.
        ParallelBox.Value = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        DriftBox.Value = VideoFingerprint.DefaultMaxDrift;
        DriftBox.Maximum = VideoFingerprint.MaxMaxDrift;
        FileDrop.Attach(this, "Add to folders", () => !_busy, OnDropAsync);
    }

    // A dropped file stands for the folder it is in.
    private Task OnDropAsync(IReadOnlyList<string> folders, IReadOnlyList<string> files)
    {
        var dropped = folders.Concat(files.Select(f => Path.GetDirectoryName(f)!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var added = dropped.Where(f => !_folders.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var f in added) _folders.Add(f);
        if (added.Count < dropped.Count)
            ShowInfo(InfoBarSeverity.Informational, added.Count == 0 ? "Those folders are already in the list." : $"Added {added.Count}; the rest were already in the list.");
        return Task.CompletedTask;
    }

    // FOLDERS

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await FileActions.PickFolderAsync() is not { } folder) return;
        if (_folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            ShowInfo(InfoBarSeverity.Informational, "That folder is already in the list.");
            return;
        }
        _folders.Add(folder);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string folder) _folders.Remove(folder);
    }

    // SCAN

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }
        await ScanAsync(refresh: false);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) await ScanAsync(refresh: true);
    }

    /// <summary>
    /// Lists every added folder, fingerprints the files and groups them across all folders. A refresh reuses
    /// fingerprints of files unchanged since the last scan and keeps the selection, so only new or changed files run.
    /// </summary>
    private async Task ScanAsync(bool refresh)
    {
        var folders = _folders.ToList();
        if (folders.Count == 0)
        {
            ShowInfo(InfoBarSeverity.Error, "Add a folder first.");
            return;
        }
        if (folders.FirstOrDefault(f => !Directory.Exists(f)) is { } missing)
        {
            ShowInfo(InfoBarSeverity.Error, $"Folder not found: {missing}. Reconnect the drive or remove the folder from the list.");
            return;
        }
        if (!await FfmpegPrompt.EnsureAsync(XamlRoot)) return;
        var ffmpeg = Ffmpeg.ExePath;

        // Fingerprints are keyed by full path and checked against size and time, so they stay valid
        // whichever folders are listed; adding a folder and refreshing only fingerprints the new files.
        var cache = refresh ? _fingerprints : [];
        var selected = refresh ? Selected.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];

        Info.IsOpen = false;
        _groups.Clear();
        _fingerprints = new(StringComparer.OrdinalIgnoreCase);
        SummaryText.Text = "Finding videos...";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);

        try
        {
            var recurse = RecurseBox.IsChecked == true;
            // Overlapping folders (a folder and its subfolder) would list a file twice; keep one.
            var files = await Task.Run(() => folders
                .SelectMany(f => VideoFiles.Find(f, recurse, ct))
                .DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList(), ct);
            if (files.Count == 0)
            {
                SummaryText.Text = "";
                ShowInfo(InfoBarSeverity.Informational, "No video files found.");
                return;
            }

            var drift = double.IsNaN(DriftBox.Value) ? VideoFingerprint.DefaultMaxDrift : Math.Clamp(DriftBox.Value, 1, VideoFingerprint.MaxMaxDrift);
            var prints = new VideoFingerprint?[files.Count];
            for (var i = 0; i < files.Count; i++)
            {
                if (cache.TryGetValue(files[i].FullName, out var c) && VideoFiles.IsUnchanged(files[i], c.Size, c.ModifiedUtc)
                    && c.Print.MaxDrift == drift)
                    prints[i] = c.Print;
            }
            var todo = Enumerable.Range(0, files.Count).Where(i => prints[i] is null).ToList();
            int done = files.Count - todo.Count, failed = 0;
            OverallProgress.Maximum = files.Count;
            OverallProgress.Value = done;
            SummaryText.Text = $"Fingerprinting {done} of {files.Count}...";

            // Continuations resume on the UI thread, so the counters and controls need no locking.
            using var gate = new SemaphoreSlim((int)Math.Clamp(double.IsNaN(ParallelBox.Value) ? 1 : ParallelBox.Value, 1, 32));
            await Task.WhenAll(todo.Select(async i =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    prints[i] = await VideoFingerprint.ComputeAsync(ffmpeg, files[i].FullName, drift, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                }
                finally
                {
                    gate.Release();
                }
                OverallProgress.Value = ++done;
                SummaryText.Text = $"Fingerprinting {done} of {files.Count}...";
            }));

            var readable = Enumerable.Range(0, files.Count).Where(i => prints[i] is not null).ToList();
            // Only successes are remembered, so files that failed (say, still copying) are retried on refresh.
            foreach (var i in readable)
                _fingerprints[files[i].FullName] = (files[i].Length, files[i].LastWriteTimeUtc, prints[i]!);

            var groups = await Task.Run(() => VideoFingerprint.Group(readable.Select(i => prints[i]!).ToList()), ct);
            foreach (var indexes in groups)
            {
                var members = indexes.Select(j => readable[j]).Select(i =>
                {
                    var path = files[i].FullName;
                    var item = new DuplicateFile(path, files[i].Length, prints[i]!)
                    {
                        IsSelected = selected.Contains(path),
                    };
                    item.PropertyChanged += Item_PropertyChanged;
                    return item;
                });
                _groups.Add(new DuplicateGroup(members.OrderByDescending(f => f.Pixels).ThenBy(f => f.Size)));
            }

            var fileCount = _groups.Sum(g => g.Count);
            SummaryText.Text = $"{files.Count} videos scanned: {_groups.Count} group{(_groups.Count == 1 ? "" : "s")} of similar videos ({fileCount} files)"
                + (failed > 0 ? $", {failed} could not be read" : "");
            if (refresh)
                ShowInfo(InfoBarSeverity.Success, todo.Count == 0
                    ? "Refreshed: no new or changed files."
                    : $"Refreshed: fingerprinted {todo.Count} new or changed file{(todo.Count == 1 ? "" : "s")}.");
            else if (_groups.Count == 0)
                ShowInfo(InfoBarSeverity.Success, "No similar videos found.");
        }
        catch (OperationCanceledException)
        {
            SummaryText.Text = "";
            ShowInfo(InfoBarSeverity.Warning, "Scan cancelled.");
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    // SELECTION

    private IEnumerable<DuplicateFile> Selected => _groups.SelectMany(g => g).Where(f => f.IsSelected);

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_bulkSelecting) UpdateSelection();
    }

    private void SelectBest_Click(object sender, RoutedEventArgs e) => SelectWhere(indexInGroup => indexInGroup > 0);

    private void Clear_Click(object sender, RoutedEventArgs e) => SelectWhere(_ => false);

    // Groups are sorted best copy first, so index 0 is the one to keep.
    private void SelectWhere(Func<int, bool> select)
    {
        _bulkSelecting = true;
        foreach (var group in _groups)
            for (var i = 0; i < group.Count; i++)
                group[i].IsSelected = select(i);
        _bulkSelecting = false;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var count = Selected.Count();
        SelectionText.Text = count == 0 ? "" : $"{count} selected";
        RecycleButton.IsEnabled = MoveButton.IsEnabled = DeleteButton.IsEnabled = !_busy && count > 0;
        SelectBestButton.IsEnabled = ClearButton.IsEnabled = !_busy && _groups.Count > 0;
    }

    // FILE ACTIONS

    private async void Recycle_Click(object sender, RoutedEventArgs e) => await RunActionAsync(FileAction.Recycle);

    private async void Delete_Click(object sender, RoutedEventArgs e) => await RunActionAsync(FileAction.Delete);

    private async void Move_Click(object sender, RoutedEventArgs e)
    {
        if (await FileActions.PickFolderAsync() is { } target) await RunActionAsync(FileAction.Move, target);
    }

    private async Task RunActionAsync(FileAction action, string? target = null)
    {
        var selected = Selected.ToList();
        if (selected.Count == 0) return;

        var wholeGroups = _groups.Count(g => g.All(f => f.IsSelected));
        var warning = wholeGroups == 0 ? null
            : $"Every copy in {wholeGroups} group{(wholeGroups == 1 ? " is" : "s are")} selected, so no copy of "
              + $"{(wholeGroups == 1 ? "that video" : "those videos")} will remain in the scanned folder.";
        if (!await FileActions.ConfirmAsync(XamlRoot, action, selected.Count, target, warning)) return;

        SetBusy(true);
        var (done, errors) = await FileActions.ApplyAsync(action, selected.Select(f => f.Path).ToList(), target);

        foreach (var file in selected.Where(f => done.Contains(f.Path)))
        {
            file.PropertyChanged -= Item_PropertyChanged;
            _groups.First(g => g.Contains(file)).Remove(file);
        }
        // A group with one file left is no longer a set of duplicates.
        foreach (var group in _groups.Where(g => g.Count < 2).ToList())
        {
            foreach (var file in group) file.PropertyChanged -= Item_PropertyChanged;
            _groups.Remove(group);
        }
        SetBusy(false);

        var (severity, message) = FileActions.Describe(action, done.Count, errors);
        ShowInfo(severity, message);
    }

    // OPEN

    private void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is DuplicateFile file) OpenVideo(file.Path);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) OpenVideo(path);
    }

    private void OpenVideo(string path)
    {
        try
        {
            VideoFiles.Open(path);
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not open {path}: {ex.Message}");
        }
    }

    // PREVIEW

    private async void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A newer click supersedes a capture still running, so a slow file never overwrites a later one.
        _previewCts?.Cancel();
        _previewCts = null;
        PreviewImage.Source = null;
        PreviewError.Text = "";
        PreviewRing.IsActive = false;
        if (Results.SelectedItem is not DuplicateFile file)
        {
            PreviewPane.Visibility = Visibility.Collapsed;
            return;
        }
        PreviewPane.Visibility = Visibility.Visible;
        PreviewTitle.Text = file.Path;
        PreviewRing.IsActive = true;

        var cts = _previewCts = new CancellationTokenSource();
        try
        {
            var jpeg = await VideoFingerprint.ScreenshotAsync(Ffmpeg.ExePath, file.Path, file.Duration / 2, cts.Token);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(jpeg.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (!cts.IsCancellationRequested) PreviewImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) PreviewError.Text = $"Could not capture a screenshot: {ex.Message}";
        }
        finally
        {
            if (_previewCts == cts)
            {
                PreviewRing.IsActive = false;
                _previewCts = null;
            }
            cts.Dispose();
        }
    }

    private void ClosePreview_Click(object sender, RoutedEventArgs e) => Results.SelectedItem = null;

    // STATE

    private void SetBusy(bool busy)
    {
        _busy = busy;
        var scanning = _cts is not null;
        ScanButton.Content = scanning ? "Cancel" : "Scan";
        ScanButton.IsEnabled = scanning || (!busy && _folders.Count > 0);
        RefreshButton.IsEnabled = !busy && _folders.Count > 0;
        FolderList.IsEnabled = AddFolderButton.IsEnabled = RecurseBox.IsEnabled = ParallelBox.IsEnabled = DriftBox.IsEnabled = !busy;
        NoFoldersText.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Results.IsEnabled = !busy;
        OverallProgress.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        Info.Severity = severity;
        Info.Message = message;
        Info.IsOpen = true;
    }
}
