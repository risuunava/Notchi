using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace WindowsNotch
{
    public sealed class MobileServerService : IDisposable
    {
        private static MobileServerService? _instance;
        public static MobileServerService? Instance => _instance;

        private readonly SmtcService _smtcService;
        private readonly SystemStatusService _systemStatusService;
        private IHost? _host;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<Guid, WebSocketClientSession> _clients = new();
        private readonly ConcurrentDictionary<string, string> _sharedFiles = new();
        private readonly object _volumeSync = new();
        private AudioVolumeService? _volumeService;

        public event Action<string>? OnFileReceived;
        public event Action<string>? OnTextReceived;

        public bool IsRunning => _host != null;
        public bool HasConnectedClients => !_clients.IsEmpty;

        public MobileServerService(SmtcService smtcService, SystemStatusService systemStatusService)
        {
            _instance = this;
            _smtcService = smtcService;
            _systemStatusService = systemStatusService;

            _smtcService.MediaChanged += BroadcastMediaState;
        }

        public async Task StartAsync(int port)
        {
            await StopAsync();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            var builder = Host.CreateDefaultBuilder();
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
            });

            builder.ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseKestrel(options =>
                {
                    options.ListenAnyIP(port);
                    options.Limits.MaxRequestBodySize = null; // Diatur dinamis di endpoint
                });

                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.Configure<FormOptions>(options =>
                    {
                        options.MultipartBodyLengthLimit = long.MaxValue;
                    });
                });

                webBuilder.Configure(app =>
                {
                    app.UseWebSockets(new WebSocketOptions
                    {
                        KeepAliveInterval = TimeSpan.FromSeconds(20)
                    });

                    app.UseRouting();

                    app.UseEndpoints(endpoints =>
                    {
                        // 1. GET /info
                        endpoints.MapGet("/info", HandleInfoAsync);

                        // 2. POST /pair
                        endpoints.MapPost("/pair", HandlePairAsync);

                        // 3. GET /status
                        endpoints.MapGet("/status", HandleStatusAsync);

                        // 4. GET /media/art
                        endpoints.MapGet("/media/art", HandleMediaArtAsync);

                        // 5. POST /shelf
                        endpoints.MapPost("/shelf", HandleShelfUploadAsync);

                        // 5b. GET /shelf/file
                        endpoints.MapGet("/shelf/file", HandleShelfFileDownloadAsync);

                        // 6. DELETE /devices/me
                        endpoints.MapDelete("/devices/me", HandleDeleteDeviceAsync);

                        // 7. GET /ws
                        endpoints.Map("/ws", HandleWebSocketAsync);
                    });
                });
            });

            _host = builder.Build();
            await _host.StartAsync(token);
        }

        public async Task StopAsync()
        {
            if (_host != null)
            {
                try
                {
                    // Tutup semua koneksi websocket
                    foreach (var client in _clients.Values)
                    {
                        try
                        {
                            await client.CloseAsync(4003, "feature_disabled");
                        }
                        catch { }
                    }
                    _clients.Clear();

                    _cts?.Cancel();
                    await _host.StopAsync(TimeSpan.FromSeconds(2));
                    _host.Dispose();
                }
                catch { }
                finally
                {
                    _host = null;
                    _cts?.Dispose();
                    _cts = null;
                }
            }
        }

        public void DisconnectDevice(string deviceId)
        {
            foreach (var kvp in _clients)
            {
                if (string.Equals(kvp.Value.Device?.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    _ = kvp.Value.CloseAsync(4001, "revoked");
                }
            }
        }

        // ==================== ENDPOINT HANDLERS ====================

        private static async Task HandleInfoAsync(HttpContext context)
        {
            var settings = SettingsStore.Load();
            var response = new
            {
                apiVersion = 1,
                appVersion = "2.0.0",
                pcId = settings.PcId ?? Guid.NewGuid().ToString(),
                pcName = Environment.MachineName
            };

            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(response));
        }

        private static async Task HandlePairAsync(HttpContext context)
        {
            var settings = SettingsStore.Load();
            if (!settings.IsMobileServerEnabled)
            {
                context.Response.StatusCode = 403;
                await WriteErrorAsync(context, "feature_disabled", "Notchi Mobile dimatikan di Settings.");
                return;
            }

            string clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!PairingService.CheckRateLimit(clientIp))
            {
                context.Response.StatusCode = 429;
                await WriteErrorAsync(context, "rate_limited", "Terlalu banyak percobaan pairing gagal.");
                return;
            }

            JsonElement body;
            try
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                string json = await reader.ReadToEndAsync();
                body = JsonSerializer.Deserialize<JsonElement>(json);
            }
            catch
            {
                context.Response.StatusCode = 400;
                await WriteErrorAsync(context, "bad_request", "Body JSON tidak valid.");
                return;
            }

            string? pairingToken = body.TryGetProperty("pairingToken", out var pTok) ? pTok.GetString() : null;
            string? deviceId = body.TryGetProperty("deviceId", out var pDevId) ? pDevId.GetString() : null;
            string? deviceName = body.TryGetProperty("deviceName", out var pDevName) ? pDevName.GetString() : null;

            if (string.IsNullOrWhiteSpace(pairingToken) ||
                string.IsNullOrWhiteSpace(deviceId) ||
                string.IsNullOrWhiteSpace(deviceName))
            {
                context.Response.StatusCode = 400;
                await WriteErrorAsync(context, "bad_request", "Field pairingToken, deviceId, dan deviceName wajib diisi.");
                return;
            }

            if (!PairingService.IsPairingTokenValid(pairingToken))
            {
                PairingService.RecordFailedAttempt(clientIp);
                context.Response.StatusCode = 401;
                await WriteErrorAsync(context, "pairing_token_expired", "Pairing token tidak valid atau sudah kedaluwarsa.");
                return;
            }

            // Pairing lewat scan QR langsung disetujui tanpa prompt konfirmasi
            PairingService.InvalidatePairingToken();

            // Hasilkan session token
            string sessionToken = PairingService.GenerateSessionToken();
            DeviceStore.AddOrUpdateDevice(deviceId, deviceName, sessionToken);
            PairingService.NotifyPairingApproved();

            var res = new
            {
                sessionToken = sessionToken,
                pcId = settings.PcId,
                pcName = Environment.MachineName
            };

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(res));
        }

        private async Task HandleStatusAsync(HttpContext context)
        {
            var device = Authenticate(context);
            if (device == null)
            {
                context.Response.StatusCode = 401;
                await WriteErrorAsync(context, "invalid_token", "Session token tidak valid atau sudah dicabut.");
                return;
            }

            var snapshot = _systemStatusService.GetCurrentSnapshot();
            var payload = BuildStatusPayload(snapshot);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
        }

        private async Task HandleMediaArtAsync(HttpContext context)
        {
            var device = Authenticate(context);
            if (device == null)
            {
                context.Response.StatusCode = 401;
                await WriteErrorAsync(context, "invalid_token", "Session token tidak valid atau sudah dicabut.");
                return;
            }

            byte[]? art = _smtcService.GetCurrentArtworkBytes();
            if (art == null || art.Length == 0)
            {
                context.Response.StatusCode = 404;
                await WriteErrorAsync(context, "no_media_session", "Tidak ada artwork lagu yang sedang diputar.");
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "image/jpeg";
            await context.Response.Body.WriteAsync(art);
        }

        private async Task HandleDeleteDeviceAsync(HttpContext context)
        {
            string? token = ExtractBearerToken(context);
            if (string.IsNullOrWhiteSpace(token))
            {
                context.Response.StatusCode = 401;
                await WriteErrorAsync(context, "invalid_token", "Token otentikasi tidak ditemukan.");
                return;
            }

            var device = DeviceStore.ValidateToken(token);
            if (device == null)
            {
                context.Response.StatusCode = 401;
                await WriteErrorAsync(context, "invalid_token", "Session token tidak valid.");
                return;
            }

            DeviceStore.RevokeDevice(device.DeviceId);
            DisconnectDevice(device.DeviceId);

            context.Response.StatusCode = 204;
        }

        private async Task HandleShelfUploadAsync(HttpContext context)
        {
            var device = Authenticate(context);
            if (device == null)
            {
                context.Response.StatusCode = 401;
                await WriteErrorAsync(context, "invalid_token", "Session token tidak valid atau sudah dicabut.");
                return;
            }

            if (!context.Request.HasFormContentType)
            {
                context.Response.StatusCode = 415;
                await WriteErrorAsync(context, "unsupported_media_type", "Harus berupa multipart/form-data.");
                return;
            }

            var settings = SettingsStore.Load();
            long maxBytes = settings.MobileMaxFileSizeMb * 1024 * 1024;

            var boundary = GetBoundary(context.Request.ContentType);
            if (string.IsNullOrEmpty(boundary))
            {
                context.Response.StatusCode = 400;
                await WriteErrorAsync(context, "bad_request", "Boundary multipart tidak ditemukan.");
                return;
            }

            var reader = new MultipartReader(boundary, context.Request.Body);
            MultipartSection? section;

            string? createdPath = null;
            string? fileName = null;
            long fileSize = 0;
            string? textContent = null;

            string downloadsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (!Directory.Exists(downloadsFolder))
            {
                downloadsFolder = Path.GetTempPath();
            }

            while ((section = await reader.ReadNextSectionAsync()) != null)
            {
                if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition))
                {
                    string name = contentDisposition.Name.Value ?? "";

                    if (name == "text")
                    {
                        using var streamReader = new StreamReader(section.Body, Encoding.UTF8);
                        textContent = await streamReader.ReadToEndAsync();
                    }
                    else if (name == "file")
                    {
                        string originalName = contentDisposition.FileName.Value ?? contentDisposition.FileNameStar.Value ?? "upload.bin";
                        originalName = Path.GetFileName(originalName); // Sanitize path traversal
                        if (string.IsNullOrWhiteSpace(originalName)) originalName = "upload.bin";

                        // Cek bentrok nama
                        string destinationPath = Path.Combine(downloadsFolder, originalName);
                        destinationPath = GetUniqueFilePath(destinationPath);

                        using (var targetStream = File.Create(destinationPath))
                        {
                            var buffer = new byte[81920];
                            int read;
                            while ((read = await section.Body.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                fileSize += read;
                                if (fileSize > maxBytes)
                                {
                                    targetStream.Close();
                                    File.Delete(destinationPath);
                                    context.Response.StatusCode = 413;
                                    await WriteErrorAsync(context, "file_too_large", $"Ukuran file melebihi batas {settings.MobileMaxFileSizeMb} MB.");
                                    return;
                                }
                                await targetStream.WriteAsync(buffer, 0, read);
                            }
                        }

                        createdPath = destinationPath;
                        fileName = Path.GetFileName(destinationPath);
                    }
                }
            }

            string resultId = Guid.NewGuid().ToString();

            if (!string.IsNullOrEmpty(createdPath))
            {
                // Beritahu UI
                OnFileReceived?.Invoke(createdPath);

                var res = new
                {
                    id = resultId,
                    name = fileName,
                    size = fileSize
                };

                context.Response.StatusCode = 201;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(JsonSerializer.Serialize(res));
                return;
            }
            else if (!string.IsNullOrEmpty(textContent))
            {
                OnTextReceived?.Invoke(textContent);

                var res = new
                {
                    id = resultId,
                    name = "Catatan Ponsel",
                    size = Encoding.UTF8.GetByteCount(textContent)
                };

                context.Response.StatusCode = 201;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(JsonSerializer.Serialize(res));
                return;
            }

            context.Response.StatusCode = 400;
            await WriteErrorAsync(context, "bad_request", "Tidak ada part 'file' atau 'text' di request.");
        }

        private async Task HandleShelfFileDownloadAsync(HttpContext context)
        {
            var device = Authenticate(context);
            if (device == null)
            {
                context.Response.StatusCode = 401;
                await WriteErrorAsync(context, "invalid_token", "Session token tidak valid atau sudah dicabut.");
                return;
            }

            string? fileId = context.Request.Query["id"].ToString();
            if (string.IsNullOrWhiteSpace(fileId) || !_sharedFiles.TryGetValue(fileId, out var filePath))
            {
                context.Response.StatusCode = 404;
                await WriteErrorAsync(context, "file_not_found", "File tidak ditemukan atau link sudah kedaluwarsa.");
                return;
            }

            if (!File.Exists(filePath))
            {
                context.Response.StatusCode = 404;
                await WriteErrorAsync(context, "file_not_found", "File fisik tidak ada di disk.");
                return;
            }

            string fileName = Path.GetFileName(filePath);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers[HeaderNames.ContentDisposition] = $"attachment; filename=\"{Uri.EscapeDataString(fileName)}\"";
            context.Response.ContentLength = new FileInfo(filePath).Length;

            await context.Response.SendFileAsync(filePath);
        }

        public async Task<bool> SendShelfFileAsync(string filePath)
        {
            if (!File.Exists(filePath) || _clients.IsEmpty) return false;

            string fileId = Guid.NewGuid().ToString("N");
            _sharedFiles[fileId] = filePath;

            var fileInfo = new FileInfo(filePath);
            var message = new
            {
                type = "shelf.file",
                data = new
                {
                    id = fileId,
                    name = fileInfo.Name,
                    size = fileInfo.Length
                }
            };

            bool sentAny = false;
            foreach (var client in _clients.Values)
            {
                await client.SendMessageAsync(message);
                sentAny = true;
            }

            return sentAny;
        }

        private async Task HandleWebSocketAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await WriteErrorAsync(context, "bad_request", "Harus berupa permintaan upgrade WebSocket.");
                return;
            }

            var device = Authenticate(context);
            if (device == null)
            {
                context.Response.StatusCode = 401;
                await WriteErrorAsync(context, "invalid_token", "Session token tidak valid atau sudah dicabut.");
                return;
            }

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var sessionId = Guid.NewGuid();
            var clientSession = new WebSocketClientSession(sessionId, webSocket, device, this);
            _clients.TryAdd(sessionId, clientSession);

            try
            {
                await clientSession.RunAsync();
            }
            finally
            {
                _clients.TryRemove(sessionId, out _);
            }
        }

        // ==================== WEBSOCKET DISPATCHING ====================

        public async Task ProcessClientMessageAsync(WebSocketClientSession client, string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string type = root.GetProperty("type").GetString() ?? "";
                string? id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

                switch (type)
                {
                    case "ping":
                        await client.SendMessageAsync(new { type = "pong" });
                        break;

                    case "media.play":
                        await _smtcService.PlayAsync();
                        if (id != null) await client.SendMessageAsync(new { type = "ack", id });
                        break;

                    case "media.pause":
                        await _smtcService.PauseAsync();
                        if (id != null) await client.SendMessageAsync(new { type = "ack", id });
                        break;

                    case "media.toggle":
                        await _smtcService.PlayPauseAsync();
                        if (id != null) await client.SendMessageAsync(new { type = "ack", id });
                        break;

                    case "media.next":
                        await _smtcService.NextAsync();
                        if (id != null) await client.SendMessageAsync(new { type = "ack", id });
                        break;

                    case "media.prev":
                        await _smtcService.PreviousAsync();
                        if (id != null) await client.SendMessageAsync(new { type = "ack", id });
                        break;

                    case "media.seek":
                        if (root.TryGetProperty("data", out var seekData) &&
                            seekData.TryGetProperty("positionSeconds", out var posProp))
                        {
                            double seconds = posProp.GetDouble();
                            await _smtcService.SeekAsync(TimeSpan.FromSeconds(seconds));
                        }
                        if (id != null) await client.SendMessageAsync(new { type = "ack", id });
                        break;

                    case "status.subscribe":
                        int intervalMs = 2000;
                        if (root.TryGetProperty("data", out var subData) &&
                            subData.TryGetProperty("intervalMs", out var intProp))
                        {
                            intervalMs = Math.Max(1000, intProp.GetInt32());
                        }
                        client.SubscribeStatus(intervalMs);
                        if (id != null) await client.SendMessageAsync(new { type = "ack", id });
                        break;

                    case "status.unsubscribe":
                        client.UnsubscribeStatus();
                        if (id != null) await client.SendMessageAsync(new { type = "ack", id });
                        break;

                    case "volume.set":
                        if (root.TryGetProperty("data", out var volumeSetData) &&
                            volumeSetData.TryGetProperty("level", out var levelProp) &&
                            levelProp.ValueKind == JsonValueKind.Number)
                        {
                            SetVolume(levelProp.GetDouble());
                            BroadcastVolumeState();
                        }
                        if (id != null) await client.SendMessageAsync(new { type = "ack", id });
                        break;

                    case "volume.mute":
                        if (root.TryGetProperty("data", out var muteData) &&
                            muteData.TryGetProperty("muted", out var mutedProp))
                        {
                            SetMuted(mutedProp.ValueKind == JsonValueKind.True);
                            BroadcastVolumeState();
                        }
                        if (id != null) await client.SendMessageAsync(new { type = "ack", id });
                        break;

                    case "volume.toggle":
                        ToggleMuted();
                        BroadcastVolumeState();
                        if (id != null) await client.SendMessageAsync(new { type = "ack", id });
                        break;

                    case "volume.get":
                        await client.SendMessageAsync(new { type = "volume.state", data = BuildVolumePayload() });
                        break;

                    case "shelf.ack":
                        if (id != null) await client.SendMessageAsync(new { type = "ack", id });
                        break;
                }
            }
            catch (Exception ex)
            {
                await client.SendMessageAsync(new
                {
                    type = "error",
                    data = new { code = "bad_request", message = ex.Message }
                });
            }
        }

        // ==================== VOLUME ====================

        private AudioVolumeService GetVolumeLocked() => _volumeService ??= new AudioVolumeService();

        public object BuildVolumePayload()
        {
            lock (_volumeSync)
            {
                var volume = GetVolumeLocked();
                return new { level = volume.Volume, muted = volume.IsMuted };
            }
        }

        private void SetVolume(double level)
        {
            lock (_volumeSync)
            {
                GetVolumeLocked().Volume = (float)Math.Clamp(level, 0.0, 1.0);
            }
        }

        private void SetMuted(bool muted)
        {
            lock (_volumeSync)
            {
                GetVolumeLocked().IsMuted = muted;
            }
        }

        private void ToggleMuted()
        {
            lock (_volumeSync)
            {
                var volume = GetVolumeLocked();
                volume.IsMuted = !volume.IsMuted;
            }
        }

        private void BroadcastVolumeState()
        {
            var payload = BuildVolumePayload();
            var message = new { type = "volume.state", data = payload };

            foreach (var client in _clients.Values)
            {
                _ = client.SendMessageAsync(message);
            }
        }

        private void BroadcastMediaState(MediaInfo? media)
        {
            var payload = BuildMediaStatePayload(media);
            var message = new { type = "media.state", data = payload };

            foreach (var client in _clients.Values)
            {
                _ = client.SendMessageAsync(message);
            }
        }

        public object BuildMediaStatePayload(MediaInfo? media)
        {
            if (media == null)
            {
                return new
                {
                    available = false,
                    title = "",
                    artist = "",
                    album = "",
                    sourceApp = "",
                    playing = false,
                    positionSeconds = 0.0,
                    durationSeconds = 0.0,
                    canSeek = false,
                    artVersion = 0
                };
            }

            return new
            {
                available = true,
                title = media.Title,
                artist = media.Artist,
                album = "",
                sourceApp = media.SourceAppId,
                playing = media.IsPlaying,
                positionSeconds = media.Position.TotalSeconds,
                durationSeconds = media.Duration.TotalSeconds,
                canSeek = media.Duration > TimeSpan.Zero,
                artVersion = media.ArtVersion
            };
        }

        public object BuildStatusPayload(SystemStatusSnapshot snapshot)
        {
            object? batteryObj = null;
            if (snapshot.BatteryPercent.HasValue)
            {
                batteryObj = new
                {
                    percent = snapshot.BatteryPercent.Value,
                    charging = snapshot.IsCharging
                };
            }

            return new
            {
                cpuPercent = snapshot.CpuPercent,
                ramPercent = snapshot.RamPercent,
                diskPercent = snapshot.DiskPercent,
                battery = batteryObj,
                timestamp = DateTime.UtcNow.ToString("o")
            };
        }

        // ==================== HELPER METHODS ====================

        private static PairedDevice? Authenticate(HttpContext context)
        {
            string? token = ExtractBearerToken(context);
            if (string.IsNullOrWhiteSpace(token)) return null;

            return DeviceStore.ValidateToken(token);
        }

        private static string? ExtractBearerToken(HttpContext context)
        {
            string? authHeader = context.Request.Headers["Authorization"];
            if (!string.IsNullOrWhiteSpace(authHeader) &&
                authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authHeader.Substring("Bearer ".Length).Trim();
            }
            return null;
        }

        private static async Task WriteErrorAsync(HttpContext context, string code, string message)
        {
            var err = new
            {
                error = new
                {
                    code = code,
                    message = message
                }
            };
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(err));
        }

        private static string? GetBoundary(string? contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return null;
            var elements = contentType.Split(';');
            foreach (var element in elements)
            {
                var trimmed = element.Trim();
                if (trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
                {
                    var boundary = trimmed.Substring("boundary=".Length).Trim('\"');
                    return boundary;
                }
            }
            return null;
        }

        private static string GetUniqueFilePath(string filePath)
        {
            if (!File.Exists(filePath)) return filePath;

            string dir = Path.GetDirectoryName(filePath) ?? "";
            string nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
            string ext = Path.GetExtension(filePath);

            int counter = 1;
            string newPath;
            do
            {
                newPath = Path.Combine(dir, $"{nameWithoutExt} ({counter}){ext}");
                counter++;
            } while (File.Exists(newPath));

            return newPath;
        }

        public void Dispose()
        {
            _smtcService.MediaChanged -= BroadcastMediaState;

            try
            {
                lock (_volumeSync)
                {
                    _volumeService?.Dispose();
                    _volumeService = null;
                }
            }
            catch { }

            _ = StopAsync();
        }
    }

    public sealed class WebSocketClientSession
    {
        public Guid SessionId { get; }
        private readonly WebSocket _socket;
        public PairedDevice? Device { get; }
        private readonly MobileServerService _server;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private Timer? _statusTimer;

        public WebSocketClientSession(Guid sessionId, WebSocket socket, PairedDevice? device, MobileServerService server)
        {
            SessionId = sessionId;
            _socket = socket;
            Device = device;
            _server = server;
        }

        public async Task RunAsync()
        {
            // 1. Kirim hello segera setelah tersambung
            var settings = SettingsStore.Load();
            await SendMessageAsync(new
            {
                type = "hello",
                data = new
                {
                    apiVersion = 1,
                    pcName = Environment.MachineName,
                    mediaAvailable = true
                }
            });

            // 2. Kirim media.state saat ini sekali
            var mediaState = _server.BuildMediaStatePayload(_server.InstanceSmtc()?.CurrentMedia);
            await SendMessageAsync(new
            {
                type = "media.state",
                data = mediaState
            });

            // 3. Kirim volume.state saat ini sekali
            await SendMessageAsync(new
            {
                type = "volume.state",
                data = _server.BuildVolumePayload()
            });

            var buffer = new byte[4096];
            using var ms = new MemoryStream();

            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string msg = Encoding.UTF8.GetString(ms.ToArray());
                        await _server.ProcessClientMessageAsync(this, msg);
                    }
                }
            }
            catch { }
            finally
            {
                _statusTimer?.Dispose();
            }
        }

        public void SubscribeStatus(int intervalMs)
        {
            _statusTimer?.Dispose();
            _statusTimer = new Timer(async _ =>
            {
                if (_socket.State != WebSocketState.Open) return;
                var snapshot = _server.InstanceStatus()?.GetCurrentSnapshot();
                if (snapshot.HasValue)
                {
                    var data = _server.BuildStatusPayload(snapshot.Value);
                    await SendMessageAsync(new { type = "status.update", data = data });
                }
            }, null, 0, intervalMs);
        }

        public void UnsubscribeStatus()
        {
            _statusTimer?.Dispose();
            _statusTimer = null;
        }

        public async Task SendMessageAsync(object message)
        {
            if (_socket.State != WebSocketState.Open) return;

            string json = JsonSerializer.Serialize(message);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync();
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
            }
            catch { }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task CloseAsync(int code, string reason)
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await SendMessageAsync(new { type = "bye", data = new { reason } });
                    await _socket.CloseAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);
                }
            }
            catch { }
        }
    }

    internal static class MobileServerExtensions
    {
        public static SmtcService? InstanceSmtc(this MobileServerService server)
        {
            // Helper access via reflection or field
            var field = typeof(MobileServerService).GetField("_smtcService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(server) as SmtcService;
        }

        public static SystemStatusService? InstanceStatus(this MobileServerService server)
        {
            var field = typeof(MobileServerService).GetField("_systemStatusService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(server) as SystemStatusService;
        }
    }
}
