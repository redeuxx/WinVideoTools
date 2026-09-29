using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace WinVideoTools.VideoVerifier;

public sealed partial class VideoVerifierPage : Page
{
    private readonly ObservableCollection<VideoItem> _items = [];
    private CancellationTokenSource? _cts;
    private int _valid, _invalid, _done;
    private bool _busy;
    private string? _scannedFolder;

    public VideoVerifierPage()
    {
        InitializeComponent();
        Results.ItemsSource = _items;
        // ffmpeg already decodes multithreaded, so a few files at once saturates most CPUs.
        ParallelBox.Value = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        FileDrop.Attach(this, "Scan this folder", () => !_busy, OnDropAsync);
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
        if (!_busy) await ScanAsync(refresh: true);
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
        if (!refresh) DetailsBox.Text = "";
        _valid = _invalid = _done = 0;
        _scannedFolder = folder;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);

        try
        {
            var recurse = RecurseBox.IsChecked == true;
            var files = await Task.Run(() => VideoFiles.Find(folder, recurse, ct), ct);

            foreach (var f in files)
            {
                var item = previous.TryGetValue(f.FullName, out var old)
                           && old.Status is not (VideoStatus.Pending or VideoStatus.Checking)
                           && VideoFiles.IsUnchanged(f, old.Size, old.ModifiedUtc)
                    ? old
                    : new VideoItem(f.FullName, Path.GetRelativePath(folder, f.FullName), f.Length, f.LastWriteTimeUtc);
                _items.Add(item);
                if (selected.Contains(item.Path)) Results.SelectedItems.Add(item);
            }
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
            SetBusy(false);
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
        UpdateSummary();
    }

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
            await File.WriteAllTextAsync(result.Path, sb.ToString(), Encoding.UTF8);
            ShowInfo(InfoBarSeverity.Success, $"Saved {result.Path}");
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not save: {ex.Message}");
        }
    }

    private void ClearList_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _items.Clear();
        _valid = _invalid = _done = 0;
        _scannedFolder = null;
        DetailsBox.Text = "";
        Info.IsOpen = false;
        SummaryText.Text = "";
        SetBusy(false);
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
            DetailsBox.Text = string.IsNullOrEmpty(shown.Details) ? shown.Summary : $"{shown.Summary}{Environment.NewLine}{Environment.NewLine}{shown.Details}";
        else if (Results.SelectedItems.Count == 0)
            DetailsBox.Text = "";
        UpdateSelection();
    }

    private static bool IsInvalid(VideoItem i) => i.Status is VideoStatus.Invalid or VideoStatus.Failed;

    private void SelectValid_Click(object sender, RoutedEventArgs e) => SelectWhere(i => i.IsValid);

    private void SelectInvalid_Click(object sender, RoutedEventArgs e) => SelectWhere(IsInvalid);

    private void Clear_Click(object sender, RoutedEventArgs e) => SelectWhere(_ => false);

    private void SelectWhere(Func<VideoItem, bool> select)
    {
        Results.SelectedItems.Clear();
        foreach (var item in _items.Where(select)) Results.SelectedItems.Add(item);
    }

    private void UpdateSelection()
    {
        var count = Results.SelectedItems.Count;
        SelectionText.Text = count == 0 ? "" : $"{count} selected";
        RecycleButton.IsEnabled = MoveButton.IsEnabled = DeleteButton.IsEnabled = !_busy && count > 0;
        SelectValidButton.IsEnabled = SelectInvalidButton.IsEnabled = ClearButton.IsEnabled = !_busy && _items.Count > 0;
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
        var selected = Results.SelectedItems.OfType<VideoItem>().ToList();
        if (selected.Count == 0) return;

        // Guard against "Select valid" clicked by mistake before a destructive action.
        var valid = selected.Count(i => i.IsValid);
        var warning = action == FileAction.Move || valid == 0 ? null
            : $"{valid} of the selected files passed verification.";
        if (!await FileActions.ConfirmAsync(XamlRoot, action, selected.Count, target, warning)) return;

        SetBusy(true);
        var (done, errors) = await FileActions.ApplyAsync(action, selected.Select(i => i.Path).ToList(), target);
        foreach (var item in selected.Where(i => done.Contains(i.Path))) _items.Remove(item);

        _valid = _items.Count(i => i.IsValid);
        _invalid = _items.Count(IsInvalid);
        _done = _valid + _invalid;
        SetBusy(false);
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

    // STATE

    private void SetBusy(bool busy)
    {
        _busy = busy;
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
        SummaryText.Text = $"{_items.Count} files: {_valid} valid, {_invalid} invalid, {_items.Count - _done} not checked";
    }

    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        Info.Severity = severity;
        Info.Message = message;
        Info.IsOpen = true;
    }
}
