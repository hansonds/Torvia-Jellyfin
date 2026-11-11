using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using PhoenicxTorBox.Filters;
using PhoenicxTorBox.Library;
using PhoenicxTorBox.Stats;
using PhoenicxTorBox.Tasks;
using PhoenicxTorBox.TorBox;

namespace PhoenicxTorBox;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TorBoxClient>();
        serviceCollection.AddSingleton<LibrarySync>();
        serviceCollection.AddSingleton<DirectPlayFilter>();
        serviceCollection.AddHostedService<SyncScheduleUpdater>();
        // Torvia Search (search any movie or show and add it to TorBox)
        serviceCollection.AddSingleton<TorviaSearch.Services.TmdbClient>();
        serviceCollection.AddSingleton<TorviaSearch.Services.TorrentioClient>();
        serviceCollection.AddSingleton<TorviaSearch.Services.TorBoxApi>();
        serviceCollection.AddSingleton<TorviaSearch.Services.AddStore>();
        serviceCollection.AddSingleton<TorviaSearch.Services.SearchService>();

        serviceCollection.AddSingleton<TitleGuard>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<TitleGuard>());
        serviceCollection.AddSingleton<StreamStats>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<StreamStats>());
        // Adds the Search & Add button script to the Jellyfin web client's page.
        serviceCollection.AddTransient<IStartupFilter, Web.WebScriptInjector>();
        serviceCollection.PostConfigure<MvcOptions>(o => o.Filters.AddService<DirectPlayFilter>());
    }
}
