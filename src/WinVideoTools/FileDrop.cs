using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace WinVideoTools;

/// <summary>Accepts files and folders dragged from Explorer onto a tool page.</summary>
public static class FileDrop
{
    /// <summary>
    /// Makes the page's root panel a drop target. <paramref name="canDrop"/> is checked while dragging,
    /// so a busy tool shows the drop as refused instead of taking it.
    /// </summary>
    public static void Attach(Page page, string caption, Func<bool> canDrop,
        Func<IReadOnlyList<string>, IReadOnlyList<string>, Task> onDrop)
    {
        var root = (Panel)page.Content;
        // A panel without a background is not hit-testable in its empty areas, so drops there would be missed.
        root.Background ??= new SolidColorBrush(Colors.Transparent);
        root.AllowDrop = true;

        root.DragOver += (_, e) =>
        {
            if (!canDrop() || !e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = caption;
        };

        root.Drop += async (_, e) =>
        {
            if (!canDrop() || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                // Items without a file-system path (such as some virtual shell items) cannot be processed.
                var folders = items.Where(i => i.IsOfType(StorageItemTypes.Folder) && !string.IsNullOrEmpty(i.Path)).Select(i => i.Path).ToList();
                var files = items.Where(i => i.IsOfType(StorageItemTypes.File) && !string.IsNullOrEmpty(i.Path)).Select(i => i.Path).ToList();
                deferral.Complete();
                deferral = null;
                await onDrop(folders, files);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
            {
                // The drag source went away or the items could not be read; a failed drop just does nothing.
            }
            finally
            {
                deferral?.Complete();
            }
        };
    }
}
