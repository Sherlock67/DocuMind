using System.Text;
using DocuMind.Application.Abstractions.Ingestion;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DocuMind.Infrastructure.Ingestion;

internal sealed class PdfTextExtractor : ITextExtractor
{
    public bool CanExtract(string extension) => extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken ct)
    {
        // PdfPig needs a seekable stream.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        using var pdf = PdfDocument.Open(buffer);
        var pages = new List<ExtractedPage>(pdf.NumberOfPages);
        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            pages.Add(new ExtractedPage(page.Number, ContentOrderTextExtractor.GetText(page)));
        }

        return new ExtractedDocument(pages);
    }
}

internal sealed class DocxTextExtractor : ITextExtractor
{
    public bool CanExtract(string extension) => extension.Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public async Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        using var doc = WordprocessingDocument.Open(buffer, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
            return new ExtractedDocument([]);

        // Paragraph per line, blank line between paragraphs, so the chunker can split on structure.
        var text = new StringBuilder();
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            var line = paragraph.InnerText;
            if (!string.IsNullOrWhiteSpace(line))
                text.AppendLine(line).AppendLine();
        }

        // .docx has no stable page boundaries, so the whole file is one unpaged section.
        return new ExtractedDocument([new ExtractedPage(null, text.ToString())]);
    }
}

internal sealed class PlainTextExtractor : ITextExtractor
{
    public bool CanExtract(string extension) =>
        extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".md", StringComparison.OrdinalIgnoreCase);

    public async Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken ct)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return new ExtractedDocument([new ExtractedPage(null, await reader.ReadToEndAsync(ct))]);
    }
}
