using System;

namespace LanScreenShare.Core.Models
{
    public enum InputEventType : byte
    {
        MouseMove = 1,
        MouseDown = 2,
        MouseUp = 3,
        MouseWheel = 4,
        KeyDown = 5,
        KeyUp = 6,
        MouseMoveRelative = 7,
        ClipboardText = 8,
        SwitchMonitor = 9,
        MonitorInfo = 10,
        SelectSource = 11,
        SecureDesktopState = 12
    }

    public enum RemoteMouseButton : byte
    {
        None = 0,
        Left = 1,
        Right = 2,
        Middle = 3
    }

    public class RemoteInputMessage
    {
        public InputEventType Type { get; set; }
        public float NormalizedX { get; set; }
        public float NormalizedY { get; set; }
        public RemoteMouseButton Button { get; set; }
        public short WheelDelta { get; set; }
        public int KeyCode { get; set; }
        public string? ClipboardText { get; set; }
    }
}
