using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsNotch
{
    public enum LyricsStatus
    {
        NotFound,
        Synced,
        Plain,
        Instrumental,
        Unavailable
    }

    public sealed record LyricLine(TimeSpan Time, string Text);

    public sealed class LyricsResult
    {
        public LyricsStatus Status { get; init; }
        public IReadOnlyList<LyricLine> SyncedLines { get; init; } = Array.Empty<LyricLine>();
        public string PlainText { get; init; } = string.Empty;

        public static LyricsResult NotFound { get; } = new() { Status = LyricsStatus.NotFound };
        public static LyricsResult Instrumental { get; } = new() { Status = LyricsStatus.Instrumental };
        public static LyricsResult Unavailable { get; } = new() { Status = LyricsStatus.Unavailable };
    }

    public sealed class LyricsService : IDisposable
    {
        private const string BaseUrl = "https://lrclib.net/api";
        private const int MaxCacheEntries = 50;

        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

        private static readonly Regex TimestampRegex = new(
            @"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]",
            RegexOptions.Compiled);

        private static readonly Regex OffsetRegex = new(
            @"\[offset:\s*([+-]?\d+)\s*\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex EnhancedTagRegex = new(
            @"<[^>]+>",
            RegexOptions.Compiled);

        private static readonly Regex NoiseBracketRegex = new(
            @"\s*[\(\[]\s*[^\)\]]*\b(official|video|audio|lyrics?|visuali[sz]er|mv|m/v|music video|hd|hq|4k|8k|remaster(ed)?|explicit|clean|color\s*coded|lyric\s*video)\b[^\)\]]*[\)\]]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex FeatureRegex = new(
            @"\s*[\(\[]?\s*\b(feat\.?|ft\.?|featuring)\b[^\)\]]*[\)\]]?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TopicSuffixRegex = new(
            @"\s*-\s*Topic\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex VevoSuffixRegex = new(
            @"\s*VEVO\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex WhitespaceRegex = new(
            @"\s{2,}",
            RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly HttpClient Http = CreateHttpClient();

        private readonly Dictionary<string, CacheEntry> _cache = new();
        private readonly object _cacheGate = new();
        private bool _disposed;

        private sealed record CacheEntry(LyricsResult Result, DateTime StoredAt);

        private sealed class LrcLibTrack
        {
            public double Duration { get; set; }
            public bool Instrumental { get; set; }
            public string? PlainLyrics { get; set; }
            public string? SyncedLyrics { get; set; }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = RequestTimeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Notchi/2.0.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return client;
        }

        public static string BuildCacheKey(MediaInfo media)
        {
            var (title, artist) = Normalize(media.Title, media.Artist);
            int duration = media.Duration > TimeSpan.Zero
                ? (int)Math.Round(media.Duration.TotalSeconds)
                : 0;

            string album = WhitespaceRegex.Replace(media.Album.Trim(), " ");
            return $"{title}|{artist}|{album}|{duration}".ToLowerInvariant();
        }

        public async Task<LyricsResult> GetLyricsAsync(
            MediaInfo media,
            CancellationToken cancellationToken)
        {
            string key = BuildCacheKey(media);
            if (TryGetCached(key, out var cached)) return cached;

            LyricsResult result;
            try
            {
                result = await FetchAsync(media, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                result = LyricsResult.Unavailable;
            }

            Store(key, result);
            return result;
        }

        private static async Task<LyricsResult> FetchAsync(
            MediaInfo media,
            CancellationToken cancellationToken)
        {
            var (title, artist) = Normalize(media.Title, media.Artist);
            if (string.IsNullOrWhiteSpace(title)) return LyricsResult.NotFound;

            int duration = media.Duration > TimeSpan.Zero
                ? (int)Math.Round(media.Duration.TotalSeconds)
                : 0;

            string album = media.Album?.Trim() ?? string.Empty;

            var track = await TryGetTrackAsync(title, artist, album, duration, cancellationToken)
                .ConfigureAwait(false);

            track ??= await SearchTrackAsync(title, artist, duration, cancellationToken)
                .ConfigureAwait(false);

            return track == null ? LyricsResult.NotFound : ToResult(track);
        }

        private static async Task<LrcLibTrack?> TryGetTrackAsync(
            string title,
            string artist,
            string album,
            int duration,
            CancellationToken cancellationToken)
        {
            var query = new List<string>
            {
                $"track_name={Uri.EscapeDataString(title)}",
                $"artist_name={Uri.EscapeDataString(artist)}"
            };

            if (!string.IsNullOrWhiteSpace(album))
            {
                query.Add($"album_name={Uri.EscapeDataString(album)}");
            }

            if (duration > 0)
            {
                query.Add($"duration={duration}");
            }

            string url = $"{BaseUrl}/get?{string.Join("&", query)}";
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"LRCLIB returned {(int)response.StatusCode}");
            }

            return await DeserializeAsync<LrcLibTrack>(response, cancellationToken)
                .ConfigureAwait(false);
        }

        private static async Task<LrcLibTrack?> SearchTrackAsync(
            string title,
            string artist,
            int duration,
            CancellationToken cancellationToken)
        {
            string queryText = string.Join(" ",
                new[] { title, artist }.Where(value => !string.IsNullOrWhiteSpace(value)));

            if (string.IsNullOrWhiteSpace(queryText)) return null;

            string url = $"{BaseUrl}/search?q={Uri.EscapeDataString(queryText)}";
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"LRCLIB returned {(int)response.StatusCode}");
            }

            var tracks = await DeserializeAsync<List<LrcLibTrack>>(response, cancellationToken)
                .ConfigureAwait(false);

            if (tracks == null || tracks.Count == 0) return null;

            return tracks
                .Where(HasContent)
                .OrderBy(track =>
                    string.IsNullOrWhiteSpace(track.SyncedLyrics)
                        ? string.IsNullOrWhiteSpace(track.PlainLyrics) ? 2 : 1
                        : 0)
                .ThenBy(track =>
                    duration > 0 && track.Duration > 0
                        ? Math.Abs(track.Duration - duration)
                        : int.MaxValue)
                .FirstOrDefault();
        }

        private static bool HasContent(LrcLibTrack track)
        {
            return !string.IsNullOrWhiteSpace(track.SyncedLyrics) ||
                   !string.IsNullOrWhiteSpace(track.PlainLyrics) ||
                   track.Instrumental;
        }

        private static async Task<T?> DeserializeAsync<T>(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            return await JsonSerializer.DeserializeAsync<T>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        private static LyricsResult ToResult(LrcLibTrack track)
        {
            if (!string.IsNullOrWhiteSpace(track.SyncedLyrics))
            {
                var lines = ParseLrc(track.SyncedLyrics);
                if (lines.Count > 0)
                {
                    return new LyricsResult
                    {
                        Status = LyricsStatus.Synced,
                        SyncedLines = lines
                    };
                }
            }

            if (!string.IsNullOrWhiteSpace(track.PlainLyrics))
            {
                return new LyricsResult
                {
                    Status = LyricsStatus.Plain,
                    PlainText = NormalizePlainLyrics(track.PlainLyrics)
                };
            }

            if (track.Instrumental) return LyricsResult.Instrumental;

            return LyricsResult.NotFound;
        }

        private static string NormalizePlainLyrics(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }

        internal static IReadOnlyList<LyricLine> ParseLrc(string syncedLyrics)
        {
            var lines = new List<LyricLine>();
            if (string.IsNullOrWhiteSpace(syncedLyrics)) return lines;

            int offsetMs = 0;
            var offsetMatch = OffsetRegex.Match(syncedLyrics);
            if (offsetMatch.Success &&
                int.TryParse(offsetMatch.Groups[1].Value,
                    NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsedOffset))
            {
                offsetMs = parsedOffset;
            }

            foreach (string rawLine in syncedLyrics
                         .Replace("\r\n", "\n")
                         .Replace('\r', '\n')
                         .Split('\n'))
            {
                var matches = TimestampRegex.Matches(rawLine);
                if (matches.Count == 0) continue;

                string text = TimestampRegex.Replace(rawLine, string.Empty);
                text = EnhancedTagRegex.Replace(text, string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(text)) continue;

                foreach (Match match in matches)
                {
                    int minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    int seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

                    string fraction = match.Groups[3].Value;
                    int milliseconds = 0;
                    if (fraction.Length == 1)
                    {
                        milliseconds = int.Parse(fraction, CultureInfo.InvariantCulture) * 100;
                    }
                    else if (fraction.Length == 2)
                    {
                        milliseconds = int.Parse(fraction, CultureInfo.InvariantCulture) * 10;
                    }
                    else if (fraction.Length >= 3)
                    {
                        milliseconds = int.Parse(fraction[..3], CultureInfo.InvariantCulture);
                    }

                    long totalMilliseconds =
                        (long)minutes * 60000 + (long)seconds * 1000 + milliseconds - offsetMs;

                    if (totalMilliseconds < 0) totalMilliseconds = 0;

                    lines.Add(new LyricLine(
                        TimeSpan.FromMilliseconds(totalMilliseconds), text));
                }
            }

            return lines
                .OrderBy(line => line.Time)
                .ToList();
        }

        private static (string Title, string Artist) Normalize(string title, string artist)
        {
            title = title?.Trim() ?? string.Empty;
            artist = artist?.Trim() ?? string.Empty;

            title = RemoveBrackets(title);
            artist = RemoveBrackets(artist);

            artist = TopicSuffixRegex.Replace(artist, string.Empty);
            artist = VevoSuffixRegex.Replace(artist, string.Empty);

            int separator = title.IndexOf(" - ", StringComparison.Ordinal);
            if (separator > 0)
            {
                string prefix = title[..separator].Trim();
                string suffix = title[(separator + 3)..].Trim();

                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    if (string.IsNullOrWhiteSpace(artist))
                    {
                        artist = prefix;
                        title = suffix;
                    }
                    else if (string.Equals(prefix, artist, StringComparison.OrdinalIgnoreCase))
                    {
                        title = suffix;
                    }
                }
            }

            title = CleanSpacing(title);
            artist = CleanSpacing(artist);

            return (title, artist);
        }

        private static string RemoveBrackets(string value)
        {
            value = NoiseBracketRegex.Replace(value, string.Empty);
            value = FeatureRegex.Replace(value, string.Empty);
            return value;
        }

        private static string CleanSpacing(string value)
        {
            value = WhitespaceRegex.Replace(value, " ");
            return value.Trim(' ', '-', '–', '—', '|');
        }

        private bool TryGetCached(string key, out LyricsResult result)
        {
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    if (DateTime.UtcNow - entry.StoredAt <= CacheLifetime)
                    {
                        result = entry.Result;
                        return true;
                    }

                    _cache.Remove(key);
                }
            }

            result = LyricsResult.NotFound;
            return false;
        }

        private void Store(string key, LyricsResult result)
        {
            lock (_cacheGate)
            {
                if (_cache.Count >= MaxCacheEntries)
                {
                    var oldest = _cache
                        .OrderBy(entry => entry.Value.StoredAt)
                        .FirstOrDefault();

                    if (!string.IsNullOrEmpty(oldest.Key))
                    {
                        _cache.Remove(oldest.Key);
                    }
                }

                _cache[key] = new CacheEntry(result, DateTime.UtcNow);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cache.Clear();
        }
    }
}
