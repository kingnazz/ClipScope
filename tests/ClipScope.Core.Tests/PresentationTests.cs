using System.Globalization;
using ClipScope.Core;

namespace ClipScope.Core.Tests;

public sealed class PresentationTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 15, 0, 0, TimeSpan.FromHours(2));

    [Theory]
    [InlineData(10, "Now")]
    [InlineData(120, "2m")]
    [InlineData(59 * 60, "59m")]
    [InlineData(3 * 3600, "3h")]
    [InlineData(20 * 3600, "Yesterday")]
    [InlineData(3 * 86400, "Tue")]
    [InlineData(30 * 86400, "Sep 23")]
    [InlineData(400 * 86400, "Sep 18, 2025")]
    public void RelativeTimeIsCompactAndLocal(int secondsAgo, string expected)
        => Assert.Equal(expected, ClipPresentation.RelativeTime(Now.AddSeconds(-secondsAgo).ToUniversalTime(), Now, Culture));

    [Fact]
    public void TimestampUsesTheClipsOriginalOffset()
    {
        var created = new DateTimeOffset(2026, 10, 23, 12, 30, 0, TimeSpan.Zero);
        Assert.Equal("Today at 14:30", ClipPresentation.Timestamp(created, TimeSpan.FromHours(2), Now, Culture));
        Assert.Equal("Yesterday at 14:30", ClipPresentation.Timestamp(created.AddDays(-1), TimeSpan.FromHours(2), Now, Culture));
        Assert.Equal("Mon, Oct 19 at 14:30", ClipPresentation.Timestamp(created.AddDays(-4), TimeSpan.FromHours(2), Now, Culture));
        Assert.Equal("Oct 23, 2025 at 14:30", ClipPresentation.Timestamp(created.AddYears(-1), TimeSpan.FromHours(2), Now, Culture));
    }

    [Theory]
    [InlineData("Microsoft 365 admin center - Google Chrome", "Google Chrome", "Microsoft 365 admin center")]
    [InlineData("Microsoft 365 admin center - Google Chrome", "Chrome", "Microsoft 365 admin center")]
    [InlineData("notes.txt - Notepad", "Notepad", "notes.txt")]
    [InlineData("Inbox - me@example.com - Outlook", "Microsoft Outlook", "Inbox - me@example.com")]
    [InlineData("Windows PowerShell", "Windows Terminal", "Windows PowerShell")]
    [InlineData("Notepad", "Notepad", "")]
    [InlineData("", "Notepad", "")]
    [InlineData("A - B", "", "A - B")]
    public void WindowContextDropsTheTrailingApplicationName(string title, string app, string expected)
        => Assert.Equal(expected, ClipPresentation.WindowContext(title, app));

    [Theory]
    [InlineData("Get-MgUser -All | Where-Object { $_.AccountEnabled }")]
    [InlineData("git checkout -b feat/premium-ui")]
    [InlineData("$total = $items.Count")]
    [InlineData("var x = items.Where(i => i.Enabled).ToList();")]
    [InlineData("public void Run()\n{\n    Start();\n}")]
    [InlineData("SELECT id, name FROM users WHERE active = 1;")]
    [InlineData("{\"name\": \"ClipScope\", \"enabled\": true}")]
    public void CodeLikeTextIsDetected(string text) => Assert.True(ClipPresentation.LooksLikeCode(text));

    [Theory]
    [InlineData("Microsoft 365 Business Premium")]
    [InlineData("Thanks for the update. I'll review it tomorrow (probably before noon).")]
    [InlineData("Coca-Cola is on the shopping list")]
    [InlineData("admin.microsoft.com")]
    [InlineData("192.168.1.15")]
    [InlineData("Dear team,\nPlease find the agenda attached.\nThanks!")]
    [InlineData("   ")]
    public void ProseStaysProportional(string text) => Assert.False(ClipPresentation.LooksLikeCode(text));

    [Fact]
    public void PreviewLineCollapsesWhitespaceAndBoundsLength()
    {
        Assert.Equal("a b c", ClipPresentation.PreviewLine("  a\r\n\tb   c "));
        Assert.Equal("abc…", ClipPresentation.PreviewLine("abcdef", 3));
        Assert.Equal("", ClipPresentation.PreviewLine(null));
    }

    [Theory]
    [InlineData(@"C:\Docs\report.PDF", FileCategory.Pdf)]
    [InlineData(@"C:\Docs\budget.xlsx", FileCategory.Spreadsheet)]
    [InlineData(@"C:\Docs\photo.jpeg", FileCategory.Image)]
    [InlineData(@"C:\Docs\Projects", FileCategory.Folder)]
    [InlineData(@"C:\Docs\script.ps1", FileCategory.Code)]
    [InlineData(@"C:\Docs\data.bin", FileCategory.Other)]
    public void FilesAreCategorizedByExtension(string path, FileCategory expected) => Assert.Equal(expected, ClipPresentation.CategorizeFile(path));

    [Fact]
    public void ShortcutKeysFollowWindowsModifierOrder()
    {
        Assert.Equal(["Ctrl", "Shift", "V"], ClipPresentation.ShortcutKeys(new Hotkey()));
        Assert.Equal(["Alt", "Win", "1"], ClipPresentation.ShortcutKeys(new Hotkey(1 | 8, '1')));
        Assert.Equal("Unknown application", ClipPresentation.AppName("", " "));
        Assert.Equal("notepad", ClipPresentation.AppName(null, "notepad"));
        Assert.Equal("1,234 characters · 2 lines", ClipPresentation.TextSummary(new string('a', 1233) + "\n", Culture));
    }
}
