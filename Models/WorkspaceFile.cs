using System.Collections.Generic;

namespace libvlc_media.Models
{
    public enum UnsavedDecision
    {
        Cancel,
        Discard,
        Save
    }

    public sealed class WorkspaceDocument
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; } = string.Empty;
        public bool CopyVideos { get; set; }
        public List<WorkspaceDeckFile> Decks { get; set; } = [];
        public List<WorkspaceLibraryFile> Library { get; set; } = [];
    }

    public sealed class WorkspaceDeckFile
    {
        public string Name { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
        public string? SourcePdf { get; set; }
        public bool AutoAdvance { get; set; }
        public bool LoopSlides { get; set; } = true;
        public decimal AutoAdvanceSeconds { get; set; } = 8;
        public List<WorkspaceSlideFile> Slides { get; set; } = [];
    }

    public sealed class WorkspaceSlideFile
    {
        public string Type { get; set; } = "image";
        public string? File { get; set; }
        public string? Path { get; set; }
        public string? OriginalPath { get; set; }
        public string? Label { get; set; }
    }

    public sealed class WorkspaceLibraryFile
    {
        public string Path { get; set; } = string.Empty;
    }

    public sealed class WorkspaceSaveRequest
    {
        public string Name { get; set; } = string.Empty;
        public bool CopyVideos { get; set; }
        public List<WorkspaceDeckSaveRequest> Decks { get; set; } = [];
        public List<string> LibraryPaths { get; set; } = [];
    }

    public sealed class WorkspaceDeckSaveRequest
    {
        public string Name { get; set; } = string.Empty;
        public string? SourcePdf { get; set; }
        public bool AutoAdvance { get; set; }
        public bool LoopSlides { get; set; } = true;
        public decimal AutoAdvanceSeconds { get; set; } = 8;
        public List<WorkspaceSlideSaveRequest> Slides { get; set; } = [];
    }

    public sealed class WorkspaceSlideSaveRequest
    {
        public bool IsVideo { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public string? Label { get; set; }
    }

    public sealed class SavedWorkspace
    {
        public string Folder { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public List<SavedWorkspaceDeck> Decks { get; init; } = [];
        public List<string> LibraryPaths { get; init; } = [];
    }

    public sealed class SavedWorkspaceDeck
    {
        public string Directory { get; init; } = string.Empty;
        public List<string> SlidePaths { get; init; } = [];
    }

    public sealed class LoadedWorkspace
    {
        public string Folder { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool CopyVideos { get; init; }
        public List<LoadedWorkspaceDeck> Decks { get; init; } = [];
        public List<string> LibraryPaths { get; init; } = [];
        public List<string> Warnings { get; init; } = [];
    }

    public sealed class LoadedWorkspaceDeck
    {
        public string Name { get; init; } = string.Empty;
        public string Directory { get; init; } = string.Empty;
        public string? SourcePdf { get; init; }
        public bool AutoAdvance { get; init; }
        public bool LoopSlides { get; init; } = true;
        public decimal AutoAdvanceSeconds { get; init; } = 8;
        public List<LoadedWorkspaceSlide> Slides { get; init; } = [];
    }

    public sealed class LoadedWorkspaceSlide
    {
        public bool IsVideo { get; init; }
        public string Path { get; init; } = string.Empty;
        public string? Label { get; init; }
        public bool Missing { get; init; }
    }
}
