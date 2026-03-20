using Jellyfin.Plugin.MediaGuard.Notifiers;
using Jellyfin.Plugin.MediaGuard.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaGuard;

/// <summary>
/// Registers plugin services into the DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<CooldownTracker>();
        serviceCollection.AddSingleton<FailureCounter>();
        serviceCollection.AddSingleton<ArrClient>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackStopEventArgs>, PlaybackFailureNotifier>();
    }
}
