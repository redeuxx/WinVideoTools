using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Windows.Storage.Pickers;

namespace WinVideoTools;

public enum FileAction { Recycle, Move, Delete }

/// <summary>Recycle, move or permanently delete files picked from a tool's results.</summary>
public static class FileActions
{
    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(App.MainWindow!.AppWindow.Id);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    /// <summary>Asks before acting. Cancel is the default button, so a stray Enter does nothing.</summary>
    public static async Task<bool> ConfirmAsync(XamlRoot root, FileAction action, int count, string? target, params string?[] warnings)
    {
        var files = count == 1 ? "1 file" : $"{count} files";
        string?[] lines = [action == FileAction.Delete ? "This cannot be undone." : null, .. warnings];
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = action switch
            {
                FileAction.Recycle => $"Move {files} to the Recycle Bin?",
                FileAction.Move => $"Move {files} to {target}?",
                _ => $"Permanently delete {files}?",
            },
            Content = new TextBlock
            {
                Text = string.Join(Environment.NewLine + Environment.NewLine, lines.OfType<string>()),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = action switch
            {
                FileAction.Recycle => "Move to Recycle Bin",
                FileAction.Move => "Move",
                _ => "Delete permanently",
            },
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Applies the action to each file off the UI thread. One failure does not stop the rest.</summary>
    public static Task<(HashSet<string> Done, List<string> Errors)> ApplyAsync(FileAction action, IReadOnlyList<string> paths, string? target) =>
        Task.Run(() =>
        {
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            foreach (var path in paths)
            {
                try
                {
                    switch (action)
                    {
                        case FileAction.Recycle: Recycle(path); break;
                        case FileAction.Move: MoveInto(path, target!); break;
                        default: File.Delete(path); break;
                    }
                    done.Add(path);
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
            return (done, errors);
        });

    public static (InfoBarSeverity Severity, string Message) Describe(FileAction action, int done, IReadOnlyList<string> errors)
    {
        var verb = action switch { FileAction.Recycle => "Recycled", FileAction.Move => "Moved", _ => "Deleted" };
        if (errors.Count == 0) return (InfoBarSeverity.Success, $"{verb} {done} file{(done == 1 ? "" : "s")}.");

        var nl = Environment.NewLine;
        var shown = string.Join(nl, errors.Take(5)) + (errors.Count > 5 ? $"{nl}(+{errors.Count - 5} more)" : "");
        return (InfoBarSeverity.Error, $"{verb} {done}, failed {errors.Count}:{nl}{shown}");
    }

    private static void Recycle(string path)
    {
        // Network and most removable drives have no Recycle Bin; the shell would delete permanently instead.
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new NotSupportedException("This drive has no Recycle Bin. Use Move to folder or Delete permanently instead.");
        if (!File.Exists(path)) throw new FileNotFoundException("File not found.", path);
        // The shell silently does nothing for paths .NET tolerates, such as doubled separators, so
        // normalize first and confirm the file is really gone rather than trusting the call.
        path = Path.GetFullPath(path);
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        if (File.Exists(path)) throw new IOException("Windows did not move the file to the Recycle Bin.");
    }

    // Never overwrites: a name clash in the target gets a " (2)", " (3)"... suffix.
    private static void MoveInto(string path, string folder)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var dest = Path.Combine(folder, name + ext);
        for (var n = 2; File.Exists(dest) || Directory.Exists(dest); n++)
            dest = Path.Combine(folder, $"{name} ({n}){ext}");
        File.Move(path, dest, overwrite: false);
    }
}
