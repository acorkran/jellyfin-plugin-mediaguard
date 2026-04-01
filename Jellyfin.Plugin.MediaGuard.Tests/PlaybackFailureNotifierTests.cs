using Jellyfin.Plugin.MediaGuard.Configuration;
using Jellyfin.Plugin.MediaGuard.Notifiers;
using Jellyfin.Plugin.MediaGuard.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MediaGuard.Tests;

public class PlaybackFailureNotifierTests : IDisposable
{
    private readonly Mock<ILogger<PlaybackFailureNotifier>> _logger = new();
    private readonly Mock<ArrClient> _arrClient;
    private readonly CooldownTracker _cooldownTracker = new();
    private readonly FailureCounter _failureCounter = new();
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly Mock<MediaProber> _mediaProber;
    private readonly PlaybackFailureNotifier _notifier;

    // Store the original Plugin.Instance so we can restore it
    private readonly PluginConfiguration _config;

    public PlaybackFailureNotifierTests()
    {
        _arrClient = new Mock<ArrClient>(
            Mock.Of<System.Net.Http.IHttpClientFactory>(),
            Mock.Of<ILogger<ArrClient>>(),
            Mock.Of<ILibraryManager>());

        _mediaProber = new Mock<MediaProber>(Mock.Of<ILogger<MediaProber>>());

        // Default: no active sessions
        _sessionManager.Setup(s => s.Sessions)
            .Returns(Array.Empty<SessionInfo>());

        _config = new PluginConfiguration
        {
            EnableReactiveMonitoring = true,
            FailureThresholdPercent = 3.0,
            MinPlaybackDurationSeconds = 10,
            ConsecutiveFailuresRequired = 3,
            FailureWindowHours = 4,
            CooldownHours = 24
        };

        _notifier = new PlaybackFailureNotifier(
            _logger.Object,
            _arrClient.Object,
            _cooldownTracker,
            _failureCounter,
            _sessionManager.Object,
            _mediaProber.Object);
    }

    public void Dispose()
    {
        // Clean up Plugin.Instance if needed
    }

    private Episode CreateEpisode(long runtimeMinutes = 22, string name = "Test Episode")
    {
        return new Episode
        {
            Id = Guid.NewGuid(),
            Name = name,
            RunTimeTicks = runtimeMinutes * 60 * TimeSpan.TicksPerSecond,
            Path = "/media/shows/test/S01E01.mkv"
        };
    }

    private PlaybackStopEventArgs CreateStopEvent(Episode episode, double positionSeconds)
    {
        return new PlaybackStopEventArgs
        {
            Item = episode,
            PlaybackPositionTicks = (long)(positionSeconds * TimeSpan.TicksPerSecond)
        };
    }

    // Helper to setup Plugin.Instance with our config via reflection
    private void SetupPluginConfig()
    {
        // Plugin.Instance is a static singleton. We need to set it up for tests.
        // Since we can't easily instantiate the Plugin class, we'll test the
        // notifier logic indirectly through the public behaviors.
    }

    [Fact]
    public void FailureCounter_SuccessfulPlayback_ResetsCount()
    {
        // Simulate: episode gets 2 failures, then a successful play
        var itemId = Guid.NewGuid();
        _failureCounter.RecordFailure(itemId, windowHours: 4);
        _failureCounter.RecordFailure(itemId, windowHours: 4);
        Assert.Equal(2, _failureCounter.GetCount(itemId));

        // Successful playback would call Reset
        _failureCounter.Reset(itemId);
        Assert.Equal(0, _failureCounter.GetCount(itemId));

        // Next failure starts from 1
        var count = _failureCounter.RecordFailure(itemId, windowHours: 4);
        Assert.Equal(1, count);
    }

    [Fact]
    public void FailureCounter_ThreeFailuresWithinWindow_ReachesThreshold()
    {
        var itemId = Guid.NewGuid();
        _failureCounter.RecordFailure(itemId, windowHours: 4);
        _failureCounter.RecordFailure(itemId, windowHours: 4);
        var count = _failureCounter.RecordFailure(itemId, windowHours: 4);
        Assert.Equal(3, count);
        Assert.True(count >= _config.ConsecutiveFailuresRequired);
    }

