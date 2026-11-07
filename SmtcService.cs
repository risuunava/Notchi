using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WindowsNotch
{
    public sealed class MediaInfo
    {
        public string Title { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
        public string Album { get; init; } = string.Empty;
        public BitmapImage? AlbumArt { get; init; }
        public byte[]? AlbumArtBytes { get; init; }
        public string SourceAppId { get; init; } = string.Empty;
        public bool IsPlaying { get; init; }
        public TimeSpan Position { get; init; }
        public TimeSpan Duration { get; init; }
        public int ArtVersion { get; init; }
    }

    /// <summary>
    /// Membungkus Windows System Media Transport Controls (SMTC):
    /// membaca sesi media aktif (Spotify, Chrome, Edge, dll) dan mengirim perintah playback.
    /// </summary>
    public sealed class SmtcService : IDisposable
    {
        // Setelah judul/video berubah, thumbnail Chrome kadang masih berupa icon
        // aplikasi. Selama jendela ini thumbnail dibaca ulang agar artwork video
        // yang datang belakangan menimpa fallback tersebut.
        private static readonly TimeSpan ArtRetryWindow = TimeSpan.FromSeconds(5);

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private BitmapImage? _cachedArt;
        private byte[]? _cachedArtBytes;
        private int _artVersion;
        private string _cachedArtKey = string.Empty;
        private DateTime _artKeyChangedAt;
        private readonly System.Threading.SemaphoreSlim _refreshGate = new(1, 1);
        private readonly System.Threading.Timer _artRetryTimer;

        public SmtcService()
        {
            _artRetryTimer = new System.Threading.Timer(
                _ => RetryThumbnailTick(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        /// <summary>Dipanggil setiap data media berubah; null berarti tidak ada sesi media.</summary>
        public event Action<MediaInfo?>? MediaChanged;

        public MediaInfo? CurrentMedia { get; private set; }

        public byte[]? GetCurrentArtworkBytes() => _cachedArtBytes;
        public int CurrentArtVersion => _artVersion;

        public async Task InitializeAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.SessionsChanged += OnSessionsChanged;
                AttachSession();
            }
            catch
            {
                // SMTC tidak tersedia -> aplikasi tetap memakai mode jam
            }
        }

        private void OnSessionsChanged(
            GlobalSystemMediaTransportControlsSessionManager sender,
            SessionsChangedEventArgs args) => AttachSession();

        private void AttachSession()
        {
            try
            {
                var sessions = _manager?.GetSessions();
                var current = _session;

                GlobalSystemMediaTransportControlsSession? next;
                if (current != null && sessions != null && sessions.Contains(current))
                {
                    // Sticky: pertahankan sesi yang sedang berjalan agar tidak
                    // berpindah-pindah antar tab/aplikasi saat SessionsChanged.
                    next = IsPlaying(current) ? current : sessions.FirstOrDefault(IsPlaying);
                    next ??= current;
                }
                else
                {
                    next = sessions?.FirstOrDefault(IsPlaying) ?? _manager?.GetCurrentSession();
                }

                if (!ReferenceEquals(next, current))
                {
                    DetachSession();
                    _session = next;
                    _cachedArt = null;
                    _cachedArtBytes = null;
                    _cachedArtKey = string.Empty;
                }

                if (_session != null && !ReferenceEquals(next, current))
                {
                    _session.MediaPropertiesChanged += OnSessionEvent;
                    _session.PlaybackInfoChanged += OnSessionEvent;
                    _session.TimelinePropertiesChanged += OnSessionEvent;
                }
            }
            catch
            {
                _session = null;
            }

            _ = RefreshAsync();
        }

        private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                return session.GetPlaybackInfo().PlaybackStatus ==
                       GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                return false;
            }
        }

        private void DetachSession()
        {
            if (_session == null) return;

            _session.MediaPropertiesChanged -= OnSessionEvent;
            _session.PlaybackInfoChanged -= OnSessionEvent;
            _session.TimelinePropertiesChanged -= OnSessionEvent;
            _session = null;
        }

        private void OnSessionEvent(
            GlobalSystemMediaTransportControlsSession sender,
            object args) => _ = RefreshAsync();

        // Memastikan jendela retry tetap berjalan walau tidak ada event baru
        // (mis. Chrome mengirim artwork video tanpa MediaPropertiesChanged).
        private void RetryThumbnailTick()
        {
            if (_cachedArtKey.Length == 0) return;

            var age = DateTime.UtcNow - _artKeyChangedAt;
            if (_cachedArt != null)
            {
                if (age >= ArtRetryWindow) return;
            }
            else if (age >= TimeSpan.FromSeconds(30))
            {
                return;
            }

            _ = RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            // Serialisasi refresh agar event beruntun tidak saling menimpa
            if (!await _refreshGate.WaitAsync(0)) return;

            try
            {
                var session = _session;
                if (session == null)
                {
                    CurrentMedia = null;
                    MediaChanged?.Invoke(null);
                    return;
                }

                var properties = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();

                var title = properties.Title ?? string.Empty;
                var artist = properties.Artist ?? string.Empty;
                if (string.IsNullOrWhiteSpace(artist))
                {
                    artist = properties.AlbumArtist ?? string.Empty;
                }

                // Kunci thumbnail termasuk app sumber agar artwork tidak tertukar
                var sourceAppId = session.SourceAppUserModelId ?? string.Empty;
                var artKey = $"{sourceAppId}|{title}|{artist}|{properties.AlbumTitle}";

                if (artKey != _cachedArtKey)
                {
                    _cachedArtKey = artKey;
                    _artKeyChangedAt = DateTime.UtcNow;
                    _cachedArt = null;
                    _cachedArtBytes = null;
                    _artVersion++;
                }

                // Chrome kadang mengirim icon aplikasi lebih dulu dan artwork video
                // menyusul. Selama jendela retry, baca ulang dan pakai yang terakhir.
                // Jangan pernah mengunci hasil null: coba lagi pada refresh berikutnya.
                bool insideRetryWindow = DateTime.UtcNow - _artKeyChangedAt < ArtRetryWindow;
                if (_cachedArt == null || insideRetryWindow)
                {
                    var (art, bytes) = await LoadThumbnailWithBytesAsync(properties.Thumbnail);
                    if (art != null && bytes != null)
                    {
                        if (_cachedArtBytes == null || !bytes.SequenceEqual(_cachedArtBytes))
                        {
                            _artVersion++;
                        }
                        _cachedArt = art;
                        _cachedArtBytes = bytes;
                    }
                }

                var mediaInfo = new MediaInfo
                {
                    Title = title,
                    Artist = artist,
                    Album = properties.AlbumTitle ?? string.Empty,
                    AlbumArt = _cachedArt,
                    AlbumArtBytes = _cachedArtBytes,
                    SourceAppId = sourceAppId,
                    IsPlaying = playback.PlaybackStatus ==
                                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    Position = GetPosition(),
                    Duration = timeline.EndTime,
                    ArtVersion = _artVersion
                };

                CurrentMedia = mediaInfo;
                MediaChanged?.Invoke(mediaInfo);
            }
            catch
            {
                // Sesi bisa hilang di tengah pembacaan; abaikan
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        /// <summary>Posisi playback saat ini (dikoreksi dengan waktu berjalan).</summary>
        public TimeSpan GetPosition()
        {
            var session = _session;
            if (session == null) return TimeSpan.Zero;

            try
            {
                var timeline = session.GetTimelineProperties();
                var position = timeline.Position;
                var playing = session.GetPlaybackInfo().PlaybackStatus ==
                              GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                if (playing)
                {
                    var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                    if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(5))
                    {
                        position += elapsed;
                    }
                }

                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                if (timeline.EndTime > TimeSpan.Zero && position > timeline.EndTime)
                {
                    position = timeline.EndTime;
                }

                return position;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }

        public async Task PlayPauseAsync()
        {
            try
            {
                if (_session != null) await _session.TryTogglePlayPauseAsync();
            }
            catch { /* sesi sudah tidak valid */ }
        }

        public async Task PlayAsync()
        {
            try
            {
                if (_session != null) await _session.TryPlayAsync();
            }
            catch { /* sesi sudah tidak valid */ }
        }

        public async Task PauseAsync()
        {
            try
            {
                if (_session != null) await _session.TryPauseAsync();
            }
            catch { /* sesi sudah tidak valid */ }
        }

        public async Task NextAsync()
        {
            try
            {
                if (_session != null) await _session.TrySkipNextAsync();
            }
            catch { /* sesi sudah tidak valid */ }
        }

        public async Task PreviousAsync()
        {
            try
            {
                if (_session != null) await _session.TrySkipPreviousAsync();
            }
            catch { /* sesi sudah tidak valid */ }
        }

        public async Task SeekAsync(TimeSpan position)
        {
            try
            {
                // TryChangePlaybackPositionAsync memakai satuan 100ns (== TimeSpan.Ticks)
                if (_session != null) await _session.TryChangePlaybackPositionAsync(position.Ticks);
            }
            catch { /* sesi sudah tidak valid */ }
        }

        private static async Task<(BitmapImage?, byte[]?)> LoadThumbnailWithBytesAsync(
            IRandomAccessStreamReference? reference)
        {
            if (reference == null) return (null, null);

            try
            {
                using var stream = await reference.OpenReadAsync();
                if (stream.Size == 0) return (null, null);

                var size = (int)Math.Min(stream.Size, 4 * 1024 * 1024);
                byte[] buffer;

                using (var reader = new DataReader(stream))
                {
                    var loaded = await reader.LoadAsync((uint)size);
                    buffer = new byte[loaded];
                    reader.ReadBytes(buffer);
                }

                if (buffer.Length == 0) return (null, null);

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(buffer);
                image.EndInit();
                image.Freeze();
                return (image, buffer);
            }
            catch
            {
                return (null, null);
            }
        }

        public void Dispose()
        {
            _artRetryTimer.Dispose();

            if (_manager != null)
            {
                _manager.SessionsChanged -= OnSessionsChanged;
                _manager = null;
            }

            DetachSession();
        }
    }
}
