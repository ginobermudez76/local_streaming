namespace LanScreenShare.Core.Models
{
    public readonly record struct CaptureProfile
    {
        public int TargetFps { get; init; } = 30;
        public int Quality { get; init; } = 70;
        public double ResolutionScale { get; init; } = 1.0;
        public string Codec { get; init; } = "jpeg";
        public int TargetWidth { get; init; } = 0;
        public int TargetHeight { get; init; } = 0;
        public int BitrateMbps { get; init; } = 8;

        public int Fps => TargetFps;
        public double Scale => ResolutionScale;

        public CaptureProfile()
        {
        }
    }
}
