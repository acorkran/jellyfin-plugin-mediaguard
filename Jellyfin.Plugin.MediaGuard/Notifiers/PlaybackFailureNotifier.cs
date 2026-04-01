using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaGuard.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
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
    private readonly FailureCounter _failureCounter;
    private readonly ISessionManager _sessionManager;
    private readonly MediaProber _mediaProber;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackFailureNotifier"/> class.
    /// </summary>
    public PlaybackFailureNotifier(
        ILogger<PlaybackFailureNotifier> logger,
        ArrClient arrClient,
        CooldownTracker cooldownTracker,
        FailureCounter failureCounter,
        ISessionManager sessionManager,
        MediaProber mediaProber)
    {
        _logger = logger;
        _arrClient = arrClient;
        _cooldownTracker = cooldownTracker;
        _failureCounter = failureCounter;
        _sessionManager = sessionManager;
        _mediaProber = mediaProber;
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

        // Only handle Episodes and Movies
        if (item is not Episode and not Movie)
        {
            return;
        }

        // Calculate how far into the file playback got
        var positionTicks = eventArgs.PlaybackPositionTicks;
        var runtimeTicks = item.RunTimeTicks ?? 0;

        if (runtimeTicks <= 0)
        {
            // No runtime info means the file couldn't even be probed - definitely corrupt
            _logger.LogWarning("MediarrGuard: {Name} has no runtime info (probe failed), flagging as corrupt", item.Name);
        }
        else
        {
            var percentPlayed = (double)(positionTicks ?? 0) / runtimeTicks * 100.0;

            if (percentPlayed > config.FailureThresholdPercent)
            {
                // Normal stop — clear any accumulated failure count for this item,
                // since a successful playback proves the file isn't corrupt.
                _failureCounter.Reset(item.Id);
                return;
            }

            // Check minimum playback duration to filter out quick skips.
            // If the user played for less than the configured minimum, this is
            // almost certainly a user-initiated skip, not a corruption failure.
            var playbackDurationSeconds = (double)(positionTicks ?? 0) / TimeSpan.TicksPerSecond;
            if (playbackDurationSeconds < config.MinPlaybackDurationSeconds)
            {
                _logger.LogDebug(
                    "MediarrGuard: {Name} playback lasted {Duration:F1}s (below {Min}s minimum), treating as user skip — ignoring",
                    item.Name, playbackDurationSeconds, config.MinPlaybackDurationSeconds);
                return;
            }

            // Check if this is an episode transition — if the user's session is now
            // playing a different item, this stop was just a normal transition
            // (auto-play next episode, manual skip to next, etc.), not a failure.
            var activeSession = _sessionManager.Sessions
                .FirstOrDefault(s =>
                    s.UserId != Guid.Empty
                    && s.NowPlayingItem != null
                    && s.NowPlayingItem.Id != item.Id
                    && s.LastPlaybackCheckIn >= DateTime.UtcNow.AddSeconds(-30));

            if (activeSession is not null)
            {
                _logger.LogDebug(
                    "MediarrGuard: {Name} stop is an episode transition (now playing {NewItem}), ignoring",
                    item.Name, activeSession.NowPlayingItem.Name);
                return;
            }

            _logger.LogWarning(
                "MediarrGuard: {Name} playback stopped at {Percent:F1}% (below {Threshold}% threshold), recording failure",
                item.Name, percentPlayed, config.FailureThresholdPercent);
        }

        // Track consecutive failures — only act once the threshold is reached.
        // Failures older than the configured window are discarded so that
        // occasional skips spread across days/weeks don't accumulate.
        var failureCount = _failureCounter.RecordFailure(item.Id, config.FailureWindowHours);
        if (failureCount < config.ConsecutiveFailuresRequired)
        {
            _logger.LogInformation(
                "MediarrGuard: {Name} failure {Count}/{Required} — not flagging yet",
                item.Name, failureCount, config.ConsecutiveFailuresRequired);
            return;
        }

        // Check cooldown (only reached after consecutive failure threshold is met)
        if (!_cooldownTracker.TryFlag(item.Id, config.CooldownHours))
        {
            _logger.LogDebug("MediarrGuard: {Name} is on cooldown, skipping", item.Name);
            return;
        }

        // Verify the file is actually corrupt with ffprobe before taking destructive action.
        // Playback heuristics can produce false positives (intro skips, transitions, etc.)
        // but ffprobe gives a definitive answer.
        if (!string.IsNullOrEmpty(item.Path))
        {
            var isActuallyCorrupt = await _mediaProber.IsFileCorruptAsync(item.Path).ConfigureAwait(false);
            if (!isActuallyCorrupt)
            {
                _logger.LogInformation(
                    "MediarrGuard: {Name} reached failure threshold but ffprobe says file is healthy — false positive, resetting",
                    item.Name);
                _failureCounter.Reset(item.Id);
                return;
            }
        }

        _logger.LogWarning(
            "MediarrGuard: {Name} has failed {Count} consecutive times and ffprobe confirms corruption — flagging",
            item.Name, failureCount);

        // Reset counter now that we're acting on it
        _failureCounter.Reset(item.Id);

        // Build a friendly display name
        var displayName = item is Episode ep
            ? $"{ep.SeriesName} S{ep.ParentIndexNumber:D2}E{ep.IndexNumber:D2} - {ep.Name}"
            : item.Name;

        // Notify the user that the file is corrupt and a replacement is being sourced
        await NotifyUserAsync(eventArgs, displayName).ConfigureAwait(false);

        // Request re-download from Sonarr/Radarr
        var success = await _arrClient.RequestRedownloadAsync(item).ConfigureAwait(false);

        if (success)
        {
            // Send follow-up confirmation
            await NotifyUserAsync(
                eventArgs,
                displayName,
                "A replacement has been found and is downloading. This item will be available again shortly.").ConfigureAwait(false);
        }
    }

    private async Task NotifyUserAsync(PlaybackStopEventArgs eventArgs, string displayName, string? followUpMessage = null)
    {
        try
        {
            // Find the session that was playing this item
            var sessions = _sessionManager.Sessions
                .Where(s => s.UserId != Guid.Empty)
                .ToList();

            // Try to find the specific session by matching the user/device
            var targetSession = sessions.FirstOrDefault(s =>
                s.LastPlaybackCheckIn >= DateTime.UtcNow.AddMinutes(-2));

            if (targetSession is null && sessions.Count > 0)
            {
                targetSession = sessions.First();
            }

            if (targetSession is null)
            {
                _logger.LogDebug("MediarrGuard: No active session found to send notification");
                return;
            }

            var header = followUpMessage is null
                ? "Corrupt File Detected"
                : "MediarrGuard Update";

            var text = followUpMessage
                ?? $"\"{displayName}\" is corrupt and cannot be played. MediarrGuard is automatically sourcing a replacement — check back shortly.";

            var messageCommand = new MessageCommand
            {
                Header = header,
                Text = text,
                TimeoutMs = followUpMessage is null ? 15000L : 10000L
            };

            await _sessionManager.SendMessageCommand(
                targetSession.Id,
                targetSession.Id,
                messageCommand,
                default).ConfigureAwait(false);

            _logger.LogInformation("MediarrGuard: Sent notification to session {Session}: {Message}",
                targetSession.DeviceName, text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MediarrGuard: Failed to send user notification (non-critical)");
        }
    }
}
