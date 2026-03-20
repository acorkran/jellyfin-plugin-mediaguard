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
            _logger.LogDebug("MediarrGuard: Item {Name} is not an Episode or Movie, skipping", item.Name);
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
            _logger.LogWarning("MediarrGuard: Sonarr not configured, cannot request re-download for {Name}", episode.Name);
            return false;
        }

        var seriesName = episode.SeriesName;
        var seasonNumber = episode.ParentIndexNumber;
        var episodeNumber = episode.IndexNumber;

        _logger.LogInformation(
            "MediarrGuard: Detected corrupt episode - {Series} S{Season:D2}E{Episode:D2} ({Name}). Searching Sonarr...",
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
                _logger.LogWarning("MediarrGuard: Series '{Series}' not found in Sonarr. Add it to Sonarr first.", seriesName);
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
                    "MediarrGuard: Episode S{Season:D2}E{Episode:D2} not found in Sonarr for series '{Series}'",
                    seasonNumber, episodeNumber, seriesName);
                return false;
            }

            // Delete the corrupt episode file via Sonarr's API so only this file is affected
            // (avoids RescanSeries which re-evaluates the entire series against the quality profile)
            var episodeFileDeleted = await DeleteSonarrEpisodeFileAsync(client, sonarrEpisode.Id, episode.Path, ct).ConfigureAwait(false);

            if (!episodeFileDeleted)
            {
                // Fallback: delete from disk manually if the API route failed
                if (!string.IsNullOrEmpty(episode.Path) && System.IO.File.Exists(episode.Path))
                {
                    _logger.LogInformation("MediarrGuard: Fallback - deleting corrupt file from disk: {Path}", episode.Path);
                    System.IO.File.Delete(episode.Path);
                }
            }

            // Search for just this episode - no RescanSeries needed
            await client.PostAsJsonAsync(
                "api/v3/command",
                new { name = "EpisodeSearch", episodeIds = new[] { sonarrEpisode.Id } },
                ct).ConfigureAwait(false);

            _logger.LogInformation(
                "MediarrGuard: Triggered Sonarr search for {Series} S{Season:D2}E{Episode:D2}",
                seriesName, seasonNumber, episodeNumber);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediarrGuard: Failed to communicate with Sonarr for {Name}", episode.Name);
            return false;
        }
    }

    private async Task<bool> HandleMovieAsync(Movie movie, PluginConfiguration config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.RadarrApiKey) || string.IsNullOrEmpty(config.RadarrUrl))
        {
            _logger.LogWarning("MediarrGuard: Radarr not configured, cannot request re-download for {Name}", movie.Name);
            return false;
        }

        _logger.LogInformation("MediarrGuard: Detected corrupt movie - {Name}. Searching Radarr...", movie.Name);

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
                _logger.LogWarning("MediarrGuard: Movie '{Name}' not found in Radarr. Add it to Radarr first.", movie.Name);
                return false;
            }

            // Delete the corrupt movie file via Radarr's API (targeted, no full library re-evaluation)
            var movieFileDeleted = await DeleteRadarrMovieFileAsync(client, radarrMovie.Id, movie.Path, ct).ConfigureAwait(false);

            if (!movieFileDeleted)
            {
                if (!string.IsNullOrEmpty(movie.Path) && System.IO.File.Exists(movie.Path))
                {
                    _logger.LogInformation("MediarrGuard: Fallback - deleting corrupt file from disk: {Path}", movie.Path);
                    System.IO.File.Delete(movie.Path);
                }
            }

            await client.PostAsJsonAsync(
                "api/v3/command",
                new { name = "MoviesSearch", movieIds = new[] { radarrMovie.Id } },
                ct).ConfigureAwait(false);

            _logger.LogInformation("MediarrGuard: Triggered Radarr search for {Name}", movie.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediarrGuard: Failed to communicate with Radarr for {Name}", movie.Name);
            return false;
        }
    }

    /// <summary>
    /// Deletes a specific episode file via Sonarr's API so Sonarr stays in sync
    /// without needing a full series rescan.
    /// </summary>
    private async Task<bool> DeleteSonarrEpisodeFileAsync(HttpClient client, int sonarrEpisodeId, string? filePath, CancellationToken ct)
    {
        try
        {
            // Get the episode file ID from the episode details
            var episodeDetail = await client.GetFromJsonAsync<ArrEpisodeDetail>(
                $"api/v3/episode/{sonarrEpisodeId}", ct).ConfigureAwait(false);

            if (episodeDetail?.EpisodeFileId is null or 0)
            {
                _logger.LogWarning("MediarrGuard: No episode file ID found in Sonarr for episode {Id}", sonarrEpisodeId);
                return false;
            }

            var response = await client.DeleteAsync(
                $"api/v3/episodefile/{episodeDetail.EpisodeFileId}", ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("MediarrGuard: Deleted episode file {FileId} via Sonarr API", episodeDetail.EpisodeFileId);
                return true;
            }

            _logger.LogWarning("MediarrGuard: Sonarr returned {Status} when deleting episode file {FileId}",
                response.StatusCode, episodeDetail.EpisodeFileId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediarrGuard: Failed to delete episode file via Sonarr API, will fall back to disk deletion");
            return false;
        }
    }

    /// <summary>
    /// Deletes a specific movie file via Radarr's API.
    /// </summary>
    private async Task<bool> DeleteRadarrMovieFileAsync(HttpClient client, int radarrMovieId, string? filePath, CancellationToken ct)
    {
        try
        {
            // Get movie details to find the file ID
            var movieDetail = await client.GetFromJsonAsync<ArrMovieDetail>(
                $"api/v3/movie/{radarrMovieId}", ct).ConfigureAwait(false);

            if (movieDetail?.MovieFileId is null or 0)
            {
                _logger.LogWarning("MediarrGuard: No movie file ID found in Radarr for movie {Id}", radarrMovieId);
                return false;
            }

            var response = await client.DeleteAsync(
                $"api/v3/moviefile/{movieDetail.MovieFileId}", ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("MediarrGuard: Deleted movie file {FileId} via Radarr API", movieDetail.MovieFileId);
                return true;
            }

            _logger.LogWarning("MediarrGuard: Radarr returned {Status} when deleting movie file {FileId}",
                response.StatusCode, movieDetail.MovieFileId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediarrGuard: Failed to delete movie file via Radarr API, will fall back to disk deletion");
            return false;
        }
    }

    private void ScheduleLibraryScan(BaseItem item)
    {
        // Queue library scans at 5 and 15 minutes so replacements get picked up
        // even if the download takes longer than expected
        _ = Task.Run(async () =>
        {
            var scanDelays = new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15) };

            foreach (var delay in scanDelays)
            {
                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);

                    _logger.LogInformation("MediarrGuard: Triggering Jellyfin library scan ({Delay} min delay)", delay.TotalMinutes);
                    await _libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "MediarrGuard: Library scan failed (non-critical)");
                }
            }
        });
    }

    private HttpClient CreateClient(string baseUrl, string apiKey)
    {
        var client = _httpClientFactory.CreateClient("MediarrGuard");
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

    private sealed class ArrEpisodeDetail
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("episodeFileId")]
        public int EpisodeFileId { get; set; }
    }

    private sealed class ArrMovieDetail
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("movieFileId")]
        public int MovieFileId { get; set; }
    }
}
