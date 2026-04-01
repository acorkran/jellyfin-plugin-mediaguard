using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaGuard.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaGuard.Tasks;

/// <summary>
/// Scheduled task that proactively scans media files for corruption using ffprobe.
/// </summary>
public class MediaIntegrityScanTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MediaIntegrityScanTask> _logger;
    private readonly ArrClient _arrClient;
    private readonly CooldownTracker _cooldownTracker;
    private readonly MediaProber _mediaProber;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaIntegrityScanTask"/> class.
    /// </summary>
    public MediaIntegrityScanTask(
        ILibraryManager libraryManager,
        ILogger<MediaIntegrityScanTask> logger,
        ArrClient arrClient,
        CooldownTracker cooldownTracker,
        MediaProber mediaProber)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _arrClient = arrClient;
        _cooldownTracker = cooldownTracker;
        _mediaProber = mediaProber;
    }

    /// <inheritdoc />
    public string Name => "MediarrGuard Integrity Scan";

    /// <inheritdoc />
    public string Key => "MediarrGuardIntegrityScan";

    /// <inheritdoc />
    public string Description => "Scans all media files with ffprobe to detect corruption and triggers re-downloads for corrupt files.";

    /// <inheritdoc />
    public string Category => "MediarrGuard";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks // 3 AM Sunday
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableProactiveScan)
        {
            _logger.LogInformation("MediarrGuard: Proactive scan is disabled, skipping");
            return;
        }

        _logger.LogInformation("MediarrGuard: Starting media integrity scan...");

        var query = new InternalItemsQuery
        {
            MediaTypes = new[] { Jellyfin.Data.Enums.MediaType.Video },
            IsVirtualItem = false,
            Recursive = true
        };

        var items = _libraryManager.GetItemList(query)
            .Where(i => i is Episode or Movie)
            .Where(i => !string.IsNullOrEmpty(i.Path))
            .ToList();

        _logger.LogInformation("MediarrGuard: Found {Count} media files to scan", items.Count);

        var corruptCount = 0;

        for (int i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            progress.Report((double)i / items.Count * 100);

            var isCorrupt = await _mediaProber.IsFileCorruptAsync(item.Path, cancellationToken).ConfigureAwait(false);

            if (isCorrupt)
            {
                corruptCount++;
                _logger.LogWarning("MediarrGuard: CORRUPT file detected: {Path}", item.Path);

                if (_cooldownTracker.TryFlag(item.Id, config.CooldownHours))
                {
                    await _arrClient.RequestRedownloadAsync(item, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        progress.Report(100);
        _logger.LogInformation(
            "MediarrGuard: Integrity scan complete. Scanned {Total} files, found {Corrupt} corrupt.",
            items.Count, corruptCount);
    }

}
