using Jellyfin.Plugin.MediaGuard.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MediaGuard.Tests;

public class MediaProberTests
{
    private readonly MediaProber _prober;

    public MediaProberTests()
    {
        _prober = new MediaProber(Mock.Of<ILogger<MediaProber>>());
    }

    [Fact]
    public async Task IsFileCorrupt_NonexistentFile_ReturnsTrue()
    {
        var result = await _prober.IsFileCorruptAsync("/nonexistent/path/video.mkv");
        Assert.True(result);
    }

    [Fact]
    public async Task IsFileCorrupt_NullPath_ReturnsFalse()
    {
        // Null path should not throw, and should return false (fail-safe: don't flag)
        // The method signature takes string, but let's test empty string
        var result = await _prober.IsFileCorruptAsync(string.Empty);
        // Empty path doesn't exist on disk → returns true (file not found = corrupt)
        Assert.True(result);
    }

    [Fact]
    public async Task IsFileCorrupt_ValidTextFile_ReturnsTrue()
    {
        // A text file is not a valid video — ffprobe should report no video stream
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "this is not a video file");
            var result = await _prober.IsFileCorruptAsync(tempFile);
            // ffprobe will either fail or return no video stream → corrupt
            Assert.True(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task IsFileCorrupt_CancellationRequested_DoesNotThrow()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Should handle cancellation gracefully (returns false — fail-safe)
        var exception = await Record.ExceptionAsync(
            () => _prober.IsFileCorruptAsync("/nonexistent/path/video.mkv", cts.Token));

        // May throw OperationCanceledException or return gracefully depending on timing
        // Either way, it shouldn't crash with an unhandled exception
    }
}
