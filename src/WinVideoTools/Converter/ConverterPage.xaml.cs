using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace WinVideoTools.Converter;

public sealed partial class ConverterPage : Page
{
    private readonly ObservableCollection<ConvertItem> _items = [];
    // What the list shows: _items in the same order, narrowed by the status filter.
    private readonly ObservableCollection<ConvertItem> _view = [];
    private readonly PresetStore _store = PresetStore.Default;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinVideoTools", "converter.json");

    // Checking the box while loading fires Checked; do not write settings back before they are read.
    private bool _settingsLoaded;

    // SameFolder is null in files saved before the checkbox existed, when an empty folder meant "next to source".
    // "Shut down when finished" is deliberately not remembered. "Delete original" is, but Convert confirms it every run.
    private sealed record Settings(string OutputFolder, bool? SameFolder, bool DeleteLarger = false, bool SkipHevc = false, bool SkipHevcUpTo1080p = false, bool AutoScroll = true,
        bool Autosave = false, string? SessionPath = null, string? Preset = null, bool DeleteOriginal = false);
    private CancellationTokenSource? _cts;
    // Folders files were added from, for Rescan; saved with the session.
    private readonly List<string> _folders = [];

    // Rewritten shortly after every change and deleted on a clean close, so finding it at startup means the app did not exit cleanly.
    private static readonly string RecoveryPath = Path.Combine(Path.GetDirectoryName(SettingsPath)!, "converter-recovery.json");
    // The session file last saved or loaded; Autosave keeps it current.
    private string? _sessionPath;
    // Changes come in bursts (a status flip, then its result fields), so saving waits a moment to batch them,
    // counted from the first change so a steady stream of changes cannot put it off.
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(2) };
    // Writes run one after another so two never share the temporary file.
    private Task _saving = Task.CompletedTask;
    // Saving waits until startup has looked for a list to restore, so an empty startup list never replaces the recovery file first.
    private bool _restored;
    // True once the list or options changed since the session file was written or loaded; Autosave only rewrites it then.
    private bool _sessionFileDirty;
    // Whether the last write failed; a clean close then keeps the recovery file. Set on the save task's thread.
    private volatile bool _lastSaveFailed;
    // True while a folder is being listed; the queue is locked until its files are in.
    private bool _adding;
    // True while AddPaths fills _items, so the list syncs once at the end rather than once per file.
    private bool _bulkAdding;
    // Every file processed and folder added, across all lists and sessions.
    private static readonly string HistoryPath = Path.Combine(Path.GetDirectoryName(SettingsPath)!, "converter-history.json");
    private ConvertHistory _history = new();
    // False when an unreadable history could not be set aside; saving would then overwrite it.
    private bool _historyWritable = true;
    // History writes run one after another so two never share the temporary file.
    private Task _historySaving = Task.CompletedTask;
    // Non-null while paused; completing it lets the queue continue.
    private TaskCompletionSource? _resume;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public ConverterPage()
    {
        InitializeComponent();
        Queue.ItemsSource = _view;
        FilterBox.ItemsSource = (string[])["All", .. Enum.GetNames<ConvertStatus>()];
        FilterBox.SelectedIndex = 0;
        _clock.Tick += (_, _) =>
        {
            foreach (var item in _items.Where(i => i.IsConverting)) item.Tick();
        };
        _items.CollectionChanged += (_, e) =>
        {
            foreach (var item in e.NewItems?.OfType<ConvertItem>() ?? [])
                item.PropertyChanged += (_, p) =>
                {
                    if (p.PropertyName != nameof(ConvertItem.Status)) return;
                    SyncView();
                    SessionChanged();
                };
            if (_bulkAdding) return;
            SyncView();
            SetBusy(_cts is not null);
            SessionChanged();
        };
        _autosave.Tick += async (_, _) =>
        {
            _autosave.Stop();
            await SaveSessionNowAsync();
        };
        App.MainWindow!.AppWindow.Closing += (_, _) => OnAppClosing();
        var settings = LoadSettings();
        LoadPresets(settings.Preset);
        ApplyOptions(new ConvertOptions(null, settings.OutputFolder, settings.SameFolder ?? settings.OutputFolder.Length == 0,
            settings.SkipHevc, settings.SkipHevcUpTo1080p, settings.DeleteLarger, settings.DeleteOriginal, settings.AutoScroll));
        AutosaveItem.IsChecked = settings.Autosave;
        _settingsLoaded = true;
        LoadHistory();
        // Rescan opens up once the history has folders.
        SetBusy(false);
        FileDrop.Attach(this, "Add to queue", () => _cts is null && !_adding, OnDropAsync);
        WatchManualScrolling();
        RestoreOnStartup(settings);
    }

    // AUTO-SCROLL

    /// <summary>
    /// Scrolling by hand during a run turns auto-scroll off, so the list stops jumping away from what
    /// the user is reading. ViewChanged cannot tell user scrolls from ScrollIntoView, so this watches
    /// the input instead. handledEventsToo is needed because the list consumes these events itself.
    /// </summary>
    private void WatchManualScrolling()
    {
        Queue.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => StopAutoScroll()), handledEventsToo: true);
        Queue.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (IsInScrollBar(e.OriginalSource as DependencyObject)) StopAutoScroll();
        }), handledEventsToo: true);
        Queue.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key is VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End or VirtualKey.Up or VirtualKey.Down)
                StopAutoScroll();
        }), handledEventsToo: true);
        // Touch and precision-touchpad panning go through direct manipulation, not wheel events.
        Queue.Loaded += (_, _) =>
        {
            if (FindChild<ScrollViewer>(Queue) is { } viewer) viewer.DirectManipulationStarted += (_, _) => StopAutoScroll();
        };
    }

    private void StopAutoScroll()
    {
        if (_cts is not null && AutoScrollBox.IsChecked == true) AutoScrollBox.IsChecked = false;
    }

    private static bool IsInScrollBar(DependencyObject? d)
    {
        for (; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ScrollBar) return true;
        return false;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as T ?? FindChild<T>(child)) is { } found) return found;
        }
        return null;
    }

    // Dropped folders are searched like Add folder, including subfolders.
    private async Task OnDropAsync(IReadOnlyList<string> folders, IReadOnlyList<string> files)
    {
        AddPaths(files.Select(f => new FileInfo(f)));
        foreach (var folder in folders) await AddFolderAsync(folder);
    }

    // OUTPUT FOLDER

    // A remembered folder that no longer exists is dropped.
    private static Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) is { } s)
                return s with { OutputFolder = s.OutputFolder is { Length: > 0 } f && (IsNetworkPath(f) || Directory.Exists(f)) ? f : "" };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable settings just mean nothing remembered.
        }
        return new Settings("", true);
    }

    // UNC and device paths are kept unchecked at startup: a loaded session can set the output folder, and probing a
    // network path contacts that server and sends it Windows credentials before the user does anything.
    private static bool IsNetworkPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    private void SaveSettings()
    {
        if (!_settingsLoaded) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var o = CurrentOptions();
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(o.OutputFolder ?? "", o.SameFolder, o.DeleteLarger, o.SkipHevc, o.SkipHevcUpTo1080p, o.AutoScroll,
                AutosaveItem.IsChecked, _sessionPath, o.Preset, o.DeleteOriginal)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Forgetting the folder is not worth interrupting a conversion.
        }
    }

    private async void Options_Click(object sender, RoutedEventArgs e) => await OptionsDialog.ShowAsync();

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (SameFolderBox.IsChecked == true) OutputBox.Text = "";
        SetBusy(_cts is not null);
        SaveSettings();
        SessionChanged();
    }

    // The folder last set or seen in the box. TextChanged arrives after the code that set the text has finished,
    // so this tells a typed change from one ApplyOptions made.
    private string _outputText = "";

    // A typed folder reaches settings when Convert is pressed; the session picks it up on its next save.
    private void OutputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (OutputBox.Text == _outputText) return;
        _outputText = OutputBox.Text;
        SessionChanged();
    }

    private ConvertOptions CurrentOptions() => new(Preset?.Name, OutputBox.Text.Trim(), SameFolderBox.IsChecked == true,
        SkipHevcBox.IsChecked == true, SkipHevc1080Box.IsChecked == true, DeleteLargerBox.IsChecked == true,
        DeleteOriginalBox.IsChecked == true, AutoScrollBox.IsChecked == true);

    /// <summary>Sets every option, and the preset when one is named.</summary>
    /// <returns>A warning when the named preset is not saved here; the current preset then stays.</returns>
    private string? ApplyOptions(ConvertOptions o)
    {
        // Same-folder first: checking it clears the folder box, so the folder is filled in after.
        SameFolderBox.IsChecked = o.SameFolder;
        OutputBox.Text = _outputText = o.SameFolder ? "" : o.OutputFolder ?? "";
        SkipHevcBox.IsChecked = o.SkipHevc;
        SkipHevc1080Box.IsChecked = o.SkipHevcUpTo1080p;
        DeleteLargerBox.IsChecked = o.DeleteLarger;
        DeleteOriginalBox.IsChecked = o.DeleteOriginal;
        AutoScrollBox.IsChecked = o.AutoScroll;
        string? warning = null;
        if (o.Preset is { } name)
        {
            if ((PresetBox.ItemsSource as IEnumerable<HandBrakePreset>)?.FirstOrDefault(p => p.Name == name) is { } preset)
                PresetBox.SelectedItem = preset;
            else
                warning = $"The preset \"{name}\" is not saved here, so \"{Preset?.Name ?? "none"}\" stays selected.";
        }
        SetBusy(_cts is not null);
        SaveSettings();
        return warning;
    }

    // PRESETS

    private void LoadPresets(string? select)
    {
        var (presets, errors) = _store.Load();
        PresetBox.ItemsSource = presets;
        PresetBox.SelectedItem = presets.FirstOrDefault(p => p.Name == select) ?? presets.FirstOrDefault();
        if (errors.Count > 0) ShowInfo(InfoBarSeverity.Warning, "Some saved presets could not be read:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    private HandBrakePreset? Preset => PresetBox.SelectedItem as HandBrakePreset;

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var notes = Preset is { } p ? PresetConverter.Unsupported(p) : [];
        PresetNotes.Text = notes.Count == 0 ? "" : "Not applied from this preset:" + Environment.NewLine + string.Join(Environment.NewLine, notes.Select(n => "  • " + n));
        PresetNotes.Visibility = notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SetBusy(_cts is not null);
        SaveSettings();
        SessionChanged();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(App.MainWindow!.AppWindow.Id);
        picker.FileTypeFilter.Add(".json");
        if (await picker.PickSingleFileAsync() is not { } file) return;

        List<HandBrakePreset> presets;
        try
        {
            presets = await Task.Run(() => HandBrakePreset.ReadFile(file.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not import {Path.GetFileName(file.Path)}: {ex.Message}");
            return;
        }

        var clashes = presets.Where(p => _store.Exists(p.Name)).Select(p => p.Name).Distinct().ToList();
        if (clashes.Count > 0 && !await ConfirmAsync(
                clashes.Count == 1 ? $"Replace the saved preset \"{clashes[0]}\"?" : $"Replace {clashes.Count} saved presets?",
                string.Join(Environment.NewLine, clashes), "Replace"))
            return;

        try
        {
            foreach (var p in presets) _store.Save(p);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not save presets: {ex.Message}");
            return;
        }
        LoadPresets(presets[0].Name);
        ShowInfo(InfoBarSeverity.Success, presets.Count == 1 ? $"Imported \"{presets[0].Name}\"." : $"Imported {presets.Count} presets.");
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Preset is not { } preset) return;
        var picker = new FileSavePicker(App.MainWindow!.AppWindow.Id) { SuggestedFileName = preset.Name };
        picker.FileTypeChoices.Add("HandBrake preset", [".json"]);
        if (await picker.PickSaveFileAsync() is not { } result) return;
        try
        {
            await File.WriteAllTextAsync(result.Path, preset.ToFileJson());
            ShowInfo(InfoBarSeverity.Success, $"Saved {result.Path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not save: {ex.Message}");
        }
    }

    private async void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (Preset is not { } preset) return;
        if (!await ConfirmAsync($"Delete the preset \"{preset.Name}\"?", "Converted files are not affected.", "Delete")) return;
        try
        {
            _store.Delete(preset.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not delete: {ex.Message}");
            return;
        }
        LoadPresets(null);
    }

    // FILES

    private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (await FileActions.PickFolderAsync() is not { } folder) return;
        OutputBox.Text = folder;
        SaveSettings();
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(App.MainWindow!.AppWindow.Id);
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        AddPaths(files.Select(f => new FileInfo(f.Path)));
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await FileActions.PickFolderAsync() is { } folder) await AddFolderAsync(folder);
    }

    /// <returns>How many videos were queued, new or changed.</returns>
    private async Task<int> AddFolderAsync(string folder)
    {
        if (!_folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) _folders.Add(folder);
        _history.AddFolder(folder);
        SaveHistory();
        // Listing a large tree can take a while; show it moving. Reports can land after Find returns, so they stop at _adding.
        _adding = true;
        SetBusy(_cts is not null);
        SummaryText.Text = $"Finding videos in {folder}...";
        var found = new Progress<int>(n => { if (_adding) SummaryText.Text = $"Finding videos in {folder}... {n:N0} found"; });
        try
        {
            var files = await Task.Run(() => VideoFiles.Find(folder, recurse: true, CancellationToken.None, found));
            return AddPaths(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoBarSeverity.Error, ex.Message);
            return 0;
        }
        finally
        {
            _adding = false;
            SetBusy(_cts is not null);
            UpdateSummary();
        }
    }

    /// <summary>
    /// Queues new videos, leaving out ones the history says were processed and have not changed since.
    /// A listed file that finished but has changed since is queued again.
    /// Sizes and times come from the FileInfo; ones from a folder listing are already filled in, so no disk access per file.
    /// </summary>
    /// <returns>How many videos were queued, new or changed.</returns>
    private int AddPaths(IEnumerable<FileInfo> files)
    {
        var listed = _items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        bool skipHevc = SkipHevcBox.IsChecked == true, skipHevcUpTo1080p = SkipHevc1080Box.IsChecked == true;
        int skipped = 0, added = 0, leftOut = 0;
        _bulkAdding = true;
        try
        {
            foreach (var file in files)
            {
                if (!VideoFiles.IsVideo(file.FullName))
                {
                    skipped++;
                    continue;
                }
                var now = ConvertHistory.Stat(file);
                if (listed.TryGetValue(file.FullName, out var item))
                {
                    if (item.Status is ConvertStatus.Done or ConvertStatus.Skipped && _history.Changed(item.Path, now))
                    {
                        // The last run's result no longer describes this file.
                        item.InputSize = now!.Value.Size;
                        item.OutputSize = null;
                        item.SizeRatio = null;
                        item.Summary = "";
                        item.Decision = "Changed since it was processed";
                        item.Status = ConvertStatus.Queued;
                        added++;
                    }
                }
                else if (_history.IsProcessed(file.FullName, now, skipHevc, skipHevcUpTo1080p)) leftOut++;
                else
                {
                    item = new ConvertItem(file.FullName, now?.Size ?? 0);
                    _items.Add(item);
                    listed.Add(item.Path, item);
                    added++;
                }
            }
        }
        finally
        {
            _bulkAdding = false;
        }
        SyncView();
        SetBusy(_cts is not null);
        if (added > 0) SessionChanged();
        var notes = new List<string>();
        if (leftOut > 0) notes.Add($"Left out {leftOut} video{(leftOut == 1 ? "" : "s")} already processed; Rescan queues them if they change.");
        if (skipped > 0) notes.Add($"Skipped {skipped} file{(skipped == 1 ? "" : "s")} that are not videos.");
        if (notes.Count > 0) ShowInfo(InfoBarSeverity.Informational, string.Join(" ", notes));
        UpdateSummary();
        return added;
    }

    // Looks in this list's folders and every folder in the history. Files already listed keep their status unless
    // they changed since they finished, and ones gone from disk stay listed with their results.
    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        int added = 0, missing = 0;
        foreach (var folder in _folders.Concat(_history.Folders).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (Directory.Exists(folder)) added += await AddFolderAsync(folder);
            else missing++;
        }
        var message = added == 0 ? "No new or changed videos found." : $"Queued {added} new or changed video{(added == 1 ? "" : "s")}.";
        if (missing > 0) message += $" {missing} folder{(missing == 1 ? " was" : "s were")} not found.";
        ShowInfo(missing > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational, message);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Queue.SelectedItems.OfType<ConvertItem>().ToList()) _items.Remove(item);
        UpdateSummary();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _items.Clear();
        _folders.Clear();
        // Detach before the debounced save runs, so Autosave never overwrites a saved session with an empty list.
        _sessionPath = null;
        // Autosave needs a file; saving the new list as a session turns it back on.
        var autosaveWasOn = AutosaveItem.IsChecked;
        AutosaveItem.IsChecked = false;
        SaveSettings();
        DetailsBox.Text = "";
        SetBusy(_cts is not null);
        UpdateSummary();
        if (autosaveWasOn) ShowInfo(InfoBarSeverity.Informational, "Autosave is off, as the list is no longer tied to a session file. Save a session to turn it back on.");
    }

    // SESSION

    private void SaveSession_Click(object sender, RoutedEventArgs e) => _ = SaveSessionAsAsync();

    /// <summary>Asks where to save, writes the session there and makes it the file Autosave keeps current.</summary>
    /// <returns>False when the picker was cancelled or the write failed.</returns>
    private async Task<bool> SaveSessionAsAsync()
    {
        var picker = new FileSavePicker(App.MainWindow!.AppWindow.Id) { SuggestedFileName = "Convert session" };
        picker.FileTypeChoices.Add("Convert session", [".json"]);
        if (await picker.PickSaveFileAsync() is not { } result) return false;
        _sessionPath = result.Path;
        SaveSettings();
        if (!await SaveSessionNowAsync(always: true)) return false;
        ShowInfo(InfoBarSeverity.Success, $"Saved {result.Path}");
        return true;
    }

    private async void LoadSession_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count > 0 && !await ConfirmAsync("Replace the current list?",
                "The files in the list now are removed and replaced with the saved session. Converted files are not affected.", "Load"))
            return;
        var picker = new FileOpenPicker(App.MainWindow!.AppWindow.Id);
        picker.FileTypeFilter.Add(".json");
        if (await picker.PickSingleFileAsync() is not { } file) return;
        if (await ReadSessionAsync(file.Path) is not { } session) return;
        // A run may have started while the picker was open.
        if (_cts is not null || _adding) return;
        var warning = ApplySession(session, file.Path);
        // A session can come from elsewhere; say where conversions will now be written.
        var output = session.Options is { SameFolder: false, OutputFolder: { Length: > 0 } f } ? $" Output folder: {f}." : "";
        ShowInfo(warning is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            $"Loaded {session.Items.Count} files.{output} Rescan folders to queue videos added since.{(warning is null ? "" : " " + warning)}");
    }

    private async void Autosave_Click(object sender, RoutedEventArgs e)
    {
        // Autosave needs a file to write to; without one, ask for it now.
        if (AutosaveItem.IsChecked && _sessionPath is null && !await SaveSessionAsAsync()) AutosaveItem.IsChecked = false;
        SaveSettings();
        if (AutosaveItem.IsChecked) SessionChanged();
    }

    /// <summary>
    /// A recovery file left behind means the app did not close cleanly, so its list comes back. Otherwise,
    /// with Autosave on, the session file it was keeping current reopens.
    /// </summary>
    private async void RestoreOnStartup(Settings settings)
    {
        bool applied = false, recovering = File.Exists(RecoveryPath);
        try
        {
            var session = recovering ? await ReadSessionAsync(RecoveryPath) : null;
            var badRecovery = recovering && session is null;
            if (badRecovery)
            {
                // Set aside rather than left for the next save to delete, then fall back to the session file below.
                KeepUnreadableRecoveryFile();
                recovering = false;
            }
            if (session is null && settings.Autosave && settings.SessionPath is { } reopen) session = await ReadSessionAsync(reopen);
            if (session is null) return;
            // Something may have been added while the file was read; never replace it.
            if (_items.Count > 0 || _cts is not null || _adding) return;
            // Settings hold the session file the list belonged to: every change to it is saved, and the finally below clears a stale one.
            var warning = ApplySession(session, settings.SessionPath);
            applied = true;
            ShowInfo(warning is null && !badRecovery ? InfoBarSeverity.Informational : InfoBarSeverity.Warning, (recovering
                ? $"Recovered {session.Items.Count} files from when the app last closed unexpectedly."
                : $"Reopened {settings.SessionPath}")
                + (badRecovery ? $" The list from the last unexpected close could not be read and was kept as {RecoveryPath}.bad." : "")
                + (warning is null ? "" : " " + warning));
        }
        finally
        {
            // Autosave never stays on without a file to write to.
            if (AutosaveItem.IsChecked && _sessionPath is null)
            {
                AutosaveItem.IsChecked = false;
                ShowInfo(InfoBarSeverity.Warning, $"Autosave is off: could not reopen {settings.SessionPath ?? "the session file"}. Load or save a session to turn it back on.");
            }
            // Brings the remembered session file in line with _sessionPath, so a later crash recovery attaches the right one.
            SaveSettings();
            // Anything changed while the file was read is saved now.
            _restored = true;
            SessionChanged();
            // A reopened session matches its file, so only real changes rewrite it. A recovered list can be newer than
            // the file (its last save may have failed), so it stays marked changed and Autosave writes it out.
            if (applied && !recovering) _sessionFileDirty = false;
        }
    }

    private static void KeepUnreadableRecoveryFile()
    {
        try
        {
            File.Move(RecoveryPath, RecoveryPath + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Then the next save replaces it.
        }
    }

    private async Task<ConvertSession?> ReadSessionAsync(string path)
    {
        try
        {
            return await Task.Run(() => ConvertSession.Read(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not load {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <returns>A warning when the session's preset is not saved here.</returns>
    private string? ApplySession(ConvertSession session, string? sessionPath)
    {
        _bulkAdding = true;
        try
        {
            _items.Clear();
            foreach (var i in session.Items)
                _items.Add(new ConvertItem(i.Path, i.InputSize)
                {
                    Status = i.Status,
                    Summary = i.Summary ?? "",
                    Decision = i.Decision ?? "",
                    Details = i.Details ?? "",
                    OutputPath = i.OutputPath,
                    OutputSize = i.OutputSize,
                    SizeRatio = i.SizeRatio,
                    OriginalDeleted = i.OriginalDeleted,
                    SkippedUpTo1080p = i.SkippedUpTo1080p,
                });
        }
        finally
        {
            _bulkAdding = false;
        }
        _folders.Clear();
        _folders.AddRange(session.Folders);
        _sessionPath = sessionPath;
        // Older sessions have no options; the current ones then stay.
        var warning = session.Options is { } o ? ApplyOptions(o) : null;
        SaveSettings();
        DetailsBox.Text = "";
        SyncView();
        SetBusy(false);
        UpdateSummary();
        // The recovery file follows the new list, but the session file it came from is only rewritten once something changes.
        SessionChanged();
        _sessionFileDirty = false;
        return warning;
    }

    private void SessionChanged()
    {
        if (!_restored) return;
        _sessionFileDirty = true;
        if (!_autosave.IsEnabled) _autosave.Start();
    }

    /// <summary>
    /// Writes the recovery file, and the session file too when Autosave is on or <paramref name="always"/> is set.
    /// An empty list deletes the recovery file instead, as there is nothing to recover.
    /// </summary>
    /// <returns>False when a write failed; the error is shown.</returns>
    private async Task<bool> SaveSessionNowAsync(bool always = false)
    {
        var session = new ConvertSession([.. _folders], _items.Select(i => new SessionItem(i.Path, i.InputSize, i.Status, i.Summary,
            i.Decision, i.Details, i.OutputPath, i.OutputSize, i.SizeRatio, i.OriginalDeleted, i.SkippedUpTo1080p)).ToList(), CurrentOptions());
        var target = always || (AutosaveItem.IsChecked && _sessionFileDirty) ? _sessionPath : null;
        if (target is not null) _sessionFileDirty = false;
        var previous = _saving;
        var saving = Task.Run(async () =>
        {
            await previous;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RecoveryPath)!);
                if (session.Items.Count == 0) File.Delete(RecoveryPath);
                else session.Write(RecoveryPath);
                if (target is not null) session.Write(target);
                _lastSaveFailed = false;
            }
            catch
            {
                _lastSaveFailed = true;
                throw;
            }
        });
        // Later writes wait for this one but must not fail because it did.
        _saving = saving.ContinueWith(_ => { }, TaskScheduler.Default);
        try
        {
            await saving;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (target is not null) _sessionFileDirty = true;
            ShowInfo(InfoBarSeverity.Error, $"Could not save the session: {ex.Message}");
            return false;
        }
    }

    // A clean close needs no recovery. Any pending autosave runs first, and the window waits for it, so the session file is current.
    // If that save failed or is still running, the recovery file stays and the list comes back next start.
    private void OnAppClosing()
    {
        _autosave.Stop();
        // Waits on _saving, not the async method: its continuation needs this (blocked) UI thread. It sets _saving before yielding.
        if (AutosaveItem.IsChecked && _sessionFileDirty && _sessionPath is not null) _ = SaveSessionNowAsync();
        _historySaving.Wait(TimeSpan.FromSeconds(5));
        try
        {
            if (_saving.Wait(TimeSpan.FromSeconds(5)) && !_lastSaveFailed) File.Delete(RecoveryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AggregateException)
        {
            // Left behind, the list just comes back next start.
        }
    }

    // HISTORY

    // An unreadable history is set aside rather than overwritten by the next save.
    private void LoadHistory()
    {
        if (!File.Exists(HistoryPath)) return;
        try
        {
            _history = ConvertHistory.Read(HistoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try
            {
                File.Move(HistoryPath, HistoryPath + ".bad", overwrite: true);
                ShowInfo(InfoBarSeverity.Warning, $"The convert history could not be read and was kept as {HistoryPath}.bad, so it starts empty: {ex.Message}");
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                _historyWritable = false;
                ShowInfo(InfoBarSeverity.Warning, $"The convert history could not be read, so nothing is remembered until the app restarts: {ex.Message}");
            }
        }
    }

    // ponytail: rewrites the whole file per change; fine for tens of thousands of files, an append log if it ever is not.
    private void SaveHistory()
    {
        if (!_historyWritable) return;
        var json = _history.ToJson();
        _historySaving = _historySaving.ContinueWith(_ =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
                ConvertHistory.WriteJson(HistoryPath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DispatcherQueue.TryEnqueue(() => ShowInfo(InfoBarSeverity.Warning, $"Could not save the convert history: {ex.Message}"));
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Remembers a finished file, and an output it wrote under another name, so neither is queued again until it changes.
    /// Failed and cancelled files are not remembered, so they are tried again.
    /// </summary>
    private async Task RecordAsync(ConvertItem item)
    {
        HistoryOutcome? outcome = item.Status switch
        {
            ConvertStatus.Skipped => HistoryOutcome.Skipped,
            ConvertStatus.Done when item.OutputPath is null && !item.OriginalDeleted => HistoryOutcome.Discarded,
            ConvertStatus.Done => HistoryOutcome.Converted,
            _ => null,
        };
        if (outcome is null) return;
        var output = item.OutputPath is { } o && !string.Equals(o, item.Path, StringComparison.OrdinalIgnoreCase) ? o : null;
        // Off the UI thread, as either file may be on a network share.
        var (source, written) = await Task.Run(() =>
            (ConvertHistory.Stat(new FileInfo(item.Path)), output is null ? null : ConvertHistory.Stat(new FileInfo(output))));
        if (source is { } s) _history.Record(item.Path, outcome.Value, s.Size, s.Modified, item.SkippedUpTo1080p);
        if (written is { } w) _history.Record(output!, HistoryOutcome.Output, w.Size, w.Modified);
        SaveHistory();
    }

    private async void ImportHistory_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(App.MainWindow!.AppWindow.Id);
        picker.FileTypeFilter.Add(".json");
        if (await picker.PickSingleFileAsync() is not { } file) return;
        ConvertHistory imported;
        try
        {
            imported = await Task.Run(() => ConvertHistory.Read(file.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not import {Path.GetFileName(file.Path)}: {ex.Message}");
            return;
        }
        _history.Merge(imported);
        SaveHistory();
        SetBusy(_cts is not null);
        ShowInfo(InfoBarSeverity.Success, $"Added {imported.Count} files and {imported.Folders.Count} folders to the convert history.");
    }

    private async void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(App.MainWindow!.AppWindow.Id) { SuggestedFileName = "Convert history" };
        picker.FileTypeChoices.Add("Convert history", [".json"]);
        if (await picker.PickSaveFileAsync() is not { } result) return;
        var json = _history.ToJson();
        try
        {
            await Task.Run(() => ConvertHistory.WriteJson(result.Path, json));
            ShowInfo(InfoBarSeverity.Success, $"Saved {result.Path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not save: {ex.Message}");
        }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Clear the convert history?",
                $"Forgets {_history.Count} processed files and {_history.Folders.Count} folders, so adding those folders queues every video again. "
                + "Converted files are not affected. Export the history first to keep a copy.", "Clear"))
            return;
        _history.Clear();
        SaveHistory();
        SetBusy(_cts is not null);
    }

    // FILTER

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncView();

    /// <summary>
    /// Brings the shown list in line with the filter by removing and inserting in place, rather than
    /// rebuilding it, so a status change mid-run does not reset scrolling or selection.
    /// </summary>
    private void SyncView()
    {
        var all = !Enum.TryParse<ConvertStatus>(FilterBox.SelectedItem as string, out var status);
        var want = _items.Where(i => all || i.Status == status).ToList();
        var keep = want.ToHashSet();
        for (var i = _view.Count - 1; i >= 0; i--)
            if (!keep.Contains(_view[i])) _view.RemoveAt(i);
        // _view is now an in-order subset of want, so anything out of step at i is missing there.
        for (var i = 0; i < want.Count; i++)
            if (i >= _view.Count || _view[i] != want[i]) _view.Insert(i, want[i]);
    }

    private void Queue_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Ticking a row adds to the selection, so show the file just ticked; otherwise show the selected file, if only one is.
        DetailsBox.Text = e.AddedItems is [ConvertItem added] ? added.Details
            : Queue.SelectedItems.Count == 1 && Queue.SelectedItems[0] is ConvertItem item ? item.Details : "";
        SetBusy(_cts is not null);
    }

    // Arrow keys move focus without ticking rows, so the log follows the focused row too.
    private void Queue_GotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ListViewItem { Content: ConvertItem item }) DetailsBox.Text = item.Details;
    }

    // OPEN

    // Double-click opens the result when there is one, otherwise the source.
    private void Queue_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ConvertItem item)
            OpenVideo(item.OutputPath ?? item.Path);
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

    // Copies the full path even if the original has since been moved or deleted; it is just text.
    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(Path.GetFullPath(path));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not copy the path for {path}: {ex.Message}");
        }
    }

    // CONVERT

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null || Preset is not { } preset) return;
        if (!await FfmpegPrompt.EnsureAsync(XamlRoot)) return;
        var ffmpeg = Ffmpeg.ExePath;
        // Empty means next to each source.
        var outFolder = SameFolderBox.IsChecked == true ? "" : OutputBox.Text.Trim();
        if (SameFolderBox.IsChecked != true && !Directory.Exists(outFolder))
        {
            ShowInfo(InfoBarSeverity.Error, outFolder.Length == 0
                ? "Choose an output folder, or tick \"Save next to the source file\"."
                : "Output folder not found.");
            return;
        }
        SaveSettings();

        var deleteLarger = DeleteLargerBox.IsChecked == true;
        var deleteOriginal = DeleteOriginalBox.IsChecked == true;
        var skipHevc = SkipHevcBox.IsChecked == true;
        var skipHevcUpTo1080p = SkipHevc1080Box.IsChecked == true;

        // Finished files stay done, and skipped ones stay skipped while the options would skip them again;
        // everything else (queued, failed, cancelled, or a skip the options no longer allow) runs again.
        var pending = _items.Where(i => i.Status switch
        {
            ConvertStatus.Done => false,
            ConvertStatus.Skipped => !PresetConverter.StillSkipped(i.SkippedUpTo1080p, skipHevc, skipHevcUpTo1080p),
            _ => true,
        }).ToList();
        if (pending.Count == 0)
        {
            ShowInfo(InfoBarSeverity.Informational, "Every file in the list is already converted or skipped.");
            return;
        }
        if (deleteOriginal && !await ConfirmAsync("Delete originals that shrink?",
                "When a converted file is smaller than its source and the full length, the source file is permanently deleted. This cannot be undone.",
                "Convert"))
            return;

        // Failed, cancelled and re-run skipped files are waiting again, so the summary and filter count them that way now,
        // not only once the queue reaches them. ConvertOneAsync clears the rest of the old result when each one starts.
        foreach (var item in pending.Where(i => i.Status != ConvertStatus.Queued))
        {
            item.Summary = "";
            item.Decision = "";
            item.Status = ConvertStatus.Queued;
        }
        UpdateSummary();

        Info.IsOpen = false;
        _cts = new CancellationTokenSource();
        _clock.Start();
        var ct = _cts.Token;
        // Reset here, not in SetBusy, which also runs on every selection change and pause mid-run.
        OverallProgress.Value = 0;
        SetBusy(true);
        var done = 0;
        var finished = false;
        try
        {
            // One file at a time: ffmpeg already uses every core, and consumer GPUs limit encoder sessions.
            foreach (var item in pending)
            {
                ct.ThrowIfCancellationRequested();
                if (_resume is { } resume)
                {
                    ShowInfo(InfoBarSeverity.Informational, "Paused. Press Resume to continue with the queued files.");
                    await resume.Task.WaitAsync(ct);
                    Info.IsOpen = false;
                }
                if (!_items.Contains(item)) continue;
                // Another list or session may have processed this file since it was queued.
                var now = await Task.Run(() => ConvertHistory.Stat(new FileInfo(item.Path)));
                if (_history.IsProcessed(item.Path, now, skipHevc, skipHevcUpTo1080p) && _history.Find(item.Path) is { } known)
                {
                    item.SkippedUpTo1080p = known.SkippedUpTo1080p;
                    item.Summary = "Already processed";
                    item.Decision = $"Unchanged since the convert history recorded it as {known.Outcome.ToString().ToLowerInvariant()}";
                    item.Status = known.Outcome == HistoryOutcome.Skipped ? ConvertStatus.Skipped : ConvertStatus.Done;
                    OverallProgress.Value = (double)++done / pending.Count;
                    continue;
                }
                if (AutoScrollBox.IsChecked == true && _view.Contains(item)) Queue.ScrollIntoView(item);
                var converting = ConvertOneAsync(item, preset, ffmpeg, outFolder, deleteLarger, deleteOriginal, skipHevc, skipHevcUpTo1080p, ct);
                UpdateSummary();
                try { await converting; }
                // The status flips before the result fields are filled in, so save again once the file is fully done.
                finally { UpdateSummary(); SessionChanged(); }
                await RecordAsync(item);
                OverallProgress.Value = (double)++done / pending.Count;
            }
            finished = true;
        }
        catch (OperationCanceledException)
        {
            ShowInfo(InfoBarSeverity.Warning, "Conversion cancelled.");
        }
        finally
        {
            _clock.Stop();
            _cts.Dispose();
            _cts = null;
            _resume = null;
            SetBusy(false);
            UpdateSummary();
        }

        // Only a queue that ran to the end shuts down; Cancel never does. The box is read now, so it can be changed mid-run.
        if (finished && ShutdownBox.IsChecked == true) await ShutdownAfterCountdownAsync();
    }

    // SHUTDOWN

    private const int ShutdownCountdownSeconds = 60;

    /// <summary>Gives anyone at the machine a minute to stop the shutdown, then asks Windows to shut down.</summary>
    private async Task ShutdownAfterCountdownAsync()
    {
        var remaining = ShutdownCountdownSeconds;
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Update() => text.Text = $"The conversion queue has finished. Windows will shut down in {remaining} seconds.";
        Update();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Shutting down",
            Content = text,
            PrimaryButtonText = "Shut down now",
            CloseButtonText = "Cancel shutdown",
            DefaultButton = ContentDialogButton.Close,
        };
        var timedOut = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (--remaining > 0) { Update(); return; }
            timedOut = true;
            dialog.Hide();
        };
        timer.Start();
        var result = await dialog.ShowAsync();
        timer.Stop();

        if (!timedOut && result != ContentDialogResult.Primary)
        {
            ShutdownBox.IsChecked = false;
            ShowInfo(InfoBarSeverity.Informational, "Shutdown cancelled.");
            return;
        }
        try
        {
            // Fixed system path and fixed arguments; nothing from the user or the files reaches this command.
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in (string[])["/s", "/t", "0"]) psi.ArgumentList.Add(arg);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowInfo(InfoBarSeverity.Error, $"Could not shut down: {ex.Message}");
        }
    }

    private void Pause_Click(SplitButton sender, SplitButtonClickEventArgs e)
    {
        if (_resume is null)
        {
            _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ShowInfo(InfoBarSeverity.Informational, "Pausing: the current file will finish, then the queue waits.");
        }
        else
        {
            var resume = _resume;
            _resume = null;
            resume.SetResult();
        }
        SetBusy(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // Runs on the UI thread; only the ffmpeg work inside PresetConverter leaves it.
    private static async Task ConvertOneAsync(ConvertItem item, HandBrakePreset preset, string ffmpeg, string outFolder,
        bool deleteLarger, bool deleteOriginal, bool skipHevc, bool skipHevcUpTo1080p, CancellationToken ct)
    {
        item.StartedUtc = DateTime.UtcNow;
        item.Status = ConvertStatus.Converting;
        item.Progress = 0;
        item.Summary = "";
        item.Decision = "";
        item.SizeRatio = null;
        item.OutputSize = null;
        item.OutputPath = null;
        item.SkippedUpTo1080p = false;
        var command = "";
        try
        {
            if (skipHevc && await PresetConverter.ProbeAsync(ffmpeg, item.Path, ct) is var src
                && PresetConverter.ShouldSkipHevc(src, skipHevcUpTo1080p))
            {
                var v = src.Streams.First(s => s.Kind == "Video" && !s.AttachedPic);
                item.SkippedUpTo1080p = PresetConverter.ShouldSkipHevc(src, onlyUpTo1080p: true);
                item.Summary = "Skipped";
                item.Decision = v.Width > 0 ? $"Original kept: already HEVC at {v.Width}x{v.Height}" : "Original kept: video is already HEVC";
                item.Status = ConvertStatus.Skipped;
                return;
            }

            var output = PresetConverter.OutputPathFor(item.Path, outFolder, preset);
            var result = await PresetConverter.ConvertAsync(ffmpeg, preset, item.Path, output,
                new Progress<double>(p => item.Progress = p), c => command = c, ct);
            item.Summary = result.Success ? $"{Path.GetFileName(output)}: {result.Summary}" : result.Summary;
            item.Details = $"{command}{Environment.NewLine}{Environment.NewLine}{result.Log}";
            item.Status = result.Success ? ConvertStatus.Done : ConvertStatus.Failed;
            var fileDecision = "Original kept; no output written";
            if (result.Success)
            {
                long inSize = new FileInfo(item.Path).Length, outSize = new FileInfo(output).Length;
                item.OutputPath = output;
                item.InputSize = inSize;
                item.OutputSize = outSize;
                if (inSize > 0) item.SizeRatio = (double)outSize / inSize;
                var original = output;
                (fileDecision, output) = await Task.Run(() => ApplySizeRules(item.Path, original, inSize, outSize, deleteLarger, deleteOriginal));
                // A renamed output now sits at the source path, so the source existing no longer means it was kept.
                item.OriginalDeleted = output != original || !File.Exists(item.Path);
                item.OutputPath = File.Exists(output) ? output : null;
                item.Summary = $"{Path.GetFileName(output)}: {result.Summary}";
            }
            item.Decision = string.Join("; ", [fileDecision, .. result.Notes]);
        }
        catch (OperationCanceledException)
        {
            item.Summary = "Cancelled";
            item.Decision = "Original kept; partial output removed";
            item.Status = ConvertStatus.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            item.Summary = ex.Message;
            item.Decision = "Original kept; no output written";
            item.Details = command;
            item.Status = ConvertStatus.Failed;
        }
    }

    /// <summary>
    /// After a successful conversion: drop an output that grew, or delete a source that shrank.
    /// ConvertAsync already rejected short outputs, so a smaller file here is a complete one.
    /// </summary>
    /// <returns>What was done with the two files, for the Decision column, and where the output ended up.</returns>
    private static (string Decision, string Output) ApplySizeRules(string source, string output, long inSize, long outSize, bool deleteLarger, bool deleteOriginal)
    {
        try
        {
            if (outSize > inSize)
            {
                if (!deleteLarger) return ("Kept both; output is larger", output);
                File.Delete(output);
                return ("Output deleted: larger than original", output);
            }
            if (outSize < inSize)
            {
                if (!deleteOriginal) return ("Kept both", output);
                File.Delete(source);
            }
            else return ("Kept both; same size", output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ($"Kept both; could not delete {(outSize > inSize ? "output" : "original")}: {ex.Message}", output);
        }

        // OutputPathFor numbered the output only because the source held its name; with the source gone, take the name back.
        var wanted = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(source) + Path.GetExtension(output));
        if (!string.Equals(Path.GetFullPath(wanted), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            return ("Original deleted: output is smaller", output);
        try
        {
            File.Move(output, source);
            return ("Original deleted: output is smaller; output renamed to original name", source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ($"Original deleted: output is smaller; could not rename output: {ex.Message}", output);
        }
    }

    // STATE

    private void SetBusy(bool busy)
    {
        var hasPreset = Preset is not null;
        // Keyboard focus follows the swap, so it is not dropped when the focused button collapses.
        var moveFocus = (busy ? ConvertButton.FocusState : RunButton.FocusState) != FocusState.Unfocused;
        ConvertButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ConvertButton.IsEnabled = !_adding && hasPreset && _items.Count > 0;
        RunButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RunButton.Content = _resume is null ? "Pause" : "Resume";
        if (moveFocus) (busy ? (Control)RunButton : ConvertButton).Focus(FocusState.Programmatic);
        PresetBox.IsEnabled = ImportButton.IsEnabled = !busy;
        ExportButton.IsEnabled = DeletePresetButton.IsEnabled = !busy && hasPreset;
        // Options are read when Convert is pressed, so the dialog stays viewable but read-only while a queue runs.
        // An option that depends on another is greyed out while that one is off.
        SkipHevcBox.IsEnabled = DeleteLargerBox.IsEnabled = DeleteOriginalBox.IsEnabled = !busy;
        SkipHevc1080Box.IsEnabled = !busy && SkipHevcBox.IsChecked == true;
        SameFolderBox.IsEnabled = !busy;
        ListButton.IsEnabled = !busy && !_adding;
        OutputBox.IsEnabled = BrowseOutputButton.IsEnabled = !busy && SameFolderBox.IsChecked != true;
        // Hidden by opacity, not collapsed, so it keeps its place in the toolbar; disabled, it takes no clicks or focus.
        RemoveButton.Opacity = Queue.SelectedItems.Count > 0 ? 1 : 0;
        RemoveButton.IsEnabled = !busy && !_adding && Queue.SelectedItems.Count > 0;
        // The List dropdown is off while busy or adding, so these only track whether there is anything to act on.
        ClearItem.IsEnabled = _items.Count > 0;
        RescanItem.IsEnabled = _folders.Count > 0 || _history.Folders.Count > 0;
        SaveSessionItem.IsEnabled = !_adding && _items.Count > 0;
        LoadSessionItem.IsEnabled = !busy && !_adding;
        OverallProgress.Visibility = busy || _adding ? Visibility.Visible : Visibility.Collapsed;
        OverallProgress.IsIndeterminate = _adding && !busy;
    }

    private void UpdateSummary()
    {
        int Count(ConvertStatus s) => _items.Count(i => i.Status == s);
        int done = Count(ConvertStatus.Done), failed = Count(ConvertStatus.Failed), converting = Count(ConvertStatus.Converting),
            skipped = Count(ConvertStatus.Skipped);
        SummaryText.Text = _items.Count == 0 ? "" :
            $"{_items.Count} files: {done} done, {skipped} skipped, {failed} failed, {converting} converting, {_items.Count - done - skipped - failed - converting} waiting{SavedText()}";
    }

    // Only finished files whose output still exists count; an output deleted for being larger saved nothing.
    private string SavedText()
    {
        long before = 0, after = 0;
        foreach (var i in _items.Where(i => i.Status == ConvertStatus.Done && i.OutputPath is not null && i.OutputSize is not null))
        {
            before += i.InputSize;
            after += i.OutputSize!.Value;
        }
        if (before == 0) return "";
        var saved = before - after;
        return saved >= 0
            ? $" · {PresetConverter.FormatSize(saved)} saved ({100.0 * saved / before:0}%)"
            : $" · {PresetConverter.FormatSize(-saved)} larger";
    }

    private async Task<bool> ConfirmAsync(string title, string body, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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
