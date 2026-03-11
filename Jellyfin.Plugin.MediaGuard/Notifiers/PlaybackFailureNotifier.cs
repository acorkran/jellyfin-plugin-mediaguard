using System.Threading.Tasks;
using Jellyfin.Plugin.MediaGuard.Services;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaGuard.Notifiers;

/// <summary>
/// Listens for playback stop events and flags items that failed to play (likely corrupt).
/// </summary>
public class PlaybackFailureNotifier : IEventConsumer<PlaybackStopEventArgs>
{
    private readonly ILogger<PlaybackFailureNotifier> _logger;
    private readonly ArrClient _arrClient;
    private readonly CooldownTracker _cooldownTracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackFailureNotifier"/> class.
    /// </summary>
    public PlaybackFailureNotifier(
        ILogger<PlaybackFailureNotifier> logger,
        ArrClient arrClient,
        CooldownTracker cooldownTracker)
    {
        _logger = logger;
        _arrClient = arrClient;
        _cooldownTracker = cooldownTracker;
    }

    /// <inheritdoc />
    public async Task OnEvent(PlaybackStopEventArgs eventArgs)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableReactiveMonitoring)
        {
            return;
        }

        var item = eventArgs.Item;
        if (item is null || item.IsThemeMedia)
        {
            return;
        }

        // Calculate how far into the file playback got
        var positionTicks = eventArgs.PlaybackPositionTicks;
        var runtimeTicks = item.RunTimeTicks ?? 0;

        if (runtimeTicks <= 0)
        {
            // No runtime info means the file couldn't even be probed - definitely corrupt
            _logger.LogWarning("MediaGuard: {Name} has no runtime info (probe failed), flagging as corrupt", item.Name);
        }
        else
        {
            var percentPlayed = (double)(positionTicks ?? 0) / runtimeTicks * 100.0;

            if (percentPlayed > config.FailureThresholdPercent)
            {
                // Normal stop, user just stopped watching
                return;
            }

            _logger.LogWarning(
                "MediaGuard: {Name} playback stopped at {Percent:F1}% (below {Threshold}% threshold), flagging as potentially corrupt",
                item.Name, percentPlayed, config.FailureThresholdPercent);
        }

        // Check cooldown
        if (!_cooldownTracker.TryFlag(item.Id, config.CooldownHours))
        {
            _logger.LogDebug("MediaGuard: {Name} is on cooldown, skipping", item.Name);
            return;
        }

        await _arrClient.RequestRedownloadAsync(item).ConfigureAwait(false);
    }
}
