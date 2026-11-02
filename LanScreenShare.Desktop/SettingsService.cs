using System;
using System.IO;
using System.Text.Json;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Desktop
{
    public static class SettingsService
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanScreenShare");

        private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private static readonly object FileLock = new();

        public static AppSettings Load()
        {
            lock (FileLock)
            {
                try
                {
                    if (File.Exists(SettingsFilePath))
                    {
                        string json = File.ReadAllText(SettingsFilePath);
                        var settings = JsonSerializer.Deserialize<AppSettings>(json);
                        if (settings != null)
                            return settings;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Settings] Error cargando configuración: {ex.Message}");
                }

                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            lock (FileLock)
            {
                try
                {
                    if (!Directory.Exists(SettingsDirectory))
                    {
                        Directory.CreateDirectory(SettingsDirectory);
                    }

                    string json = JsonSerializer.Serialize(settings, JsonOptions);
                    File.WriteAllText(SettingsFilePath, json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Settings] Error guardando configuración: {ex.Message}");
                }
            }
        }
    }
}

