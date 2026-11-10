using System;

namespace LanScreenShare.Core.Models
{
    public enum CaptureSourceType
    {
        Screen,         // Pantalla / Monitor individual
        AllScreens,     // Todas las pantallas (Escritorio virtual)
        Window          // Ventana / Aplicación específica
    }

    public class CaptureSource
    {
        public CaptureSourceType Type { get; set; } = CaptureSourceType.Screen;
        public string Id { get; set; } = "screen:0";
        public string Name { get; set; } = "Pantalla 1";
        public string? Subtitle { get; set; }
        public IntPtr Handle { get; set; } = IntPtr.Zero;
        public int MonitorIndex { get; set; } = 0;
        public string? DeviceName { get; set; }
        public string? ProcessName { get; set; }
        public int ProcessId { get; set; } = 0;
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsPrimary { get; set; }
        public bool IsAllScreens => Type == CaptureSourceType.AllScreens;
        public bool IsSingleScreen => Type == CaptureSourceType.Screen;
    }

    public class RemoteSourceItemDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public string Category { get; set; } = "screen"; // "screen" | "all" | "window"
    }

    public class RemoteMonitorCatalogDto
    {
        public string ActiveSourceId { get; set; } = string.Empty;
        public string ActiveSourceName { get; set; } = string.Empty;
        public System.Collections.Generic.List<RemoteSourceItemDto> Sources { get; set; } = new();
    }
}
