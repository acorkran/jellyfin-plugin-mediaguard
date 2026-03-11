using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaGuard.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaGuard;

/// <summary>
/// MediarrGuard plugin - detects corrupt media and triggers re-downloads via Sonarr/Radarr.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "MediarrGuard";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a5d1e3b2-4f6c-8a9d-0e1f-2b3c4d5e6f7a");

    /// <inheritdoc />
    public override string Description => "Detects corrupt or unplayable media files and automatically requests replacements from Sonarr and Radarr.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