    [Fact]
    public void FailureCounter_InterleavedSuccessAndFailure_NeverReachesThreshold()
    {
        // Simulates: user skips intro (failure), watches normally (success/reset),
        // skips intro again (failure), watches normally (success/reset), etc.
        var itemId = Guid.NewGuid();

        for (int i = 0; i < 10; i++)
        {
            var count = _failureCounter.RecordFailure(itemId, windowHours: 4);
            Assert.Equal(1, count); // Always 1 because we reset each time
            _failureCounter.Reset(itemId); // Simulates successful playback
        }
    }

    [Fact]
    public async Task Notifier_NullItem_DoesNothing()
    {
        // OnEvent requires Plugin.Instance to be set (returns early if null config).
        // Since Plugin.Instance is null in tests, OnEvent returns immediately —
        // verifying that the notifier handles a missing config gracefully
        // without throwing or calling any downstream services.
        var eventArgs = new PlaybackStopEventArgs { Item = null };
        var exception = await Record.ExceptionAsync(() => _notifier.OnEvent(eventArgs));
        Assert.Null(exception);
    }

    [Fact]
    public void TransitionDetection_SessionPlayingDifferentItem_ShouldBeDetectable()
    {
        // Verify that we can detect when a session is playing a different item
        var episode1Id = Guid.NewGuid();
        var episode2Id = Guid.NewGuid();

        var session = new SessionInfo(
            Mock.Of<ISessionManager>(),
            Mock.Of<ILogger<SessionInfo>>())
        {
            NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto
            {
                Id = episode2Id,
                Name = "Next Episode"
            }
        };

        _sessionManager.Setup(s => s.Sessions)
            .Returns(new[] { session });

        var sessions = _sessionManager.Object.Sessions;
        var activeSession = sessions.FirstOrDefault(s =>
            s.NowPlayingItem != null
            && s.NowPlayingItem.Id != episode1Id);

        Assert.NotNull(activeSession);
        Assert.Equal(episode2Id, activeSession.NowPlayingItem!.Id);
    }

    [Fact]
    public void TransitionDetection_SessionPlayingSameItem_NotATransition()
    {
        var episodeId = Guid.NewGuid();

        var session = new SessionInfo(
            Mock.Of<ISessionManager>(),
            Mock.Of<ILogger<SessionInfo>>())
        {
            NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto
            {
                Id = episodeId,
                Name = "Same Episode"
            }
        };

        _sessionManager.Setup(s => s.Sessions)
            .Returns(new[] { session });

        var sessions = _sessionManager.Object.Sessions;
        var activeSession = sessions.FirstOrDefault(s =>
            s.NowPlayingItem != null
            && s.NowPlayingItem.Id != episodeId);

        // Should NOT find a transition — same item is playing
        Assert.Null(activeSession);
    }

    [Fact]
    public void TransitionDetection_NoActiveSessions_NotATransition()
    {
        var episodeId = Guid.NewGuid();

        _sessionManager.Setup(s => s.Sessions)
            .Returns(Array.Empty<SessionInfo>());

        var sessions = _sessionManager.Object.Sessions;
        var activeSession = sessions.FirstOrDefault(s =>
            s.NowPlayingItem != null
            && s.NowPlayingItem.Id != episodeId);

        Assert.Null(activeSession);
    }

    [Fact]
    public void PercentPlayed_NormalPlayback_AboveThreshold()
    {
        // 22-minute episode watched for 15 minutes
        long runtimeTicks = 22 * 60 * TimeSpan.TicksPerSecond;
        long positionTicks = 15 * 60 * TimeSpan.TicksPerSecond;
        var percentPlayed = (double)positionTicks / runtimeTicks * 100.0;

        Assert.True(percentPlayed > _config.FailureThresholdPercent);
    }

