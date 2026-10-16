using System.Collections.Generic;

namespace libvlc_media.Models
{
    public sealed class RemoteSnapshot
    {
        public string Type { get; set; } = "state";
        public int Revision { get; set; }
        public bool Blanked { get; set; }
        public int SelectedDeckIndex { get; set; } = -1;
        public int SelectedSlideIndex { get; set; } = -1;
        public int LiveDeckIndex { get; set; } = -1;
        public int LiveSlideIndex { get; set; } = -1;
        public string OutputCaption { get; set; } = "No output";
        public string BlankLabel { get; set; } = "Blank";
        public List<RemoteDeckDto> Decks { get; set; } = [];
    }

    public sealed class RemoteDeckDto
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<RemoteSlideDto> Slides { get; set; } = [];
    }

    public sealed class RemoteSlideDto
    {
        public int Index { get; set; }
        public string Label { get; set; } = string.Empty;
        public bool IsVideo { get; set; }
        public bool HasThumb { get; set; }
        public string Thumb { get; set; } = string.Empty;
    }

    public sealed class RemoteCommand
    {
        public string Type { get; set; } = string.Empty;
        public int DeckIndex { get; set; }
        public int SlideIndex { get; set; }
    }
}
