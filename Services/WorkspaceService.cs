using libvlc_media.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace libvlc_media.Services
{
    public sealed class WorkspaceService
    {
        public const string JsonFileName = "workspace.json";
        public const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "con", "prn", "aux", "nul",
            "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
            "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
            "workspace", "media", "decks"
        };

        public static string ResolveFolderFromSavePath(string pickedPath)
        {
            if (Directory.Exists(pickedPath))
                return pickedPath;

            var fileName = Path.GetFileName(pickedPath);
            var directory = Path.GetDirectoryName(pickedPath) ?? Environment.CurrentDirectory;
            if (fileName.Equals(JsonFileName, StringComparison.OrdinalIgnoreCase))
                return directory;

            return Path.Combine(directory, Path.GetFileNameWithoutExtension(pickedPath));
        }

        public static string? FindWorkspaceFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (File.Exists(path)
                && Path.GetFileName(path).Equals(JsonFileName, StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(path);

            if (Directory.Exists(path) && File.Exists(Path.Combine(path, JsonFileName)))
                return path;

            return null;
        }

        public async Task<SavedWorkspace> SaveAsync(
            string folder,
            WorkspaceSaveRequest request,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(folder);
            ArgumentNullException.ThrowIfNull(request);

            var name = string.IsNullOrWhiteSpace(request.Name)
                ? Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : request.Name.Trim();

            Directory.CreateDirectory(folder);
            var mediaDir = Path.Combine(folder, "media");
            var decksRoot = Path.Combine(folder, "decks");
            Directory.CreateDirectory(mediaDir);
            Directory.CreateDirectory(decksRoot);

            var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var videoCopies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var document = new WorkspaceDocument
            {
                Version = CurrentVersion,
                Name = name,
                CopyVideos = request.CopyVideos
            };

            var savedDecks = new List<SavedWorkspaceDeck>();
            var imagePad = request.Decks.Select(d => d.Slides.Count(s => !s.IsVideo)).DefaultIfEmpty(0).Max() >= 100
                ? 3
                : 2;

            foreach (var deck in request.Decks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folderName = UniqueName(SanitizeFolderName(deck.Name), usedFolders);
                var deckDir = Path.Combine(decksRoot, folderName);
                Directory.CreateDirectory(deckDir);
                progress?.Report($"Saving {deck.Name}…");

                var writtenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var slidePaths = new List<string>(deck.Slides.Count);
                var slideFiles = new List<WorkspaceSlideFile>(deck.Slides.Count);
                var imageIndex = 0;

                foreach (var slide in deck.Slides)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (slide.IsVideo)
                    {
                        var (absolute, relative, original) = await CopyOrLinkVideoAsync(
                            slide.SourcePath,
                            folder,
                            mediaDir,
                            request.CopyVideos,
                            videoCopies,
                            progress,
                            cancellationToken);
                        slidePaths.Add(absolute);
                        slideFiles.Add(new WorkspaceSlideFile
                        {
                            Type = "video",
                            Path = relative,
                            OriginalPath = original,
                            Label = slide.Label
                        });
                    }
                    else
                    {
                        imageIndex++;
                        var fileName = imageIndex.ToString(new string('0', imagePad)) + ".png";
                        var dest = Path.Combine(deckDir, fileName);
                        progress?.Report($"Saving {deck.Name} — {fileName}");
                        CopyFile(slide.SourcePath, dest);
                        writtenImages.Add(fileName);
                        slidePaths.Add(dest);
                        slideFiles.Add(new WorkspaceSlideFile
                        {
                            Type = "image",
                            File = fileName,
                            Label = slide.Label
                        });
                    }
                }

                foreach (var leftover in Directory.GetFiles(deckDir, "*.png"))
                {
                    if (!writtenImages.Contains(Path.GetFileName(leftover)))
                        TryDelete(leftover);
                }

                document.Decks.Add(new WorkspaceDeckFile
                {
                    Name = deck.Name,
                    Folder = folderName,
                    SourcePdf = deck.SourcePdf,
                    AutoAdvance = deck.AutoAdvance,
                    LoopSlides = deck.LoopSlides,
                    AutoAdvanceSeconds = deck.AutoAdvanceSeconds,
                    Slides = slideFiles
                });

                savedDecks.Add(new SavedWorkspaceDeck
                {
                    Directory = deckDir,
                    SlidePaths = slidePaths
                });
            }

            var keepDeckFolders = new HashSet<string>(
                document.Decks.Select(d => d.Folder),
                StringComparer.OrdinalIgnoreCase);
            foreach (var leftover in Directory.GetDirectories(decksRoot))
            {
                if (!keepDeckFolders.Contains(Path.GetFileName(leftover)))
                    TryDeleteDirectory(leftover);
            }

            var libraryPaths = new List<string>();
            foreach (var libraryPath in request.LibraryPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(libraryPath))
                    continue;

                if (MediaLooksLikeVideo(libraryPath) && request.CopyVideos)
                {
                    var (absolute, relative, _) = await CopyOrLinkVideoAsync(
                        libraryPath,
                        folder,
                        mediaDir,
                        copy: true,
                        videoCopies,
                        progress,
                        cancellationToken);
                    libraryPaths.Add(absolute);
                    document.Library.Add(new WorkspaceLibraryFile { Path = relative });
                }
                else
                {
                    libraryPaths.Add(libraryPath);
                    document.Library.Add(new WorkspaceLibraryFile
                    {
                        Path = ToStoredPath(libraryPath, folder)
                    });
                }
            }

            if (request.CopyVideos)
            {
                var keepMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rel in videoCopies.Values)
                    keepMedia.Add(Path.GetFullPath(Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar))));

                if (Directory.Exists(mediaDir))
                {
                    foreach (var leftover in Directory.GetFiles(mediaDir))
                    {
                        if (!keepMedia.Contains(Path.GetFullPath(leftover)))
                            TryDelete(leftover);
                    }
                }
            }

            progress?.Report("Writing workspace.json…");
            var jsonPath = Path.Combine(folder, JsonFileName);
            var tempJson = jsonPath + ".tmp";
            var json = JsonSerializer.Serialize(document, JsonOptions);
            await File.WriteAllTextAsync(tempJson, json, cancellationToken);
            File.Copy(tempJson, jsonPath, overwrite: true);
            TryDelete(tempJson);

            return new SavedWorkspace
            {
                Folder = folder,
                Name = name,
                Decks = savedDecks,
                LibraryPaths = libraryPaths
            };
        }

        public LoadedWorkspace Load(string folder)
        {
            var workspaceFolder = FindWorkspaceFolder(folder)
                                  ?? throw new FileNotFoundException("This folder is not a slide workspace.", folder);
            var jsonPath = Path.Combine(workspaceFolder, JsonFileName);
            var json = File.ReadAllText(jsonPath);
            var document = JsonSerializer.Deserialize<WorkspaceDocument>(json, JsonOptions)
                           ?? throw new InvalidDataException("The workspace file is empty.");

            if (document.Version > CurrentVersion)
                throw new InvalidDataException($"This workspace was saved by a newer version of Slide Presenter (v{document.Version}).");

            var warnings = new List<string>();
            var decks = new List<LoadedWorkspaceDeck>();
            var name = string.IsNullOrWhiteSpace(document.Name)
                ? Path.GetFileName(workspaceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : document.Name;

            foreach (var deck in document.Decks)
            {
                var deckDir = Path.Combine(workspaceFolder, "decks", deck.Folder);
                var slides = new List<LoadedWorkspaceSlide>();
                foreach (var slide in deck.Slides)
                {
                    if (string.Equals(slide.Type, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        var stored = slide.Path ?? string.Empty;
                        var absolute = ToAbsolutePath(stored, workspaceFolder);
                        var missing = string.IsNullOrWhiteSpace(absolute) || !File.Exists(absolute);
                        if (missing)
                        {
                            var label = Path.GetFileName(string.IsNullOrWhiteSpace(stored) ? slide.OriginalPath : stored);
                            warnings.Add($"Missing video: {label}");
                            slides.Add(new LoadedWorkspaceSlide
                            {
                                IsVideo = true,
                                Path = string.IsNullOrWhiteSpace(absolute) ? stored : absolute,
                                Label = string.IsNullOrWhiteSpace(slide.Label) ? label : slide.Label,
                                Missing = true
                            });
                        }
                        else
                        {
                            slides.Add(new LoadedWorkspaceSlide
                            {
                                IsVideo = true,
                                Path = absolute,
                                Label = slide.Label,
                                Missing = false
                            });
                        }
                    }
                    else
                    {
                        var fileName = slide.File ?? string.Empty;
                        var absolute = Path.Combine(deckDir, fileName);
                        if (!File.Exists(absolute))
                        {
                            warnings.Add($"Missing slide image in {deck.Name}: {fileName}");
                            continue;
                        }

                        slides.Add(new LoadedWorkspaceSlide
                        {
                            IsVideo = false,
                            Path = absolute,
                            Label = slide.Label,
                            Missing = false
                        });
                    }
                }

                decks.Add(new LoadedWorkspaceDeck
                {
                    Name = string.IsNullOrWhiteSpace(deck.Name) ? deck.Folder : deck.Name,
                    Directory = deckDir,
                    SourcePdf = deck.SourcePdf,
                    AutoAdvance = deck.AutoAdvance,
                    LoopSlides = deck.LoopSlides,
                    AutoAdvanceSeconds = deck.AutoAdvanceSeconds <= 0 ? 8 : deck.AutoAdvanceSeconds,
                    Slides = slides
                });
            }

            var library = new List<string>();
            foreach (var item in document.Library)
            {
                var absolute = ToAbsolutePath(item.Path, workspaceFolder);
                if (string.IsNullOrWhiteSpace(absolute))
                    continue;
                if (!File.Exists(absolute))
                {
                    warnings.Add($"Missing media: {Path.GetFileName(item.Path)}");
                    continue;
                }

                library.Add(absolute);
            }

            return new LoadedWorkspace
            {
                Folder = workspaceFolder,
                Name = name,
                CopyVideos = document.CopyVideos,
                Decks = decks,
                LibraryPaths = library,
                Warnings = warnings
            };
        }

        public static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "deck";

            var invalid = Path.GetInvalidFileNameChars();
            var buffer = new StringBuilder(name.Length);
            foreach (var ch in name.Trim())
            {
                if (ch < 32 || invalid.Contains(ch) || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                    buffer.Append('-');
                else
                    buffer.Append(char.ToLowerInvariant(ch));
            }

            var sanitized = Regex.Replace(buffer.ToString(), @"[-\s]+", "-").Trim('-', '.', ' ');
            if (string.IsNullOrEmpty(sanitized))
                sanitized = "deck";
            if (sanitized.Length > 40)
                sanitized = sanitized[..40].Trim('-', '.');
            if (ReservedNames.Contains(sanitized))
                sanitized = "deck-" + sanitized;
            return sanitized;
        }

        private static string UniqueName(string baseName, HashSet<string> used)
        {
            var name = baseName;
            var n = 2;
            while (!used.Add(name))
                name = $"{baseName}-{n++}";
            return name;
        }

        private static async Task<(string Absolute, string Stored, string Original)> CopyOrLinkVideoAsync(
            string sourcePath,
            string workspaceFolder,
            string mediaDir,
            bool copy,
            Dictionary<string, string> copies,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            var original = sourcePath;
            if (!copy)
                return (sourcePath, ToStoredPath(sourcePath, workspaceFolder), original);

            if (copies.TryGetValue(sourcePath, out var existing))
            {
                var absoluteExisting = Path.GetFullPath(Path.Combine(workspaceFolder, existing.Replace('/', Path.DirectorySeparatorChar)));
                return (absoluteExisting, existing, original);
            }

            if (!File.Exists(sourcePath))
                return (sourcePath, ToStoredPath(sourcePath, workspaceFolder), original);

            Directory.CreateDirectory(mediaDir);
            var destName = Path.GetFileName(sourcePath);
            var dest = Path.Combine(mediaDir, destName);
            var destFull = Path.GetFullPath(dest);
            var sourceFull = Path.GetFullPath(sourcePath);
            var relative = "media/" + destName;

            if (!destFull.Equals(sourceFull, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(destFull) && copies.ContainsValue(relative))
                {
                    destName = UniqueFileName(mediaDir, destName);
                    dest = Path.Combine(mediaDir, destName);
                    destFull = Path.GetFullPath(dest);
                    relative = "media/" + destName;
                }

                progress?.Report($"Copying {destName}…");
                await CopyFileAsync(sourceFull, destFull, cancellationToken);
            }

            copies[sourcePath] = relative;
            return (destFull, relative, original);
        }

        private static string UniqueFileName(string directory, string fileName)
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var dest = Path.Combine(directory, fileName);
            var n = 2;
            while (File.Exists(dest))
            {
                dest = Path.Combine(directory, $"{name}-{n++}{ext}");
            }

            return Path.GetFileName(dest);
        }

        private static string ToStoredPath(string path, string workspaceFolder)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(workspaceFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full[root.Length..].Replace('\\', '/');
            return full;
        }

        private static string ToAbsolutePath(string stored, string workspaceFolder)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return string.Empty;
            if (Path.IsPathRooted(stored))
                return Path.GetFullPath(stored);
            return Path.GetFullPath(Path.Combine(workspaceFolder, stored.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static bool MediaLooksLikeVideo(string path) =>
            Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

        private static void CopyFile(string source, string destination)
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                throw new FileNotFoundException("Slide image was not found.", source);

            var srcFull = Path.GetFullPath(source);
            var destFull = Path.GetFullPath(destination);
            if (srcFull.Equals(destFull, StringComparison.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
            File.Copy(srcFull, destFull, overwrite: true);
        }

        private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
        {
            var srcFull = Path.GetFullPath(source);
            var destFull = Path.GetFullPath(destination);
            if (srcFull.Equals(destFull, StringComparison.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
            await using var input = File.OpenRead(srcFull);
            await using var output = new FileStream(destFull, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 256, useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
