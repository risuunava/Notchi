using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WindowsNotch
{
    public sealed class PairedDevice
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string TokenHash { get; set; } = string.Empty;
        public DateTime PairedAt { get; set; }
        public DateTime LastSeen { get; set; }
    }

    public static class DeviceStore
    {
        private static readonly object FileLock = new();

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true
        };

        public static string StorePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Notchi", "paired_devices.json");

        public static List<PairedDevice> Load()
        {
            lock (FileLock)
            {
                try
                {
                    if (!File.Exists(StorePath)) return new List<PairedDevice>();

                    return JsonSerializer.Deserialize<List<PairedDevice>>(
                               File.ReadAllText(StorePath), SerializerOptions)
                           ?? new List<PairedDevice>();
                }
                catch
                {
                    return new List<PairedDevice>();
                }
            }
        }

        public static bool Save(List<PairedDevice> devices)
        {
            lock (FileLock)
            {
                try
                {
                    string? directory = Path.GetDirectoryName(StorePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    string tempPath = StorePath + ".tmp";
                    File.WriteAllText(tempPath, JsonSerializer.Serialize(devices, SerializerOptions));
                    File.Move(tempPath, StorePath, overwrite: true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static string ComputeSha256(string token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(token);
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static void AddOrUpdateDevice(string deviceId, string deviceName, string sessionToken)
        {
            var hash = ComputeSha256(sessionToken);
            var now = DateTime.UtcNow;

            lock (FileLock)
            {
                var list = Load();
                var existing = list.Find(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.DeviceName = deviceName;
                    existing.TokenHash = hash;
                    existing.LastSeen = now;
                }
                else
                {
                    list.Add(new PairedDevice
                    {
                        DeviceId = deviceId,
                        DeviceName = deviceName,
                        TokenHash = hash,
                        PairedAt = now,
                        LastSeen = now
                    });
                }
                Save(list);
            }
        }

        public static PairedDevice? ValidateToken(string sessionToken)
        {
            if (string.IsNullOrWhiteSpace(sessionToken)) return null;
            var hash = ComputeSha256(sessionToken);

            lock (FileLock)
            {
                var list = Load();
                var device = list.Find(d => string.Equals(d.TokenHash, hash, StringComparison.OrdinalIgnoreCase));
                if (device != null)
                {
                    device.LastSeen = DateTime.UtcNow;
                    Save(list);
                }
                return device;
            }
        }

        public static bool RevokeDevice(string deviceId)
        {
            lock (FileLock)
            {
                var list = Load();
                int count = list.RemoveAll(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                if (count > 0)
                {
                    Save(list);
                    return true;
                }
                return false;
            }
        }

        public static bool RevokeByToken(string sessionToken)
        {
            if (string.IsNullOrWhiteSpace(sessionToken)) return false;
            var hash = ComputeSha256(sessionToken);

            lock (FileLock)
            {
                var list = Load();
                int count = list.RemoveAll(d => string.Equals(d.TokenHash, hash, StringComparison.OrdinalIgnoreCase));
                if (count > 0)
                {
                    Save(list);
                    return true;
                }
                return false;
            }
        }
    }
}
