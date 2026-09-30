using System.Text;
using Portal.Api.Documents;

namespace Portal.Tests;

/// <summary>Pure logic, so these run without a database.</summary>
public sealed class FileTypePolicyTests
{
    private static string? Validate(string name, byte[] content) => FileTypePolicy.Validate(name, content, content.Length);

    [Theory]
    [InlineData("brief.pdf", "%PDF-1.7\n", "application/pdf")]
    [InlineData("logo.PNG", "\u0089PNG\r\n\u001a\n", "image/png")]
    [InlineData("photo.jpg", "\u00FF\u00D8\u00FF\u00E0", "image/jpeg")]
    [InlineData("anim.gif", "GIF89a", "image/gif")]
    [InlineData("notes.txt", "plain text é", "text/plain; charset=utf-8")]
    [InlineData("data.csv", "a,b\n1,2\n", "text/csv; charset=utf-8")]
    public void Accepts_files_whose_contents_match_their_extension(string name, string content, string expected)
    {
        var bytes = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            ? content.Select(c => (byte)c).ToArray()
            : Encoding.UTF8.GetBytes(content);

        Assert.Equal(expected, Validate(name, bytes));
    }

    [Fact]
    public void Accepts_zip_based_office_files()
    {
        var zip = "PK\u0003\u0004rest"u8.ToArray();

        Assert.NotNull(Validate("deck.pptx", zip));
        Assert.NotNull(Validate("sheet.xlsx", zip));
        Assert.NotNull(Validate("doc.docx", zip));
        Assert.NotNull(Validate("bundle.zip", zip));
    }

    [Fact]
    public void Accepts_webp()
    {
        var webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();

        Assert.Equal("image/webp", Validate("hero.webp", webp));
    }

    [Theory]
    [InlineData("page.html")]
    [InlineData("vector.svg")]
    [InlineData("run.exe")]
    [InlineData("script.js")]
    [InlineData("archive.tar.gz")]
    [InlineData("noextension")]
    public void Refuses_types_that_are_not_on_the_allow_list(string name)
    {
        Assert.Null(Validate(name, "<html><script>alert(1)</script></html>"u8.ToArray()));
    }

    [Fact]
    public void Refuses_a_renamed_file_whose_contents_do_not_match()
    {
        Assert.Null(Validate("invoice.pdf", "MZ\u0090\u0000 this is an executable"u8.ToArray()));
        Assert.Null(Validate("photo.png", "%PDF-1.4"u8.ToArray()));
        Assert.Null(Validate("notes.txt", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]));
    }

    [Fact]
    public void Refuses_text_that_is_not_valid_utf8()
    {
        Assert.Null(Validate("notes.txt", [0xC3, 0x28, 0x41]));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system32\\cmd.exe", "cmd.exe")]
    [InlineData("C:\\Users\\me\\report.pdf", "report.pdf")]
    [InlineData("  report .pdf  ", "report .pdf")]
    [InlineData("bad\"name<>|:*?.pdf", "badname.pdf")]
    [InlineData("tab\tand\nnewline.txt", "tabandnewline.txt")]
    public void Sanitizes_file_names(string raw, string expected)
    {
        Assert.Equal(expected, FileTypePolicy.SanitizeFileName(raw));
    }

    [Fact]
    public void Bounds_the_file_name_length_and_keeps_the_extension()
    {
        var name = FileTypePolicy.SanitizeFileName(new string('a', 500) + ".pdf");

        Assert.Equal(200, name.Length);
        Assert.EndsWith(".pdf", name, StringComparison.Ordinal);
    }
}