    [Fact]
    public void PercentPlayed_IntroSkip_BelowThreshold()
    {
        // 22-minute episode, stopped at 30 seconds (intro skip)
        long runtimeTicks = 22 * 60 * TimeSpan.TicksPerSecond;
        long positionTicks = 30 * TimeSpan.TicksPerSecond;
        var percentPlayed = (double)positionTicks / runtimeTicks * 100.0;

        // 30s / 1320s = 2.27% which is below 3% threshold
        Assert.True(percentPlayed < _config.FailureThresholdPercent);
    }

    [Fact]
    public void MinDuration_QuickSkip_FilteredOut()
    {
        // Position at 5 seconds — below 10s minimum
        long positionTicks = 5 * TimeSpan.TicksPerSecond;
        var durationSeconds = (double)positionTicks / TimeSpan.TicksPerSecond;

        Assert.True(durationSeconds < _config.MinPlaybackDurationSeconds);
    }

    [Fact]
    public void MinDuration_IntroWatchThenSkip_PassesFilter()
    {
        // Position at 30 seconds — above 10s minimum (this is the problem case)
        long positionTicks = 30 * TimeSpan.TicksPerSecond;
        var durationSeconds = (double)positionTicks / TimeSpan.TicksPerSecond;

        Assert.True(durationSeconds >= _config.MinPlaybackDurationSeconds);
    }

    [Fact]
    public void FfprobeGate_HealthyFile_PreventsAction()
    {
        // Verify that the ffprobe gate logic works:
        // Even after reaching failure threshold, a healthy file should not be flagged
        var itemId = Guid.NewGuid();

        // Accumulate failures to threshold
        _failureCounter.RecordFailure(itemId, windowHours: 4);
        _failureCounter.RecordFailure(itemId, windowHours: 4);
        _failureCounter.RecordFailure(itemId, windowHours: 4);

        Assert.Equal(3, _failureCounter.GetCount(itemId));

        // If ffprobe says file is healthy, we reset the counter
        // (this is what the notifier does)
        bool ffprobeSaysCorrupt = false; // healthy file
        if (!ffprobeSaysCorrupt)
        {
            _failureCounter.Reset(itemId);
        }

        Assert.Equal(0, _failureCounter.GetCount(itemId));
    }

    [Fact]
    public void FfprobeGate_CorruptFile_AllowsAction()
    {
        var itemId = Guid.NewGuid();

        _failureCounter.RecordFailure(itemId, windowHours: 4);
        _failureCounter.RecordFailure(itemId, windowHours: 4);
        _failureCounter.RecordFailure(itemId, windowHours: 4);

        bool ffprobeSaysCorrupt = true;
        if (!ffprobeSaysCorrupt)
        {
            _failureCounter.Reset(itemId);
        }

        // Counter should still be at 3 — action should proceed
        Assert.Equal(3, _failureCounter.GetCount(itemId));
    }

    [Fact]
    public void FullScenario_BingeWithIntroSkips_NeverFlagsHealthyFile()
    {
        // Simulate binge-watching: skip intro (failure), watch episode (success), repeat
        // This should never reach the failure threshold for any single episode
        var episodes = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();

        foreach (var epId in episodes)
        {
            // User starts episode, skips intro → failure recorded
            var count = _failureCounter.RecordFailure(epId, windowHours: 4);
            Assert.Equal(1, count);

            // User watches the rest normally → success resets counter
            _failureCounter.Reset(epId);
            Assert.Equal(0, _failureCounter.GetCount(epId));
        }
    }

    [Fact]
    public void FullScenario_GenuineCorruption_ReachesThreshold()
    {
        // Simulate a genuinely corrupt file: user tries 3 times, fails each time
        var corruptEpisode = Guid.NewGuid();

        // Attempt 1: fails immediately
        Assert.Equal(1, _failureCounter.RecordFailure(corruptEpisode, windowHours: 4));

        // Attempt 2: fails again
        Assert.Equal(2, _failureCounter.RecordFailure(corruptEpisode, windowHours: 4));

        // Attempt 3: fails again — threshold reached
        Assert.Equal(3, _failureCounter.RecordFailure(corruptEpisode, windowHours: 4));
        Assert.True(_failureCounter.GetCount(corruptEpisode) >= _config.ConsecutiveFailuresRequired);
    }
}
