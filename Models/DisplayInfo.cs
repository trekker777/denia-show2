namespace libvlc_media.Models
{
    public sealed class DisplayInfo
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double Scaling { get; init; } = 1;
        public bool IsPrimary { get; init; }
    }
}
