namespace LanScreenShare.Core.Models
{
    public readonly record struct CaptureProfile(
        int TargetFps = 30,
        int Quality = 70,
        double ResolutionScale = 1.0,
        string Codec = "jpeg",
        int TargetWidth = 0,
        int TargetHeight = 0,
        int BitrateMbps = 8);
}
