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
    public double FailureThresholdPercent { get; set; } = 2.0;

    /// <summary>
    /// Gets or sets the number of hours to suppress duplicate notifications for the same item.
    /// </summary>
    public int CooldownHours { get; set; } = 24;
}
