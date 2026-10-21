using DocuMind.Application.Abstractions.Ingestion;

namespace DocuMind.UnitTests.Documents;

public class SupportedFileTypesTests
{
    [Theory]
    [InlineData("a.pdf", "application/pdf", true)]
    [InlineData("a.PDF", "application/pdf", true)]
    [InlineData("a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", false)]
    [InlineData("a.txt", "text/plain; charset=utf-8", true)]
    [InlineData("a.md", "text/markdown; charset=utf-8", true)]
    public void Supported_types_map_to_a_fixed_content_type(string name, string contentType, bool inline)
    {
        Assert.True(SupportedFileTypes.IsSupported(name));
        Assert.Equal(contentType, SupportedFileTypes.ContentTypeFor(name));
        Assert.Equal(inline, SupportedFileTypes.CanPreviewInline(name));
    }

    [Theory]
    [InlineData("page.html")]
    [InlineData("image.svg")]
    [InlineData("noextension")]
    public void Anything_else_is_unsupported_and_never_served_as_renderable(string name)
    {
        Assert.False(SupportedFileTypes.IsSupported(name));
        Assert.Equal("application/octet-stream", SupportedFileTypes.ContentTypeFor(name));
    }
}
