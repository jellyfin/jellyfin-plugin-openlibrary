using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenLibrary.Providers
{
    /// <summary>
    /// OpenLibrary metadata provider for books.
    /// </summary>
    public class OpenLibraryProvider : IRemoteMetadataProvider<Book, BookInfo>
    {
        private const string ProviderKey = "OpenLibrary";
        private const string SearchFields = "key,title,author_name,author_key,cover_i,cover_edition_key,first_publish_year,series_name,ratings_average";
        private const int IdentifyResultLimit = 10;
        private const int MetadataResultLimit = 3;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OpenLibraryProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenLibraryProvider"/> class.
        /// </summary>
        /// <param name="logger">Instance of the <see cref="ILogger{OpenLibraryProvider}"/> interface.</param>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        public OpenLibraryProvider(
            ILogger<OpenLibraryProvider> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        /// <inheritdoc />
        public string Name => "OpenLibrary";

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(BookInfo searchInfo, CancellationToken cancellationToken)
        {
            _logger.LogInformation("OpenLibrary search for: {Title}", searchInfo.Name);

            var remoteResults = new List<RemoteSearchResult>();

            try
            {
                var searchResults = await FindWorks(searchInfo, IdentifyResultLimit, cancellationToken).ConfigureAwait(false);

                foreach (var searchResult in searchResults)
                {
                    var remoteResult = new RemoteSearchResult
                    {
                        Name = searchResult.Title,
                        SearchProviderName = Name,
                        ProductionYear = searchResult.FirstPublishYear
                    };

                    remoteResult.SetProviderId(ProviderKey, GetWorkId(searchResult.Key));

                    if (searchResult.AuthorNames.Count > 0)
                    {
                        remoteResult.Overview = string.Join(", ", searchResult.AuthorNames);
                    }

                    if (searchResult.CoverId.HasValue)
                    {
                        remoteResult.ImageUrl = $"https://covers.openlibrary.org/b/id/{searchResult.CoverId.Value}-M.jpg";
                    }

                    remoteResults.Add(remoteResult);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching OpenLibrary for {Title}", searchInfo.Name);
            }

            return remoteResults;
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Book>> GetMetadata(BookInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Book>();

            if (string.IsNullOrWhiteSpace(info.Name) && string.IsNullOrEmpty(info.GetProviderId(ProviderKey)))
            {
                return result;
            }

            try
            {
                // Step 1: Look up the identified work or search for the book
                var searchResults = await FindWorks(info, MetadataResultLimit, cancellationToken).ConfigureAwait(false);
                if (searchResults.Count == 0)
                {
                    return result;
                }

                // Step 2: Get detailed information for the first result
                var firstResult = searchResults.First();
                var title = string.IsNullOrWhiteSpace(info.Name) ? firstResult.Title : info.Name;
                var bookMetadata = await GetBookDetails(firstResult, title, cancellationToken).ConfigureAwait(false);
                if (bookMetadata != null)
                {
                    bookMetadata.Item.SetProviderId(ProviderKey, GetWorkId(firstResult.Key));
                    result.Item = bookMetadata.Item;
                    result.HasMetadata = true;
                    result.People = bookMetadata.People;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting OpenLibrary metadata for {Title}", info.Name);
            }

            return result;
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var httpClient = _httpClientFactory.CreateClient(PluginServiceRegistrator.OpenLibraryHttpClientName);
            return httpClient.GetAsync(new Uri(url), cancellationToken);
        }

        private static string GetWorkId(string workKey)
        {
            return workKey.Replace("/works/", string.Empty, StringComparison.Ordinal);
        }

        private Task<List<OpenLibrarySearchResult>> FindWorks(BookInfo info, int limit, CancellationToken cancellationToken)
        {
            // A work chosen through `Identify` is prioritized over the title
            var workId = info.GetProviderId(ProviderKey);
            if (!string.IsNullOrEmpty(workId))
            {
                return SearchOpenLibrary($"q={HttpUtility.UrlEncode("key:/works/" + workId)}", workId, 1, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(info.Name))
            {
                return Task.FromResult(new List<OpenLibrarySearchResult>());
            }

            return SearchOpenLibrary($"title={HttpUtility.UrlEncode(info.Name)}", info.Name, limit, cancellationToken);
        }

        private async Task<List<OpenLibrarySearchResult>> SearchOpenLibrary(string query, string title, int limit, CancellationToken cancellationToken)
        {
            var searchUrl = $"https://openlibrary.org/search.json?{query}&limit={limit}&fields={SearchFields}";

            using var httpClient = _httpClientFactory.CreateClient(PluginServiceRegistrator.OpenLibraryHttpClientName);

            try
            {
                var response = await httpClient.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("OpenLibrary search failed with status: {StatusCode} for: {Title}", response.StatusCode, title);
                    return new List<OpenLibrarySearchResult>();
                }

                var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseSearchResults(jsonContent);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("OpenLibrary search timed out for: {Title}", title);
                return new List<OpenLibrarySearchResult>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching OpenLibrary for: {Title}", title);
                return new List<OpenLibrarySearchResult>();
            }
        }

        private async Task<MetadataResult<Book>?> GetBookDetails(OpenLibrarySearchResult searchResult, string originalTitle, CancellationToken cancellationToken)
        {
            // Try works endpoint first (what we were using before)
            var worksUrl = $"https://openlibrary.org{searchResult.Key}.json";

            var book = await TryGetBookFromUrl(worksUrl, originalTitle, searchResult.Key, cancellationToken).ConfigureAwait(false);

            // The search result carries the proper series name
            if (book != null && !string.IsNullOrEmpty(searchResult.SeriesName))
            {
                book.SeriesName = searchResult.SeriesName;
            }

            // If works didn't have series info and we have a cover edition, try that
            if (book != null && string.IsNullOrEmpty(book.SeriesName) && !string.IsNullOrEmpty(searchResult.CoverEdition))
            {
                var editionUrl = $"https://openlibrary.org/books/{searchResult.CoverEdition}.json";

                var editionBook = await TryGetBookFromUrl(editionUrl, originalTitle, searchResult.CoverEdition, cancellationToken).ConfigureAwait(false);
                if (editionBook != null && !string.IsNullOrEmpty(editionBook.SeriesName))
                {
                    // Merge series info from edition into the works result
                    book.SeriesName = editionBook.SeriesName;
                }
            }

            // If works failed but we have cover edition, try edition as fallback
            if (book == null && !string.IsNullOrEmpty(searchResult.CoverEdition))
            {
                var editionUrl = $"https://openlibrary.org/books/{searchResult.CoverEdition}.json";
                book = await TryGetBookFromUrl(editionUrl, originalTitle, searchResult.CoverEdition, cancellationToken).ConfigureAwait(false);
            }

            if (book == null)
            {
                return null;
            }

            if (!book.ProductionYear.HasValue && searchResult.FirstPublishYear.HasValue)
            {
                book.ProductionYear = searchResult.FirstPublishYear;
            }

            if (searchResult.RatingsAverage.HasValue)
            {
                // OpenLibrary rates out of 5, Jellyfin out of 10
                book.CommunityRating = (float)Math.Round(searchResult.RatingsAverage.Value * 2, 1);
            }

            // Fetch author bios if we have author keys
            var people = new List<PersonInfo>();
            if (searchResult.AuthorKeys.Count > 0)
            {
                foreach (var authorKey in searchResult.AuthorKeys)
                {
                    var authorInfo = await GetAuthorInfo(authorKey, cancellationToken).ConfigureAwait(false);
                    if (authorInfo != null)
                    {
                        people.Add(authorInfo);
                    }
                }
            }

            // Create metadata result with author information
            var result = new MetadataResult<Book>
            {
                Item = book,
                HasMetadata = true,
                People = people
            };

            return result;
        }

        private async Task<Book?> TryGetBookFromUrl(string url, string originalTitle, string key, CancellationToken cancellationToken)
        {
            using var httpClient = _httpClientFactory.CreateClient(PluginServiceRegistrator.OpenLibraryHttpClientName);

            try
            {
                var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("OpenLibrary request failed with status: {StatusCode} for: {Key}", response.StatusCode, key);
                    return null;
                }

                var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseBookDetails(jsonContent, originalTitle, key);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("OpenLibrary request timed out for: {Key}", key);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting OpenLibrary details for: {Key}", key);
                return null;
            }
        }

        private List<OpenLibrarySearchResult> ParseSearchResults(string jsonContent)
        {
            var results = new List<OpenLibrarySearchResult>();

            try
            {
                using var document = JsonDocument.Parse(jsonContent);
                var root = document.RootElement;

                if (root.TryGetProperty("docs", out var docsElement))
                {
                    foreach (var doc in docsElement.EnumerateArray())
                    {
                        if (doc.TryGetProperty("key", out var keyElement))
                        {
                            var key = keyElement.GetString();
                            if (!string.IsNullOrEmpty(key))
                            {
                                var title = string.Empty;
                                if (doc.TryGetProperty("title", out var titleElement))
                                {
                                    title = titleElement.GetString() ?? string.Empty;
                                }

                                var coverEdition = string.Empty;
                                if (doc.TryGetProperty("cover_edition_key", out var coverEditionElement))
                                {
                                    coverEdition = coverEditionElement.GetString() ?? string.Empty;
                                }

                                var authorKeys = new List<string>();
                                if (doc.TryGetProperty("author_key", out var authorKeyElement))
                                {
                                    if (authorKeyElement.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var authorKey in authorKeyElement.EnumerateArray())
                                        {
                                            var keyValue = authorKey.GetString();
                                            if (!string.IsNullOrEmpty(keyValue))
                                            {
                                                authorKeys.Add(keyValue);
                                            }
                                        }
                                    }
                                }

                                var authorNames = new List<string>();
                                if (doc.TryGetProperty("author_name", out var authorNameElement))
                                {
                                    authorNames = ExtractStringArray(authorNameElement);
                                }

                                int? coverId = null;
                                if (doc.TryGetProperty("cover_i", out var coverIdElement) && coverIdElement.TryGetInt32(out var coverIdValue))
                                {
                                    coverId = coverIdValue;
                                }

                                int? firstPublishYear = null;
                                if (doc.TryGetProperty("first_publish_year", out var yearElement) && yearElement.TryGetInt32(out var yearValue))
                                {
                                    firstPublishYear = yearValue;
                                }

                                string? seriesName = null;
                                if (doc.TryGetProperty("series_name", out var seriesNameElement))
                                {
                                    seriesName = ExtractStringValue(seriesNameElement);
                                }

                                double? ratingsAverage = null;
                                if (doc.TryGetProperty("ratings_average", out var ratingElement) && ratingElement.TryGetDouble(out var ratingValue))
                                {
                                    ratingsAverage = ratingValue;
                                }

                                results.Add(new OpenLibrarySearchResult
                                {
                                    Key = key,
                                    Title = title,
                                    CoverEdition = coverEdition,
                                    AuthorKeys = authorKeys,
                                    AuthorNames = authorNames,
                                    CoverId = coverId,
                                    FirstPublishYear = firstPublishYear,
                                    SeriesName = seriesName,
                                    RatingsAverage = ratingsAverage
                                });
                            }
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error parsing OpenLibrary search results");
            }

            return results;
        }

        private Book? ParseBookDetails(string jsonContent, string originalTitle, string openLibraryKey)
        {
            try
            {
                using var document = JsonDocument.Parse(jsonContent);
                var root = document.RootElement;

                var book = new Book
                {
                    Name = originalTitle
                };

                // Extract description
                if (root.TryGetProperty("description", out var descElement))
                {
                    var description = ExtractStringOrTextValue(descElement);
                    if (!string.IsNullOrEmpty(description))
                    {
                        book.Overview = description;
                    }
                }

                // Extract series information
                if (root.TryGetProperty("series", out var seriesElement))
                {
                    var series = ExtractStringValue(seriesElement);
                    if (!string.IsNullOrEmpty(series))
                    {
                        book.SeriesName = series;
                    }

                    // Works list series as objects holding a series key and the position of the book in it
                    if (seriesElement.ValueKind == JsonValueKind.Array
                        && seriesElement.GetArrayLength() > 0
                        && seriesElement[0].ValueKind == JsonValueKind.Object
                        && seriesElement[0].TryGetProperty("position", out var positionElement)
                        && positionElement.ValueKind == JsonValueKind.String
                        && int.TryParse(positionElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var position))
                    {
                        book.IndexNumber = position;
                    }
                }

                // Extract publication date
                if (root.TryGetProperty("first_publish_date", out var dateElement))
                {
                    var dateStr = dateElement.GetString();
                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var date))
                    {
                        book.ProductionYear = date.Year;
                        book.PremiereDate = date;
                    }
                }

                // Extract subjects as genres
                if (root.TryGetProperty("subjects", out var subjectsElement))
                {
                    var genres = ExtractStringArray(subjectsElement);
                    foreach (var genre in genres.Take(5)) // Limit to first 5 genres
                    {
                        book.AddGenre(genre);
                    }
                }

                // Note: Provider ID setting removed for now
                return book;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error parsing OpenLibrary book details");
                return null;
            }
        }

        private static string? ExtractStringOrTextValue(JsonElement element)
        {
            // OpenLibrary sometimes stores descriptions as objects with "value" property
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("value", out var valueElement))
                {
                    return valueElement.GetString();
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString();
            }

            return null;
        }

        private static string? ExtractStringValue(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0)
            {
                return element[0].ValueKind == JsonValueKind.String ? element[0].GetString() : null;
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString();
            }

            return null;
        }

        private static List<string> ExtractStringArray(JsonElement element)
        {
            var result = new List<string>();

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (!string.IsNullOrEmpty(value))
                    {
                        result.Add(value);
                    }
                }
            }

            return result;
        }

        private async Task<PersonInfo?> GetAuthorInfo(string authorKey, CancellationToken cancellationToken)
        {
            var authorUrl = $"https://openlibrary.org/authors/{authorKey}.json";
            _logger.LogInformation("Fetching OpenLibrary author info: {Url}", authorUrl);

            using var httpClient = _httpClientFactory.CreateClient(PluginServiceRegistrator.OpenLibraryHttpClientName);

            try
            {
                var response = await httpClient.GetAsync(authorUrl, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("OpenLibrary author request failed with status: {StatusCode} for: {AuthorKey}", response.StatusCode, authorKey);
                    return null;
                }

                var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseAuthorInfo(jsonContent, authorKey);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("OpenLibrary author request timed out for: {AuthorKey}", authorKey);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting OpenLibrary author info for: {AuthorKey}", authorKey);
                return null;
            }
        }

        private PersonInfo? ParseAuthorInfo(string jsonContent, string authorKey)
        {
            try
            {
                using var document = JsonDocument.Parse(jsonContent);
                var root = document.RootElement;

                var name = string.Empty;
                if (root.TryGetProperty("name", out var nameElement))
                {
                    name = nameElement.GetString() ?? string.Empty;
                }

                if (string.IsNullOrEmpty(name))
                {
                    _logger.LogWarning("No name found for OpenLibrary author: {AuthorKey}", authorKey);
                    return null;
                }

                var personInfo = new PersonInfo
                {
                    Name = name,
                    Type = Jellyfin.Data.Enums.PersonKind.Author
                };

                // Extract biography (PersonInfo doesn't support Overview, but log if found)
                if (root.TryGetProperty("bio", out var bioElement))
                {
                    var bio = ExtractStringOrTextValue(bioElement);
                    if (!string.IsNullOrEmpty(bio))
                    {
                        _logger.LogDebug("Found biography for author {Name} (length: {Length})", name, bio.Length);
                    }
                }

                // Extract birth date
                if (root.TryGetProperty("birth_date", out var birthElement))
                {
                    var birthDate = birthElement.GetString();
                    if (!string.IsNullOrEmpty(birthDate))
                    {
                        _logger.LogDebug("Found birth date for author {Name}: {BirthDate}", name, birthDate);
                    }
                }

                return personInfo;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error parsing OpenLibrary author info for: {AuthorKey}", authorKey);
                return null;
            }
        }

        private sealed class OpenLibrarySearchResult
        {
            public string Key { get; set; } = string.Empty;

            public string Title { get; set; } = string.Empty;

            public string CoverEdition { get; set; } = string.Empty;

            public List<string> AuthorKeys { get; set; } = new List<string>();

            public List<string> AuthorNames { get; set; } = new List<string>();

            public int? CoverId { get; set; }

            public int? FirstPublishYear { get; set; }

            public string? SeriesName { get; set; }

            public double? RatingsAverage { get; set; }
        }
    }
}
