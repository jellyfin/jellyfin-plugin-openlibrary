using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenLibrary.Providers
{
    /// <summary>
    /// OpenLibrary cover image provider for books.
    /// </summary>
    public class OpenLibraryImageProvider : IRemoteImageProvider
    {
        private const int MaxCovers = 10;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OpenLibraryImageProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenLibraryImageProvider"/> class.
        /// </summary>
        /// <param name="logger">Instance of the <see cref="ILogger{OpenLibraryImageProvider}"/> interface.</param>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        public OpenLibraryImageProvider(
            ILogger<OpenLibraryImageProvider> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        /// <inheritdoc />
        public string Name => "OpenLibrary";

        /// <inheritdoc />
        public bool Supports(BaseItem item)
        {
            return item is Book;
        }

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var images = new List<RemoteImageInfo>();

            var workId = item.GetProviderId("OpenLibrary");
            if (string.IsNullOrEmpty(workId))
            {
                return images;
            }

            var workUrl = $"https://openlibrary.org/works/{workId}.json";

            using var httpClient = _httpClientFactory.CreateClient(PluginServiceRegistrator.OpenLibraryHttpClientName);

            try
            {
                var response = await httpClient.GetAsync(workUrl, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("OpenLibrary cover request failed with status: {StatusCode} for: {Key}", response.StatusCode, workId);
                    return images;
                }

                var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(jsonContent);

                if (document.RootElement.TryGetProperty("covers", out var coversElement) && coversElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cover in coversElement.EnumerateArray())
                    {
                        // Missing covers are stored as null or -1
                        if (cover.ValueKind != JsonValueKind.Number || !cover.TryGetInt32(out var coverId) || coverId <= 0)
                        {
                            continue;
                        }

                        images.Add(new RemoteImageInfo
                        {
                            ProviderName = Name,
                            Type = ImageType.Primary,
                            Url = $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg"
                        });

                        if (images.Count >= MaxCovers)
                        {
                            break;
                        }
                    }
                }
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("OpenLibrary cover request timed out for: {Key}", workId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting OpenLibrary covers for: {Key}", workId);
            }

            return images;
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var httpClient = _httpClientFactory.CreateClient(PluginServiceRegistrator.OpenLibraryHttpClientName);
            return httpClient.GetAsync(new Uri(url), cancellationToken);
        }
    }
}
