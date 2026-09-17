using System;
using System.IO;

namespace libvlc_media.Models
{
    public static class SlideCache
    {
        public static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "libvlc-media",
            "decks");

        public static string NewDeckFolder()
        {
            var directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        public static void DeleteFolder(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return;

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Cache cleanup is best-effort.
            }
        }

        public static void ClearAll()
        {
            DeleteFolder(Root);
        }
    }
}
