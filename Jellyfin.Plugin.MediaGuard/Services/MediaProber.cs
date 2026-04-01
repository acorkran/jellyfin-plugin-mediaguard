using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaGuard.Services;

/// <summary>
/// Probes media files with ffprobe to determine whether they are genuinely corrupt.
/// Used as a verification step before taking destructive action (deleting + re-downloading).
/// </summary>
public class MediaProber
{
    private readonly ILogger<MediaProber> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaProber"/> class.
    /// </summary>
    public MediaProber(ILogger<MediaProber> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Probes a media file with ffprobe and returns true if the file appears corrupt.
    /// Returns false if the file is healthy or if ffprobe can't be run (fail-safe —
    /// we never flag a file as corrupt if we can't verify).
    /// </summary>
    public async Task<bool> IsFileCorruptAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("MediarrGuard: File not found at {Path}", filePath);
                return true;
            }

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

            // Read both streams concurrently before WaitForExit to avoid deadlocks
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var output = outputTask.Result;
            var errorOutput = errorTask.Result;

            // ffprobe returns non-zero for corrupt files
            if (process.ExitCode != 0)
            {
                _logger.LogDebug("MediarrGuard: ffprobe failed for {Path}: {Error}", filePath, errorOutput.Trim());
                return true;
            }

            // If ffprobe returns nothing for a video file, it's corrupt
            if (string.IsNullOrWhiteSpace(output))
            {
                _logger.LogDebug("MediarrGuard: ffprobe returned no video stream info for {Path}", filePath);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediarrGuard: Error probing file {Path}", filePath);
            return false; // Don't flag as corrupt if we can't even run ffprobe
        }
    }
}
