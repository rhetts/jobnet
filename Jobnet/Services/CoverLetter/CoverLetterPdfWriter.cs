using System;
using System.IO;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Jobnet.Services.CoverLetter;

public sealed class CoverLetterPdfWriter : ICoverLetterPdfWriter
{
    private static int _licenseSet;

    public CoverLetterPdfWriter()
    {
        // QuestPDF requires a license type to be set before any document is rendered.
        // Community is free for individuals / small companies; do it once per process.
        if (System.Threading.Interlocked.Exchange(ref _licenseSet, 1) == 0)
            QuestPDF.Settings.License = LicenseType.Community;
    }

    public void Write(string path, string companyName, string jobTitle, string letterText)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var paragraphs = (letterText ?? "")
            .Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.None)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(0.9f, Unit.Inch);
                page.DefaultTextStyle(s => s.FontFamily("Calibri").FontSize(11).LineHeight(1.35f));

                page.Header().Column(col =>
                {
                    col.Item().Text($"{jobTitle} — {companyName}")
                        .FontSize(13).SemiBold();
                    col.Item().PaddingTop(2).Text(DateTime.Now.ToString("MMMM d, yyyy"))
                        .FontSize(10).FontColor(Colors.Grey.Darken1);
                });

                page.Content().PaddingTop(18).Column(col =>
                {
                    col.Spacing(10);
                    foreach (var p in paragraphs)
                        col.Item().Text(p);
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(9).FontColor(Colors.Grey.Medium));
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf(path);
    }
}
