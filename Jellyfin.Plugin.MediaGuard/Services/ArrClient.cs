using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaGuard.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaGuard.Services;

/// <summary>
/// Client for communicating with Sonarr and Radarr APIs.
/// </summary>
public class ArrClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArrClient> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrClient"/> class.
    /// </summary>
    public ArrClient(IHttpClientFactory httpClientFactory, ILogger<ArrClient> logger, ILibraryManager libraryManager)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Requests a re-download for the given library item via Sonarr or Radarr.
    /// Returns true if a search was successfully triggered.
    /// </summary>
    public async Task<bool> RequestRedownloadAsync(BaseItem item, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return false;
        }

        bool success;

        if (item is Episode episode)
        {
            success = await HandleEpisodeAsync(episode, config, cancellationToken).ConfigureAwait(false);
        }
        else if (item is Movie movie)
        {
            success = await HandleMovieAsync(movie, config, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogDebug("MediaGuard: Item {Name} is not an Episode or Movie, skipping", item.Name);
            return false;
        }

        // Trigger a Jellyfin library scan so replaced files are picked up
        if (success)
        {
            ScheduleLibraryScan(item);
        }

        return success;
    }

    private async Task<bool> HandleEpisodeAsync(Episode episode, PluginConfiguration config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.SonarrApiKey) || string.IsNullOrEmpty(config.SonarrUrl))
        {
            _logger.LogWarning("MediaGuard: Sonarr not configured, cannot request re-download for {Name}", episode.Name);
            return false;
        }

        var seriesName = episode.SeriesName;
        var seasonNumber = episode.ParentIndexNumber;
        var episodeNumber = episode.IndexNumber;

        _logger.LogInformation(
            "MediaGuard: Detected corrupt episode - {Series} S{Season:D2}E{Episode:D2} ({Name}). Searching Sonarr...",
            seriesName, seasonNumber, episodeNumber, episode.Name);

        try
        {
            var client = CreateClient(config.SonarrUrl, config.SonarrApiKey);

            // Find the series in Sonarr
            var seriesResponse = await client.GetFromJsonAsync<List<ArrSeries>>(
                "api/v3/series", ct).ConfigureAwait(false);

            var series = seriesResponse?.FirstOrDefault(s =>
                s.Title != null && s.Title.Equals(seriesName, StringComparison.OrdinalIgnoreCase));

            if (series is null)
            {
                series = seriesResponse?.FirstOrDefault(s =>
                    s.Title != null && s.Title.Contains(seriesName ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            }

            if (series is null)
            {
                _logger.LogWarning("MediaGuard: Series '{Series}' not found in Sonarr. Add it to Sonarr first.", seriesName);
                return false;
            }

            // Find the episode in Sonarr
            var episodesResponse = await client.GetFromJsonAsync<List<ArrEpisode>>(
                $"api/v3/episode?seriesId={series.Id}", ct).ConfigureAwait(false);

            var sonarrEpisode = episodesResponse?.FirstOrDefault(e =>
                e.SeasonNumber == seasonNumber && e.EpisodeNumber == episodeNumber);

            if (sonarrEpisode is null)
            {
                _logger.LogWarning(
                    "MediaGuard: Episode S{Season:D2}E{Episode:D2} not found in Sonarr for series '{Series}'",
                    seasonNumber, episodeNumber, seriesName);
                return false;
            }

            // Delete the corrupt file from disk so Sonarr sees it as missing
            if (!string.IsNullOrEmpty(episode.Path) && System.IO.File.Exists(episode.Path))
            {
                _logger.LogInformation("MediaGuard: Deleting corrupt file: {Path}", episode.Path);
                System.IO.File.Delete(episode.Path);
            }

            // Tell Sonarr to refresh and search
            await client.PostAsJsonAsync(
                "api/v3/command",
                new { name = "RescanSeries", seriesId = series.Id },
                ct).ConfigureAwait(false);

            await client.PostAsJsonAsync(
                "api/v3/command",
                new { name = "EpisodeSearch", episodeIds = new[] { sonarrEpisode.Id } },
                ct).ConfigureAwait(false);

            _logger.LogInformation(
                "MediaGuard: Triggered Sonarr search for {Series} S{Season:D2}E{Episode:D2}",
                seriesName, seasonNumber, episodeNumber);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediaGuard: Failed to communicate with Sonarr for {Name}", episode.Name);
            return false;
        }
    }

    private async Task<bool> HandleMovieAsync(Movie movie, PluginConfiguration config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.RadarrApiKey) || string.IsNullOrEmpty(config.RadarrUrl))
        {
            _logger.LogWarning("MediaGuard: Radarr not configured, cannot request re-download for {Name}", movie.Name);
            return false;
        }

        _logger.LogInformation("MediaGuard: Detected corrupt movie - {Name}. Searching Radarr...", movie.Name);

        try
        {
            var client = CreateClient(config.RadarrUrl, config.RadarrApiKey);

            var moviesResponse = await client.GetFromJsonAsync<List<ArrMovie>>(
                "api/v3/movie", ct).ConfigureAwait(false);

            var radarrMovie = moviesResponse?.FirstOrDefault(m =>
                m.Title != null && m.Title.Equals(movie.Name, StringComparison.OrdinalIgnoreCase));

            if (radarrMovie is null)
            {
                radarrMovie = moviesResponse?.FirstOrDefault(m =>
                    m.Title != null
                    && m.Title.Contains(movie.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    && m.Year == movie.ProductionYear);
            }

            if (radarrMovie is null)
            {
                _logger.LogWarning("MediaGuard: Movie '{Name}' not found in Radarr. Add it to Radarr first.", movie.Name);
                return false;
            }

            // Delete the corrupt file from disk so Radarr sees it as missing
            if (!string.IsNullOrEmpty(movie.Path) && System.IO.File.Exists(movie.Path))
            {
                _logger.LogInformation("MediaGuard: Deleting corrupt file: {Path}", movie.Path);
                System.IO.File.Delete(movie.Path);
            }

            await client.PostAsJsonAsync(
                "api/v3/command",
                new { name = "RefreshMovie", movieIds = new[] { radarrMovie.Id } },
                ct).ConfigureAwait(false);

            await client.PostAsJsonAsync(
                "api/v3/command",
                new { name = "MoviesSearch", movieIds = new[] { radarrMovie.Id } },
                ct).ConfigureAwait(false);

            _logger.LogInformation("MediaGuard: Triggered Radarr search for {Name}", movie.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediaGuard: Failed to communicate with Radarr for {Name}", movie.Name);
            return false;
        }
    }

    private void ScheduleLibraryScan(BaseItem item)
    {
        // Queue a library scan for the parent folder so Jellyfin picks up the replacement
        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for the download to have a chance to complete
                await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);

                _logger.LogInformation("MediaGuard: Triggering Jellyfin library scan for replaced media");
                await _libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MediaGuard: Library scan failed (non-critical)");
            }
        });
    }

    private HttpClient CreateClient(string baseUrl, string apiKey)
    {
        var client = _httpClientFactory.CreateClient("MediaGuard");
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private sealed class ArrSeries
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }
    }

    private sealed class ArrEpisode
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("seasonNumber")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("episodeNumber")]
        public int EpisodeNumber { get; set; }

        [JsonPropertyName("hasFile")]
        public bool HasFile { get; set; }
    }

    private sealed class ArrMovie
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("year")]
        public int Year { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }
    }
}
