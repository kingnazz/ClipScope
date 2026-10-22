using System.Text;
using ClipScope.Core;

namespace ClipScope.Core.Tests;

public sealed class PolicyTests
{
    private static SourceContext Source => new() { ProcessId = 10, ProcessName = "editor", ExecutablePath = @"C:\Apps\editor.exe", WindowHandle = 42, WindowTitle = "notes.txt" };
    [Theory]
    [InlineData("a\r\nb\rc\0", "a\nb\nc")]
    [InlineData("e\u0301", "é")]
    [InlineData("  value  ", "  value  ")]
    public void NormalizationPreservesMeaning(string text, string expected) => Assert.Equal(expected, ContentPolicy.NormalizeText(text));
    [Fact]
    public void HashNormalizesTextButRetainsRichRepresentations()
    {
        var plain = new ClipboardPayload { PlainText = "a\r\nb" };
        Assert.Equal(ContentPolicy.Hash(plain), ContentPolicy.Hash(plain with { PlainText = "a\nb" }));
        Assert.NotEqual(ContentPolicy.Hash(plain), ContentPolicy.Hash(plain with { Html = "<b>a</b>" }));
        Assert.NotEqual(ContentPolicy.Hash(plain), ContentPolicy.Hash(plain with { Rtf = "{\\rtf1 a}" }));
    }
    [Fact]
    public void HashIncludesImagesAndOrderedFileBoundaries()
    {
        Assert.NotEqual(ContentPolicy.Hash(new() { Dib = [1, 2] }), ContentPolicy.Hash(new() { Dib = [1, 3] }));
        Assert.NotEqual(ContentPolicy.Hash(new() { Files = ["ab", "c"] }), ContentPolicy.Hash(new() { Files = ["a", "bc"] }));
        Assert.NotEqual(ContentPolicy.Hash(new() { Files = ["a", "b"] }), ContentPolicy.Hash(new() { Files = ["b", "a"] }));
    }
    [Fact]
    public void RepeatedCopyOnlyCollapsesRecentSameWindowContext()
    {
        var first = new Clip { ContentHash = "hash", Source = Source, CreatedUtc = DateTimeOffset.UtcNow };
        var repeat = first with { Id = Guid.NewGuid(), CreatedUtc = first.CreatedUtc.AddSeconds(1) };
        Assert.True(ContentPolicy.IsDuplicate(first, repeat, TimeSpan.FromSeconds(2)));
        Assert.False(ContentPolicy.IsDuplicate(first, repeat with { CreatedUtc = first.CreatedUtc.AddSeconds(3) }, TimeSpan.FromSeconds(2)));
        Assert.False(ContentPolicy.IsDuplicate(first, repeat with { Source = Source with { ProcessId = 11 } }, TimeSpan.FromSeconds(2)));
        Assert.False(ContentPolicy.IsDuplicate(first, repeat with { Source = Source with { WindowTitle = "other.txt" } }, TimeSpan.FromSeconds(2)));
        Assert.False(ContentPolicy.IsDuplicate(first, repeat with { CreatedUtc = first.CreatedUtc.AddSeconds(-1) }, TimeSpan.FromSeconds(2)));
    }
    [Theory]
    [InlineData("EDITOR.EXE")]
    [InlineData("editor")]
    [InlineData("C:\\Apps\\EDITOR.exe")]
    public void ExclusionsMatchExplicitIdentity(string entry) => Assert.True(PrivacyPolicy.IsExcluded(Source, new() { ExcludedApplications = [entry] }));
    [Fact]
    public void PathExclusionDoesNotMatchDifferentExecutableWithSameName()
    {
        var settings = new AppSettings { ExcludedApplications = [@"C:\Apps\editor.exe"] };
        Assert.False(PrivacyPolicy.IsExcluded(Source with { ExecutablePath = @"C:\Other\editor.exe" }, settings));
        Assert.True(PrivacyPolicy.IsExcluded(new(), settings));
        Assert.True(PrivacyPolicy.IsExcluded(Source with { ExecutablePath = null }, settings));
    }
    [Fact]
    public void HtmlOffsetsUseUtf8Bytes()
    {
        const string header = "Version:1.0\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\n";
        const string prefix = "<html>é<body>";
        const string fragment = "<b>café</b> &amp; tea";
        var start = Encoding.UTF8.GetByteCount(header + prefix);
        var end = start + Encoding.UTF8.GetByteCount(fragment);
        var actualHeader = header.Replace("StartFragment:0000000000", $"StartFragment:{start:D10}")
            .Replace("EndFragment:0000000000", $"EndFragment:{end:D10}");
        Assert.Equal("café  & tea", ContentPolicy.HtmlText(actualHeader + prefix + fragment + "</body></html>"));
    }
    [Fact]
    public void MalformedHtmlDoesNotThrow() => Assert.Contains("text", ContentPolicy.HtmlText("StartFragment:-999\nEndFragment:9999999\n<b>text</b>"));
    [Fact]
    public void SettingsRejectUnsafeLimits()
    {
        Assert.Throws<ArgumentException>(() => (new AppSettings { MaxImageBytes = int.MaxValue }).Validate());
        Assert.Throws<ArgumentException>(() => (new AppSettings { Shortcut = new(0, 0) }).Validate());
        Assert.Throws<ArgumentException>(() => (new AppSettings { RetentionDays = 0 }).Validate());
    }
}
