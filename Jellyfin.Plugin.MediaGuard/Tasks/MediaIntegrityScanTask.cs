using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaIntegrityScanTask"/> class.
    /// </summary>
    public MediaIntegrityScanTask(
        ILibraryManager libraryManager,
        ILogger<MediaIntegrityScanTask> logger,
        ArrClient arrClient,
        CooldownTracker cooldownTracker)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _arrClient = arrClient;
        _cooldownTracker = cooldownTracker;
    }

    /// <inheritdoc />
    public string Name => "MediaGuard Integrity Scan";

    /// <inheritdoc />
    public string Key => "MediaGuardIntegrityScan";

    /// <inheritdoc />
    public string Description => "Scans all media files with ffprobe to detect corruption and triggers re-downloads for corrupt files.";

    /// <inheritdoc />
    public string Category => "MediaGuard";

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
            _logger.LogInformation("MediaGuard: Proactive scan is disabled, skipping");
            return;
        }

        _logger.LogInformation("MediaGuard: Starting media integrity scan...");

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

        _logger.LogInformation("MediaGuard: Found {Count} media files to scan", items.Count);

        var corruptCount = 0;

        for (int i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            progress.Report((double)i / items.Count * 100);

            if (!System.IO.File.Exists(item.Path))
            {
                _logger.LogWarning("MediaGuard: File missing: {Path}", item.Path);
                continue;
            }

            var isCorrupt = await ProbeFileAsync(item.Path, cancellationToken).ConfigureAwait(false);

            if (isCorrupt)
            {
                corruptCount++;
                _logger.LogWarning("MediaGuard: CORRUPT file detected: {Path}", item.Path);

                if (_cooldownTracker.TryFlag(item.Id, config.CooldownHours))
                {
                    await _arrClient.RequestRedownloadAsync(item, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        progress.Report(100);
        _logger.LogInformation(
            "MediaGuard: Integrity scan complete. Scanned {Total} files, found {Corrupt} corrupt.",
            items.Count, corruptCount);
    }

    private async Task<bool> ProbeFileAsync(string filePath, CancellationToken ct)
    {
        try
        {
            // Try jellyfin-ffmpeg first, fall back to system ffprobe
            var ffprobePath = System.IO.File.Exists("/usr/lib/jellyfin-ffmpeg/ffprobe")
                ? "/usr/lib/jellyfin-ffmpeg/ffprobe"
                : "ffprobe";

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            var errorOutput = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            // ffprobe returns non-zero for corrupt files
            if (process.ExitCode != 0)
            {
                _logger.LogDebug("MediaGuard: ffprobe failed for {Path}: {Error}", filePath, errorOutput.Trim());
                return true;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);

            // If ffprobe returns nothing for a video file, it's corrupt
            if (string.IsNullOrWhiteSpace(output))
            {
                _logger.LogDebug("MediaGuard: ffprobe returned no video stream info for {Path}", filePath);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediaGuard: Error probing file {Path}", filePath);
            return false; // Don't flag as corrupt if we can't even run ffprobe
        }
    }
}
