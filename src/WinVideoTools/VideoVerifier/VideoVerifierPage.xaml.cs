using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace WinVideoTools.VideoVerifier;

public sealed partial class VideoVerifierPage : Page
{
    private readonly ObservableCollection<VideoItem> _items = [];
    // What the list shows: _items in the same order, narrowed by the status filter.
    private readonly ObservableCollection<VideoItem> _view = [];
    private CancellationTokenSource? _cts;
    private int _valid, _invalid, _done;
    // Moving or deleting files; a scan is running while _cts is set. Either can run while the other does.
    private bool _acting;
    private bool Busy => _cts is not null || _acting;
    private string? _scannedFolder;

    public VideoVerifierPage()
    {
        InitializeComponent();
        Results.ItemsSource = _view;
        FilterBox.ItemsSource = (string[])["All", "Valid", "Invalid", "Processing"];
        FilterBox.SelectedIndex = 0;
        // ffmpeg already decodes multithreaded, so a few files at once saturates most CPUs.
        ParallelBox.Value = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        FileDrop.Attach(this, "Scan this folder", () => !Busy, OnDropAsync);
        MainWindow.TrackActivity(() => _cts is not null ? "Video Verifier is checking videos."
            : _acting ?"Video Verifier is moving or deleting files." : null);
        // Kills the ffmpeg processes, which would otherwise outlive the app.
        MainWindow.Exiting += () => _cts?.Cancel();
    }

    // One folder is scanned at a time; a dropped file stands for the folder it is in.
    private Task OnDropAsync(IReadOnlyList<string> folders, IReadOnlyList<string> files)
    {
        if ((folders.FirstOrDefault() ?? files.Select(Path.GetDirectoryName).FirstOrDefault()) is not { } folder) return Task.CompletedTask;
        FolderBox.Text = folder;
        if (folders.Count + files.Count > 1)
            ShowInfo(InfoBarSeverity.Informational, $"The verifier scans one folder at a time; using {folder}.");
        return Task.CompletedTask;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(App.MainWindow!.AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        if (result is not null) FolderBox.Text = result.Path;
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
        if (!Busy) await ScanAsync(refresh: true);
    }

    /// <summary>
    /// Lists the folder and checks every pending file. A full scan starts from scratch; a refresh keeps
    /// finished results for files unchanged since they were checked, so only new or changed files run.
    /// </summary>
    private async Task ScanAsync(bool refresh)
    {
        var folder = FolderBox.Text.Trim();
        if (!Directory.Exists(folder))
        {
            ShowInfo(InfoBarSeverity.Error, "Folder not found.");
            return;
        }
        if (!await FfmpegPrompt.EnsureAsync(XamlRoot)) return;

        // Results are only reusable for the folder they came from; relative paths depend on it.
        refresh = refresh && string.Equals(folder, _scannedFolder, StringComparison.OrdinalIgnoreCase);
        var previous = refresh ? _items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase) : [];
        var selected = refresh
            ? Results.SelectedItems.OfType<VideoItem>().Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        Info.IsOpen = false;
        _items.Clear();
        SyncView();
        if (!refresh) DetailsBox.Text = "";
        _valid = _invalid = _done = 0;
        _scannedFolder = folder;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        UpdateState();

        try
        {
            var recurse = RecurseBox.IsChecked == true;
            // Listing a large tree can take a while; show it moving. Reports can land after Find returns, so they stop at "listing".
            var listing = true;
            SummaryText.Text = "Finding videos...";
            OverallProgress.IsIndeterminate = true;
            var found = new Progress<int>(n => { if (listing) SummaryText.Text = $"Finding videos... {n:N0} found"; });
            List<FileInfo> files;
            try { files = await Task.Run(() => VideoFiles.Find(folder, recurse, ct, found), ct); }
            finally
            {
                listing = false;
                OverallProgress.IsIndeterminate = false;
            }

            foreach (var f in files)
            {
                var item = previous.TryGetValue(f.FullName, out var old)
                           && old.Status is not (VideoStatus.Pending or VideoStatus.Checking)
                           && VideoFiles.IsUnchanged(f, old.Size, old.ModifiedUtc)
                    ? old
                    : new VideoItem(f.FullName, Path.GetRelativePath(folder, f.FullName), f.Length, f.LastWriteTimeUtc);
                _items.Add(item);
            }
            SyncView();
            // Only rows in the list can be selected, so files hidden by the filter lose their selection.
            foreach (var item in _view.Where(i => selected.Contains(i.Path))) Results.SelectedItems.Add(item);
            _valid = _items.Count(i => i.IsValid);
            _invalid = _items.Count(IsInvalid);
            _done = _valid + _invalid;
            OverallProgress.Maximum = Math.Max(1, _items.Count);
            UpdateSummary();

            if (files.Count == 0)
            {
                ShowInfo(InfoBarSeverity.Informational, "No video files found.");
                return;
            }

            var pending = _items.Where(i => i.Status == VideoStatus.Pending).ToList();
            using var gate = new SemaphoreSlim((int)Math.Clamp(double.IsNaN(ParallelBox.Value) ? 1 : ParallelBox.Value, 1, 32));
            await Task.WhenAll(pending.Select(item => CheckOneAsync(item, gate, ct)));
            if (refresh)
                ShowInfo(InfoBarSeverity.Success, pending.Count == 0
                    ? "Refreshed: no new or changed files."
                    : $"Refreshed: checked {pending.Count} new or changed file{(pending.Count == 1 ? "" : "s")}.");
        }
        catch (OperationCanceledException)
        {
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
            UpdateState();
            UpdateSummary();
        }
    }

