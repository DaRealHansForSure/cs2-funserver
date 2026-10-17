using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2ServerControl.Services;
using Cs2ServerControl.Shell;

namespace Cs2ServerControl.Maps;

public sealed partial class WorkshopRowViewModel : ObservableObject {
    public WorkshopRowViewModel(WorkshopItem item, WorkshopDownload? download, ImageSource? thumbnail) {
        Item = item;
        Download = download;
        Thumbnail = thumbnail;
    }

    public WorkshopItem Item { get; }
    public WorkshopDownload? Download { get; }
    public ImageSource? Thumbnail { get; }
    public string Title => Item.Title;
    public string Id => Item.Id;

    /// <summary>The name the Server screen lists it under: the map name once downloaded, the title before.</summary>
    public string MapName => Download?.MapNames.FirstOrDefault() ?? Item.Title;

    public string SizeText => (Item.SizeBytes / 1024.0 / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " MB";

    public string StateText {
        get {
            if (Download == null) {
                return "Downloads on its first start";
            }
            if (Download.MapNames.Count == 0) {
                return "Downloaded, but holds no map";
            }
            return "Downloaded: " + string.Join(", ", Download.MapNames);
        }
    }
}

/// <summary>
/// The Workshop tab of the Maps screen: the host's list of Workshop maps.
/// Steam's details come without an API key, a search would need one, so the
/// Workshop opens in the browser and a link goes into the box. The server
/// downloads a map when it starts it (host_workshop_map); players' CS2
/// downloads it from Steam when they join.
/// </summary>
public sealed partial class WorkshopViewModel : ObservableObject {
    private readonly ServerConnection _connection;
    private readonly IDialogService _dialogs;
    private readonly MapThumbnails _thumbnails;
    private readonly Func<string, string, bool> _pickOnServer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _linkText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private bool _isAdding;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isMessageAProblem;

    public WorkshopViewModel(ServerConnection connection, IDialogService dialogs, Func<string, string, bool> pickOnServer) {
        _connection = connection;
        _dialogs = dialogs;
        _pickOnServer = pickOnServer;
        _thumbnails = new MapThumbnails(connection.Paths);
    }

    public ObservableCollection<WorkshopRowViewModel> Rows { get; } = [];

    public void Refresh() {
        PackagePaths paths = _connection.Paths;
        Rows.Clear();
        foreach (WorkshopItem item in WorkshopLibrary.LoadItems(paths)) {
            Rows.Add(new WorkshopRowViewModel(item, WorkshopLibrary.FindDownloadById(paths, item.Id), _thumbnails.Get(item.Id)));
        }
    }

    private bool CanAdd() {
        return !IsAdding && LinkText.Trim().Length > 0;
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task Add() {
        string? id = WorkshopLibrary.ParseId(LinkText);
        if (id == null) {
            ShowMessage("That is no Workshop link. Copy the address of the map's Workshop page, it ends with ?id= and a number.", isProblem: true);
            return;
        }
        List<WorkshopItem> items = [.. WorkshopLibrary.LoadItems(_connection.Paths)];
        if (items.Any(item => item.Id == id)) {
            ShowMessage("This map is already in the list.", isProblem: false);
            return;
        }

        IsAdding = true;
        try {
            WorkshopItem item = await WorkshopClient.GetDetailsAsync(id);
            await DownloadPreviewAsync(item);
            items.Add(item);
            WorkshopLibrary.SaveItems(_connection.Paths, items);
            LinkText = "";
            ShowMessage($"{item.Title} added. The Server screen lists it in every mode.", isProblem: false);
            Refresh();
        } catch (Exception exception) when (exception is WorkshopException or HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException) {
            ShowMessage("Could not add it: " + exception.Message, isProblem: true);
        } finally {
            IsAdding = false;
        }
    }

    /// <summary>The picture is nice to have; without it the map is still added.</summary>
    private async Task DownloadPreviewAsync(WorkshopItem item) {
        if (item.PreviewUrl.Length == 0) {
            return;
        }
        // Steam's picture links carry no extension; WPF reads the image by its content anyway.
        try {
            await WorkshopClient.DownloadPreviewAsync(item, Path.Combine(_connection.Paths.PreviewsDir, item.Id + ".jpg"));
            MapThumbnails.Forget(item.Id);
        } catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException) {
            // The list shows an empty picture frame instead.
        }
    }

    [RelayCommand]
    private async Task Remove(WorkshopRowViewModel? row) {
        if (row == null) {
            return;
        }
        bool isPlaying = row.Download != null && row.Download.MapNames.Contains(_connection.CurrentMap ?? "", StringComparer.OrdinalIgnoreCase);
        if (isPlaying) {
            ShowMessage($"{row.MapName} is running right now. Change the map first.", isProblem: true);
            return;
        }
        string files = row.Download == null ? "" : $" The downloaded files ({row.Download.SizeBytes / 1024 / 1024} MB) are deleted too.";
        if (!await _dialogs.ConfirmAsync("Remove Workshop map", $"Remove {row.Title} from the list?{files}", "Remove")) {
            return;
        }

        List<WorkshopItem> items = [.. WorkshopLibrary.LoadItems(_connection.Paths).Where(item => item.Id != row.Id)];
        WorkshopLibrary.SaveItems(_connection.Paths, items);
        if (row.Download != null) {
            try {
                Directory.Delete(Path.GetDirectoryName(row.Download.PackageFile)!, recursive: true);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                ShowMessage("Removed from the list, but the files could not be deleted: " + exception.Message, isProblem: true);
                Refresh();
                return;
            }
        }
        ShowMessage($"{row.Title} removed.", isProblem: false);
        Refresh();
    }

    /// <summary>To the Server screen's draft, in the mode that runs (or is picked there); Apply starts it.</summary>
    [RelayCommand]
    private void Pick(WorkshopRowViewModel? row) {
        if (row == null) {
            return;
        }
        _pickOnServer(_connection.ActiveModeId ?? GameModes.All[0].Id, row.MapName);
    }

    [RelayCommand]
    private static void OpenPage(WorkshopRowViewModel? row) {
        if (row != null) {
            OpenInBrowser(WorkshopClient.PageUrl(row.Id));
        }
    }

    [RelayCommand]
    private static void Browse() {
        OpenInBrowser(WorkshopClient.BrowseUrl);
    }

    private static void OpenInBrowser(string url) {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private void ShowMessage(string text, bool isProblem) {
        Message = text;
        IsMessageAProblem = isProblem;
    }
}
