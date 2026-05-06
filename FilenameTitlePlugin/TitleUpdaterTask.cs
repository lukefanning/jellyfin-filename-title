using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FilenameTitlePlugin;

public class TitleUpdaterTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly FilenameCleanerService _cleaner;
    private readonly ILogger<TitleUpdaterTask> _logger;

    public TitleUpdaterTask(
        ILibraryManager libraryManager,
        FilenameCleanerService cleaner,
        ILogger<TitleUpdaterTask> logger)
    {
        _libraryManager = libraryManager;
        _cleaner = cleaner;
        _logger = logger;
    }

    public string Name => "Update Titles from Filenames";
    public string Key => "FilenameTitleUpdater";
    public string Description => "Updates media item titles to cleaned versions of their source filenames.";
    public string Category => "Library";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <summary>
    /// Compatibility helper method to handle API changes between Jellyfin versions.
    /// In Jellyfin 10.10+, GetItemList was replaced with GetItemsResult.
    /// </summary>
    /// <param name="query">The query to execute.</param>
    /// <returns>List of items matching the query.</returns>
    private IReadOnlyList<BaseItem> GetItems(InternalItemsQuery query)
    {
        // Use reflection to check which method is available
        var libraryManagerType = _libraryManager.GetType();

        // First try GetItemsResult (Jellyfin 10.10+)
        var getItemsResultMethod = libraryManagerType.GetMethod("GetItemsResult");
        if (getItemsResultMethod != null)
        {
            _logger.LogInformation("[FilenameTitlePlugin] Using compatibility method: GetItemsResult (Jellyfin 10.10+)");
            var result = getItemsResultMethod.Invoke(_libraryManager, new object[] { query });
            if (result != null)
            {
                var itemsProperty = result.GetType().GetProperty("Items");
                if (itemsProperty != null)
                {
                    var items = itemsProperty.GetValue(result) as IReadOnlyList<BaseItem>;
                    if (items != null)
                    {
                        return items;
                    }
                }
            }
        }

        // Fall back to GetItemList (Jellyfin 10.9.x and earlier)
        var getItemListMethod = libraryManagerType.GetMethod("GetItemList");
        if (getItemListMethod != null)
        {
            _logger.LogInformation("[FilenameTitlePlugin] Using compatibility method: GetItemList (Jellyfin 10.9.x and earlier)");
            var items = getItemListMethod.Invoke(_libraryManager, new object[] { query }) as IReadOnlyList<BaseItem>;
            if (items != null)
            {
                return items;
            }
        }

        // If neither method is found, return empty list
        _logger.LogWarning("[FilenameTitlePlugin] Neither GetItemsResult nor GetItemList methods found on ILibraryManager - plugin may not work correctly with this Jellyfin version");
        return new List<BaseItem>();
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var items = GetItems(new InternalItemsQuery
        {
            IsFolder = false,
            Recursive = true
        });

        var total = items.Count;
        if (total == 0)
        {
            progress.Report(100);
            return;
        }

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];

            try
            {
                if (!string.IsNullOrEmpty(item.Path))
                {
                    var rawName = Path.GetFileNameWithoutExtension(item.Path);

                    // Safety rule: only update items whose title is still the raw filename
                    if (string.Equals(item.Name, rawName, StringComparison.OrdinalIgnoreCase))
                    {
                        var cleanTitle = _cleaner.Clean(item.Path);

                        if (!string.IsNullOrEmpty(cleanTitle))
                        {
                            _logger.LogInformation(
                                "[FilenameTitlePlugin] \"{OldTitle}\" → \"{NewTitle}\" ({File})",
                                item.Name,
                                cleanTitle,
                                Path.GetFileName(item.Path));

                            item.Name = cleanTitle;
                            await _libraryManager
                                .UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FilenameTitlePlugin] Failed to update title for item {ItemId}", item.Id);
            }

            progress.Report((double)(i + 1) / total * 100);
        }
    }
}
