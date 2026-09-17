namespace libvlc_media.Models
{
    public sealed class VideoRendererOption
    {
        public VideoRendererOption(string name, string? module)
        {
            Name = name;
            Module = module;
        }

        public string Name { get; }

        public string? Module { get; }

        public override string ToString() => Name;
    }
}
