using Jellyfin.Plugin.SmartShuffle.Playback;
using Jellyfin.Plugin.SmartShuffle.State;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SmartShuffle;

/// <summary>
/// Registers the plugin's services with Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ShuffleStateStore>();
        serviceCollection.AddSingleton<ShuffleService>();
        serviceCollection.AddSingleton<ReportedQueues>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.Add<PlaybackStartQueueFilter>());
        serviceCollection.AddHostedService<PlaybackListener>();
    }
}
