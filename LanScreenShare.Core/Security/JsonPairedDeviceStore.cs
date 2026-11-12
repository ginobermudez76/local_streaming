using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Security;

/// <summary>
/// Almacén persistente en JSON para dispositivos emparejados y de confianza, con escrituras atómicas
/// para tolerancia ante apagones o fallas repentinas. Cumple con la especificación ADR-001.
/// </summary>
public class JsonPairedDeviceStore : IPairedDeviceStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public JsonPairedDeviceStore(string? customFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customFilePath))
        {
            _filePath = customFilePath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = Path.Combine(appData, "LanScreenShare");
            _filePath = Path.Combine(appFolder, "trusted_devices.json");
        }
    }

    public Task<bool> IsDevicePairedAsync(string fingerprint) =>
        QueryDevicesAsync(devices => devices.Any(d => string.Equals(d.PublicKeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)));

    public Task<bool> IsDeviceRevokedAsync(string fingerprint) =>
        QueryDevicesAsync(devices =>
        {
            var device = devices.FirstOrDefault(d => string.Equals(d.PublicKeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
            return device != null && device.IsRevoked;
        });

    public Task<IReadOnlyList<PairedDevice>> GetAllPairedDevicesAsync() =>
        QueryDevicesAsync<IReadOnlyList<PairedDevice>>(devices => devices.ToList());

    public Task AddPairedDeviceAsync(PairedDevice device) =>
        MutateDevicesAsync(devices =>
        {
            int existingIndex = FindDeviceIndex(devices, device.PublicKeyFingerprint);
            if (existingIndex >= 0)
            {
                devices[existingIndex] = device;
            }
            else
            {
                devices.Add(device);
            }
            return true;
        });

    public Task RevokeDeviceAsync(string fingerprint) =>
        MutateDevicesAsync(devices =>
        {
            int existingIndex = FindDeviceIndex(devices, fingerprint);
            if (existingIndex >= 0)
            {
                devices[existingIndex] = devices[existingIndex] with { IsRevoked = true };
                return true;
            }
            return false;
        });

    private static int FindDeviceIndex(List<PairedDevice> devices, string fingerprint) =>
        devices.FindIndex(d => string.Equals(d.PublicKeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

    public Task RemoveDeviceAsync(string fingerprint) =>
        MutateDevicesAsync(devices =>
            devices.RemoveAll(d => string.Equals(d.PublicKeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) > 0);

    private async Task<T> QueryDevicesAsync<T>(Func<List<PairedDevice>, T> query)
    {
        await _lock.WaitAsync();
        try
        {
            var devices = await LoadDevicesInternalAsync();
            return query(devices);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task MutateDevicesAsync(Func<List<PairedDevice>, bool> mutate)
    {
        await _lock.WaitAsync();
        try
        {
            var devices = await LoadDevicesInternalAsync();
            if (mutate(devices))
            {
                await SaveDevicesInternalAsync(devices);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<PairedDevice>> LoadDevicesInternalAsync()
    {
        if (!File.Exists(_filePath))
        {
            return new List<PairedDevice>();
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var result = await JsonSerializer.DeserializeAsync<List<PairedDevice>>(stream, JsonOptions);
            return result ?? new List<PairedDevice>();
        }
        catch (JsonException)
        {
            // Respaldar archivo corrupto para evitar sobreescritura ciega
            string corruptBackup = _filePath + $".corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Copy(_filePath, corruptBackup, overwrite: true);
            }
            catch { }

            return new List<PairedDevice>();
        }
    }

    private async Task SaveDevicesInternalAsync(List<PairedDevice> devices)
    {
        string directory = Path.GetDirectoryName(_filePath)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = _filePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, devices, JsonOptions);
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}

