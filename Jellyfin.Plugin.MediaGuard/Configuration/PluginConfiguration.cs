using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaGuard.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the Sonarr base URL.
    /// </summary>
    public string SonarrUrl { get; set; } = "http://localhost:8989";

    /// <summary>
    /// Gets or sets the Sonarr API key.
    /// </summary>
    public string SonarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Radarr base URL.
    /// </summary>
    public string RadarrUrl { get; set; } = "http://localhost:7878";

    /// <summary>
    /// Gets or sets the Radarr API key.
    /// </summary>
    public string RadarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether reactive monitoring is enabled.
    /// When enabled, playback failures trigger automatic re-download requests.
    /// </summary>
    public bool EnableReactiveMonitoring { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether proactive scanning is enabled.
    /// When enabled, a scheduled task periodically validates media file integrity.
    /// </summary>
    public bool EnableProactiveScan { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum playback percentage to consider a failure.
    /// If playback stops before this percentage, it may indicate corruption.
    /// </summary>
    public double FailureThresholdPercent { get; set; } = 3.0;

    /// <summary>
    /// Gets or sets the number of hours to suppress duplicate notifications for the same item.
    /// </summary>
    public int CooldownHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets the minimum number of seconds a playback session must last
    /// before a low-progress stop is considered a potential corruption.
    /// Stops shorter than this are treated as user skips and ignored.
    /// </summary>
    public int MinPlaybackDurationSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets the number of consecutive low-progress stops required
    /// before flagging an item as corrupt. A single skip won't trigger action;
    /// the item must fail repeatedly to be considered genuinely corrupt.
    /// </summary>
    public int ConsecutiveFailuresRequired { get; set; } = 3;

    /// <summary>
    /// Gets or sets the number of hours within which consecutive failures must
    /// occur to count toward the threshold. Failures older than this window are
    /// discarded, preventing occasional skips from accumulating over days/weeks
    /// into false corruption flags.
    /// </summary>
    public int FailureWindowHours { get; set; } = 4;
}
