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

    public async Task<bool> IsDevicePairedAsync(string fingerprint)
    {
        await _lock.WaitAsync();
        try
        {
            var devices = await LoadDevicesInternalAsync();
            return devices.Any(d => string.Equals(d.PublicKeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> IsDeviceRevokedAsync(string fingerprint)
    {
        await _lock.WaitAsync();
        try
        {
            var devices = await LoadDevicesInternalAsync();
            var device = devices.FirstOrDefault(d => string.Equals(d.PublicKeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
            return device != null && device.IsRevoked;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<PairedDevice>> GetAllPairedDevicesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var devices = await LoadDevicesInternalAsync();
            return devices.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddPairedDeviceAsync(PairedDevice device)
    {
        await _lock.WaitAsync();
        try
        {
            var devices = await LoadDevicesInternalAsync();
            int existingIndex = devices.FindIndex(d => string.Equals(d.PublicKeyFingerprint, device.PublicKeyFingerprint, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                devices[existingIndex] = device;
            }
            else
            {
                devices.Add(device);
            }

            await SaveDevicesInternalAsync(devices);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RevokeDeviceAsync(string fingerprint)
    {
        await _lock.WaitAsync();
        try
        {
            var devices = await LoadDevicesInternalAsync();
            int existingIndex = devices.FindIndex(d => string.Equals(d.PublicKeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                devices[existingIndex] = devices[existingIndex] with { IsRevoked = true };
                await SaveDevicesInternalAsync(devices);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveDeviceAsync(string fingerprint)
    {
        await _lock.WaitAsync();
        try
        {
            var devices = await LoadDevicesInternalAsync();
            int removed = devices.RemoveAll(d => string.Equals(d.PublicKeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
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
