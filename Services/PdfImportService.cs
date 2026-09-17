using PDFtoImage;
using System;
using System.Collections.Generic;
using System.IO;

namespace libvlc_media.Services
{
    public sealed class PdfImportService
    {
        private static readonly RenderOptions RenderOptions = new(Dpi: 144);

        public IReadOnlyList<string> ImportDeck(
            string pdfPath,
            string outputDirectory,
            IProgress<(int Current, int Total)>? progress = null)
        {
            if (!File.Exists(pdfPath))
                throw new FileNotFoundException("PDF file was not found.", pdfPath);

            Directory.CreateDirectory(outputDirectory);

            using var stream = File.OpenRead(pdfPath);
            var pageCount = Conversion.GetPageCount(stream, leaveOpen: true);
            if (pageCount <= 0)
                throw new InvalidOperationException("This PDF has no pages to import.");

            var paths = new List<string>(pageCount);
            for (var i = 0; i < pageCount; i++)
            {
                stream.Position = 0;
                var file = Path.Combine(outputDirectory, $"slide-{i + 1:000}.png");
                Conversion.SavePng(
                    file,
                    stream,
                    page: i,
                    leaveOpen: true,
                    password: null,
                    options: RenderOptions);
                paths.Add(file);
                progress?.Report((i + 1, pageCount));
            }

            return paths;
        }
    }
}
