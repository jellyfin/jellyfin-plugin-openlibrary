using System;
using System.Net.Http.Headers;
using Jellyfin.Plugin.OpenLibrary.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.OpenLibrary
{
    /// <summary>
    /// Register OpenLibrary services.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <summary>
        /// The name of the HTTP client used for OpenLibrary API requests.
        /// </summary>
        public const string OpenLibraryHttpClientName = "OpenLibrary";

        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // Register the rate-limited HTTP handler
            serviceCollection.AddTransient<ClientSideRateLimitedHandler>();

            // Register a named HTTP client with rate limiting for OpenLibrary API
            serviceCollection.AddHttpClient(OpenLibraryHttpClientName, client =>
                {
                    // OpenLibrary asks API clients to identify themselves in the User-Agent
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Jellyfin-OpenLibrary-Plugin", typeof(PluginServiceRegistrator).Assembly.GetName().Version?.ToString()));
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/jellyfin/jellyfin-plugin-openlibrary)"));
                })
                .ConfigurePrimaryHttpMessageHandler<ClientSideRateLimitedHandler>()
                .SetHandlerLifetime(TimeSpan.FromMinutes(5));

            // Register the metadata providers
            serviceCollection.AddSingleton<OpenLibraryProvider>();
            serviceCollection.AddSingleton<IRemoteMetadataProvider<Person, PersonLookupInfo>, OpenLibraryPersonProvider>();
        }
    }
}
