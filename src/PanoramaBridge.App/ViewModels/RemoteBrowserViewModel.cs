using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.App.ViewModels;

/// <summary>
/// A folder in the remote browser tree, loaded on demand.
/// </summary>
/// <remarks>
/// Children are fetched when the node is first expanded rather than up front. The MacCoss
/// project alone has around sixty sub-containers, each with its own tree, so eager loading would
/// mean thousands of requests to show one level.
/// </remarks>
public sealed partial class RemoteFolderViewModel : ObservableObject
{
    private readonly IWebDavClient _client;
    private bool _loaded;

    public RemoteFolderViewModel(IWebDavClient client, RemotePath path, string name, bool canUpload)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));

        Path = path.AsCollection();
        Name = name;
        CanUpload = canUpload;

        // A placeholder child makes the node expandable before its contents are known. WPF
        // only draws an expander for a node that already has at least one child.
        Children.Add(new RemoteFolderViewModel());
    }

    /// <summary>Placeholder shown until a folder's real contents arrive.</summary>
    private RemoteFolderViewModel()
    {
        _client = null!;
        Path = RemotePath.Root;
        Name = "Loading...";
        CanUpload = false;
        IsPlaceholder = true;
    }

    /// <summary>True for the stand-in child, which is never selectable.</summary>
    public bool IsPlaceholder { get; }

    /// <summary>Full remote path of this folder.</summary>
    public RemotePath Path { get; }

    /// <summary>Folder name shown in the tree.</summary>
    public string Name { get; }

    /// <summary>
    /// Whether this account may upload here.
    /// </summary>
    /// <remarks>
    /// Shown in the tree and used to disable selection, so a read-only destination is refused
    /// when it is chosen rather than surfacing as a 403 hours into a transfer.
    /// </remarks>
    public bool CanUpload { get; }

    /// <summary>Tooltip explaining why a folder cannot be chosen.</summary>
    public string PermissionHint => CanUpload
        ? "You can upload to this folder."
        : "Read-only. A Panorama administrator would need to grant write access.";

    public ObservableCollection<RemoteFolderViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoadingChildren;

    [ObservableProperty]
    private string? _error;

    async partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
        {
            await LoadChildrenAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Fetches this folder's subfolders.</summary>
    public async Task LoadChildrenAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        IsLoadingChildren = true;
        Error = null;

        try
        {
            var entries = await _client.ListAsync(Path, cancellationToken).ConfigureAwait(true);

            Children.Clear();

            foreach (var entry in entries
                .Where(e => e.IsCollection)
                .Where(e => !e.Name.StartsWith('.'))
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Children.Add(new RemoteFolderViewModel(
                    _client, entry.Path, entry.Name, entry.Permissions.CanUpload));
            }
        }
        catch (WebDavException ex)
        {
            Children.Clear();
            Error = ex.ToUserMessage();

            // Allow another attempt: a folder that failed once because of a transient problem
            // should not stay permanently unopenable.
            _loaded = false;
        }
        finally
        {
            IsLoadingChildren = false;
        }
    }

}

/// <summary>
/// The remote folder picker.
/// </summary>
/// <remarks>
/// Shows each folder's write permission, taken from the same listing that populates the tree.
/// The Python version had no way to know: its folder dialog discovered a permissions problem by
/// reading the application's own log file back off disk and string-matching for
/// "Permission denied".
/// </remarks>
public sealed partial class RemoteBrowserViewModel : ObservableObject
{
    private readonly IWebDavClient _client;

    public RemoteBrowserViewModel(IWebDavClient client, string startingPath)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));

        SelectedPath = startingPath;
    }

    /// <summary>Top-level folders.</summary>
    public ObservableCollection<RemoteFolderViewModel> Roots { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    private RemoteFolderViewModel? _selected;

    [ObservableProperty]
    private string _selectedPath = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    /// <summary>True when the highlighted folder can actually be uploaded to.</summary>
    public bool CanAccept => Selected is { IsPlaceholder: false, CanUpload: true };

    /// <summary>Explains why the highlighted folder cannot be chosen.</summary>
    public string AcceptHint => Selected is null
        ? "Choose a folder."
        : Selected.CanUpload
            ? Selected.Path.ToEncodedString()
            : $"{Selected.Path} is read-only for this account.";

    partial void OnSelectedChanged(RemoteFolderViewModel? value)
    {
        if (value is not null)
        {
            SelectedPath = value.Path.ToEncodedString();
        }

        OnPropertyChanged(nameof(AcceptHint));
    }

    /// <summary>Loads the WebDAV root.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        Error = null;

        try
        {
            // The WebDAV root lists the projects this account can see.
            var root = RemotePath.Parse("/_webdav/");
            var entries = await _client.ListAsync(root, cancellationToken).ConfigureAwait(true);

            Roots.Clear();

            foreach (var entry in entries
                .Where(e => e.IsCollection)
                .Where(e => !e.Name.StartsWith('.'))
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Roots.Add(new RemoteFolderViewModel(
                    _client, entry.Path, entry.Name, entry.Permissions.CanUpload));
            }
        }
        catch (WebDavException ex)
        {
            Error = ex.ToUserMessage();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Creates a subfolder under the highlighted one.</summary>
    [RelayCommand(CanExecute = nameof(CanAccept))]
    private async Task CreateFolderAsync(string? name)
    {
        if (Selected is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var validation = PathSafety.ValidateSegment(name.Trim());
        if (!validation.IsValid)
        {
            Error = validation.Message;
            return;
        }

        try
        {
            var created = Selected.Path.Append(name.Trim(), isCollection: true);

            // Recursive by design, so creating a nested path in one step just works. The
            // server's own MKCOL is single-level and answers 409 for a missing parent.
            await _client.EnsureCollectionAsync(created).ConfigureAwait(true);

            Selected.Children.Add(new RemoteFolderViewModel(
                _client, created, name.Trim(), canUpload: true));

            Selected.IsExpanded = true;
            Error = null;
        }
        catch (WebDavException ex)
        {
            Error = ex.ToUserMessage();
        }
    }
}
