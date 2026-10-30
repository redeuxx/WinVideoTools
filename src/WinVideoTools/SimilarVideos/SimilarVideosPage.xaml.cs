using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
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
        // A preview capture only reads, so it is not worth asking about, but its ffmpeg is still stopped.
        MainWindow.TrackActivity(() => _cts is not null ? "Similar Videos is scanning for duplicates."
            : _busy ? "Similar Videos is moving or deleting files." : null);
        // Kills the ffmpeg processes, which would otherwise outlive the app.
        MainWindow.Exiting += () =>
        {
            _cts?.Cancel();
            _previewCts?.Cancel();
        };
        Results.Loaded += Results_Loaded;
    }

    // RESULTS WIDTH

    // The fixed columns, their spacing and the row padding take about 610 px; the rest leaves room for the path.
    // Narrower than this (the preview pane alone takes 400 px), the list scrolls sideways instead of squeezing columns out of view.
    // ponytail: matched by hand to the column widths in the XAML; change both together.
    private const double MinResultsWidth = 800;
    // How far the results are scrolled sideways; each row's check box is shifted by this much to stay in view.
    private double _scrolledRight;

    private void Results_Loaded(object sender, RoutedEventArgs e)
    {
        // Once: the page is cached, so Loaded fires on every visit, but the template parts wired up here stay the same.
        Results.Loaded -= Results_Loaded;
        if (VisualTree.FindChild<ScrollViewer>(Results) is not { Content: FrameworkElement content } viewer) return;
        content.Width = Math.Max(viewer.ActualWidth, MinResultsWidth);
        viewer.SizeChanged += (_, args) => content.Width = Math.Max(args.NewSize.Width, MinResultsWidth);
        // Selecting a row brings it into view. Rows are as wide as the content, so that would scroll back to the left edge;
        // keeping the request inside the visible columns leaves the sideways position alone and still scrolls up and down.
        content.BringIntoViewRequested += (_, args) =>
        {
            var left = args.TargetElement.TransformToVisual(viewer).TransformPoint(default).X;
            var r = args.TargetRect;
            args.TargetRect = new Rect(-left, r.Y, Math.Min(r.Width, viewer.ViewportWidth), r.Height);
        };
        // Fires repeatedly while scrolling, so the check boxes keep up with the drag.
        viewer.ViewChanged += (_, _) =>
        {
            _scrolledRight = viewer.HorizontalOffset;
            foreach (var row in Results.ItemsPanelRoot?.Children.OfType<ListViewItem>() ?? [])
                if (row.ContentTemplateRoot is Grid root) PinCheckBox(root);
        };
    }

    // Rows created or reused while scrolled sideways start out pinned too.
    private void Results_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.ItemContainer.ContentTemplateRoot is Grid root) PinCheckBox(root);
    }

    // Moves the check box right by the sideways scroll so it stays at the left edge, and cuts off the cells that
    // slide under it. A render transform, unlike a composition animation, moves where clicks land too.
    private void PinCheckBox(Grid row)
    {
        if (row.Children.Count < 2 || row.Children[0] is not CheckBox box || row.Children[1] is not FrameworkElement cells) return;
        var x = _scrolledRight;
        if (box.RenderTransform is TranslateTransform shift) shift.X = x;
        else box.RenderTransform = new TranslateTransform { X = x };
        // The cells start one column gap right of the box, so hiding everything left of x in their own space keeps that gap.
        // ponytail: oversized rect; only its left edge matters.
        cells.Clip = x <= 0 ? null : new RectangleGeometry { Rect = new Rect(x, 0, 1e5, 1e5) };
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
            // Listing a large tree can take a while; show it moving. Reports can land after listing ends, so they stop at "listing".
            var listing = true;
            var earlier = 0;
            OverallProgress.IsIndeterminate = true;
            var found = new Progress<int>(n => { if (listing) SummaryText.Text = $"Finding videos... {earlier + n:N0} found"; });
            List<FileInfo> files;
            try
            {
                // Overlapping folders (a folder and its subfolder) would list a file twice; keep one.
                files = await Task.Run(() => folders
                    .SelectMany(f =>
                    {
                        var list = VideoFiles.Find(f, recurse, ct, found);
                        // Only this thread writes it; the UI reading a stale value just makes the count lag a moment.
                        earlier += list.Count;
                        return list;
                    })
                    .DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToList(), ct);
            }
            finally
            {
                listing = false;
                OverallProgress.IsIndeterminate = false;
            }
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

            SummaryText.Text = $"Comparing {readable.Count:N0} videos...";
            OverallProgress.IsIndeterminate = true;
            List<List<int>> groups;
            try { groups = await Task.Run(() => VideoFingerprint.Group(readable.Select(i => prints[i]!).ToList()), ct); }
            finally { OverallProgress.IsIndeterminate = false; }
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
        FileActionButton.IsEnabled = !_busy && count > 0;
        SelectButton.IsEnabled = !_busy && _groups.Count > 0;
    }

    // FILE ACTIONS

    private async void Recycle_Click(object sender, RoutedEventArgs e) => await RunActionAsync(FileAction.Recycle);

    private async void Delete_Click(SplitButton sender, SplitButtonClickEventArgs e) => await RunActionAsync(FileAction.Delete);

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

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        try
        {
            VideoFiles.ShowInFolder(path);
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not open the folder for {path}: {ex.Message}");
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

    // Closes itself after 10 seconds unless a newer message has replaced it; errors stay until dismissed.
    private async void ShowInfo(InfoBarSeverity severity, string message)
    {
        Info.Severity = severity;
        Info.Message = message;
        Info.IsOpen = true;
        var shown = Info.Tag = new object();
        if (severity == InfoBarSeverity.Error) return;
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (Info.Tag == shown) Info.IsOpen = false;
    }
}