    // Runs on the UI thread; only the ffmpeg work inside VideoChecker leaves it.
    private async Task CheckOneAsync(VideoItem item, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            item.Status = VideoStatus.Checking;
            if (AutoScrollBox.IsChecked == true && _view.Contains(item)) Results.ScrollIntoView(item);
            UpdateSummary();
            var result = await VideoChecker.CheckAsync(
                Ffmpeg.ExePath, item.Path, new Progress<double>(p => item.Progress = p), ct);
            item.Summary = result.Summary;
            item.Details = result.Details;
            item.Status = !result.IsValid ? VideoStatus.Invalid : result.HasMinorErrors ? VideoStatus.ValidWithErrors : VideoStatus.Valid;
            if (result.IsValid) _valid++; else _invalid++;
        }
        catch (OperationCanceledException)
        {
            item.Status = VideoStatus.Pending;
            throw;
        }
        catch (Exception ex)
        {
            item.Summary = ex.Message;
            item.Status = VideoStatus.Failed;
            _invalid++;
        }
        finally
        {
            gate.Release();
        }
        _done++;
        SyncView();
        UpdateSummary();
        // A selected file that just finished may unlock the file actions.
        UpdateSelection();
    }

    private async void Options_Click(object sender, RoutedEventArgs e) => await OptionsDialog.ShowAsync();

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(App.MainWindow!.AppWindow.Id)
        {
            SuggestedFileName = $"video-check-{DateTime.Now:yyyyMMdd-HHmmss}",
        };
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        var result = await picker.PickSaveFileAsync();
        if (result is null) return;

        var sb = new StringBuilder("Status,Path,Summary,Details\r\n");
        foreach (var i in _items)
            sb.AppendJoin(',', Csv(i.StatusText), Csv(i.Path), Csv(i.Summary), Csv(i.Details)).Append("\r\n");
        try
        {
            await MainWindow.Track("Video Verifier is saving the results.", File.WriteAllTextAsync(result.Path, sb.ToString(), Encoding.UTF8));
            ShowInfo(InfoBarSeverity.Success, $"Saved {result.Path}");
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not save: {ex.Message}");
        }
    }

    private void ClearList_Click(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        _items.Clear();
        SyncView();
        _valid = _invalid = _done = 0;
        _scannedFolder = null;
        DetailsBox.Text = "";
        Info.IsOpen = false;
        SummaryText.Text = "";
        UpdateState();
    }

    // Quote every field, and neutralise leading formula characters so spreadsheet apps
    // do not execute file names or ffmpeg output as formulas.
    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    // SELECTION

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Show the file just ticked; with several ticked and none added, keep what is shown.
        var shown = e.AddedItems.OfType<VideoItem>().LastOrDefault()
                    ?? (Results.SelectedItems.Count == 1 ? Results.SelectedItems[0] as VideoItem : null);
        if (shown is not null)
            ShowDetails(shown);
        else if (Results.SelectedItems.Count == 0)
            DetailsBox.Text = "";
        UpdateSelection();
    }

    private void ShowDetails(VideoItem item) =>
        DetailsBox.Text = string.IsNullOrEmpty(item.Details) ? item.Summary : $"{item.Summary}{Environment.NewLine}{Environment.NewLine}{item.Details}";

    // Arrow keys and row clicks move focus without ticking rows, so the details follow the focused row too.
    private void Results_GotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ListViewItem { Content: VideoItem item }) ShowDetails(item);
    }

    // Only the checkbox ticks a row. Clicking the row focuses it instead, so a double-click to open does not flicker the tick.
    private void Row_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        (Results.ContainerFromItem((sender as FrameworkElement)?.DataContext) as ListViewItem)?.Focus(FocusState.Pointer);
    }

    private static bool IsInvalid(VideoItem i) => i.Status is VideoStatus.Invalid or VideoStatus.Failed;

    private static bool IsProcessing(VideoItem i) => i.Status is VideoStatus.Pending or VideoStatus.Checking;

    // Pending files only count while a scan will still get to them.
    private bool CanActOn(IList<object> selected) =>
        !_acting && selected.Count > 0 && (_cts is null || !selected.OfType<VideoItem>().Any(IsProcessing));

    private void SelectValid_Click(object sender, RoutedEventArgs e) => SelectWhere(i => i.IsValid);

    private void SelectInvalid_Click(object sender, RoutedEventArgs e) => SelectWhere(IsInvalid);

    private void Clear_Click(object sender, RoutedEventArgs e) => SelectWhere(_ => false);

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        if (DetailsBox.Text == "") return;
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(DetailsBox.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Toast.Show(XamlRoot, "Details copied");
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not copy the details: {ex.Message}");
        }
    }

    private void SelectWhere(Func<VideoItem, bool> select)
    {
        Results.SelectedItems.Clear();
        foreach (var item in _view.Where(select)) Results.SelectedItems.Add(item);
    }

    private void UpdateSelection()
    {
        var count = Results.SelectedItems.Count;
        SelectionText.Text = count == 0 ? "" : $"{count} selected";
        FileActionButton.IsEnabled = CanActOn(Results.SelectedItems);
        SelectButton.IsEnabled = !_acting && _items.Count > 0;
    }

    // FILTER

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncView();

    /// <summary>
    /// Brings _view in line with _items and the status filter by adding and removing rows rather than
    /// rebuilding it, so a status change mid-scan does not reset scrolling or selection.
    /// </summary>
    private void SyncView()
    {
        Func<VideoItem, bool> show = (FilterBox.SelectedItem as string) switch
        {
            "Valid" => i => i.IsValid,
            "Invalid" => IsInvalid,
            "Processing" => IsProcessing,
            _ => _ => true,
        };
        var want = _items.Where(show).ToList();
        var keep = want.ToHashSet();
        for (var i = _view.Count - 1; i >= 0; i--)
            if (!keep.Contains(_view[i])) _view.RemoveAt(i);
        // _view is now an in-order subset of want, so anything out of step at i is missing there.
        for (var i = 0; i < want.Count; i++)
            if (i >= _view.Count || _view[i] != want[i]) _view.Insert(i, want[i]);
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
        if (!CanActOn(Results.SelectedItems)) return;
        var selected = Results.SelectedItems.OfType<VideoItem>().ToList();

        // Guard against "Select valid" clicked by mistake before a destructive action.
        var valid = selected.Count(i => i.IsValid);
        var warning = action == FileAction.Move || valid == 0 ? null
            : $"{valid} of the selected files passed verification.";
        if (!await FileActions.ConfirmAsync(XamlRoot, action, selected.Count, target, warning)) return;

        _acting = true;
        UpdateState();
        var (done, errors) = await FileActions.ApplyAsync(action, selected.Select(i => i.Path).ToList(), target);
        foreach (var item in selected.Where(i => done.Contains(i.Path))) _items.Remove(item);
        SyncView();

        _valid = _items.Count(i => i.IsValid);
        _invalid = _items.Count(IsInvalid);
        _done = _valid + _invalid;
        OverallProgress.Maximum = Math.Max(1, _items.Count);
        _acting = false;
        UpdateState();
        UpdateSummary();

        var (severity, message) = FileActions.Describe(action, done.Count, errors);
        ShowInfo(severity, message);
    }

    // OPEN

    private void Results_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is VideoItem item) OpenVideo(item.Path);
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

    // STATE

    private void UpdateState()
    {
        var busy = Busy;
        var scanning = _cts is not null;
        ScanButton.Content = scanning ? "Cancel" : "Scan";
        ScanButton.IsEnabled = !busy || scanning;
        RefreshButton.IsEnabled = !busy && _scannedFolder is not null;
        FolderBox.IsEnabled = BrowseButton.IsEnabled = RecurseBox.IsEnabled = ParallelBox.IsEnabled = !busy;
        ExportButton.IsEnabled = ClearListButton.IsEnabled = !busy && _items.Count > 0;
        OverallProgress.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private void UpdateSummary()
    {
        OverallProgress.Value = _done;
        var checking = _items.Count(i => i.IsChecking);
        SummaryText.Text = $"{_items.Count} files: {_valid} valid, {_invalid} invalid, {checking} processing, {_items.Count - _done - checking} not checked";
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
