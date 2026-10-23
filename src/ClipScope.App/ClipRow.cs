using System.ComponentModel;
using ClipScope.App.Design;
using ClipScope.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClipScope.App;

// Presentation model for one history row. Everything the template needs is
// precomputed so the list stays cheap to scroll.
[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ClipRow : INotifyPropertyChanged
{
    private static readonly FontFamily MonoFamily = new(Ui.MonoFont), TextFamily = new(Ui.TextFont);
    private ImageSource? _icon, _thumbnail;
    private readonly string? _iconPath, _thumbnailPath;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ClipRow(ClipSummary clip, string? icon, string? thumbnail, DateTimeOffset now)
    {
        Clip = clip; _iconPath = icon; _thumbnailPath = thumbnail;
        App = ClipPresentation.AppName(clip.SourceApp);
        Context = ClipPresentation.WindowContext(clip.WindowTitle, App);
        Preview = clip.Kind == ClipKind.Image && ShowsThumbnail ? "" : ClipPresentation.PreviewLine(clip.Preview);
        var code = clip.Kind == ClipKind.Text && ClipPresentation.LooksLikeCode(clip.Preview);
        PreviewFont = code ? MonoFamily : TextFamily;
        PreviewFontSize = code ? 13 : 14;
        KindGlyph = clip.Kind switch
        {
            ClipKind.Files => Glyph.ForFile(ClipPresentation.CategorizeFile(clip.Preview.Split(" · ")[0])),
            ClipKind.Image => Glyph.Image,
            _ => ""
        };
        Refresh(now);
    }

    public ClipSummary Clip { get; }
    public string App { get; }
    public string Context { get; }
    public string Preview { get; }
    public string KindGlyph { get; }
    public FontFamily PreviewFont { get; }
    public double PreviewFontSize { get; }
    public string Time { get; private set; } = "";
    public string Timestamp { get; private set; } = "";

    // Images decode lazily, only for realized rows, at the size they are displayed.
    public ImageSource? IconSource => _icon ??= Load(_iconPath, 40);
    public ImageSource? ThumbnailSource => _thumbnail ??= ShowsThumbnail ? Load(_thumbnailPath, 480) : null;

    private bool ShowsThumbnail => Clip.Kind == ClipKind.Image && _thumbnailPath is not null;
    public Visibility FallbackIconVisibility => Visible(_iconPath is null);
    public Visibility ContextVisibility => Visible(Context.Length > 0);
    public Visibility PreviewVisibility => Visible(Preview.Length > 0);
    public Visibility KindGlyphVisibility => Visible(KindGlyph.Length > 0 && Preview.Length > 0);
    public Visibility ThumbnailVisibility => Visible(ShowsThumbnail);
    public Visibility PinVisibility => Visible(Clip.IsPinned);
    public Visibility SnapshotVisibility => Visible(Clip.HasContextScreenshot);

    public void Refresh(DateTimeOffset now)
    {
        Time = ClipPresentation.RelativeTime(Clip.CreatedUtc, now);
        Timestamp = ClipPresentation.Timestamp(Clip.CreatedUtc, now.Offset, now);
        PropertyChanged?.Invoke(this, new(nameof(Time)));
        PropertyChanged?.Invoke(this, new(nameof(Timestamp)));
    }

    // Read by the list item's automation peer, so screen readers hear a useful row summary.
    public override string ToString()
    {
        var kind = Clip.Kind switch { ClipKind.Image => "Image", ClipKind.Files => "Files", _ => "Text" };
        var content = Preview.Length > 0 ? $": {Preview}" : "";
        var extras = (Clip.IsPinned ? ", pinned" : "") + (Clip.HasContextScreenshot ? ", has source snapshot" : "");
        return $"{kind} from {App}, {Timestamp}{extras}{content}";
    }

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private static BitmapImage? Load(string? path, int decodeWidth)
    {
        if (path is null) return null;
        try { return new BitmapImage(new Uri(path)) { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical }; }
        catch (UriFormatException) { return null; }
    }

    public const string Template = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid ColumnSpacing="12">
            <Grid.ColumnDefinitions><ColumnDefinition Width="20"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
            <Grid Width="20" Height="20" VerticalAlignment="Top" Margin="0,0,0,0">
              <FontIcon Glyph="&#xE71D;" FontSize="16" Foreground="{ThemeResource TextFillColorTertiaryBrush}" Visibility="{Binding FallbackIconVisibility}"/>
              <Image Source="{Binding IconSource}" Width="20" Height="20" Stretch="Uniform"/>
            </Grid>
            <StackPanel Grid.Column="1" Spacing="3">
              <Grid ColumnSpacing="6" MinHeight="20">
                <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                <TextBlock Text="{Binding App}" Style="{StaticResource CsCaptionTextStyle}" FontWeight="SemiBold" MaxWidth="180" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                <TextBlock Grid.Column="1" Text="{Binding Context}" Visibility="{Binding ContextVisibility}" Style="{StaticResource CsTertiaryTextStyle}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                <StackPanel Grid.Column="2" Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
                  <FontIcon Glyph="&#xE7F4;" FontSize="12" Foreground="{ThemeResource TextFillColorTertiaryBrush}" Visibility="{Binding SnapshotVisibility}" ToolTipService.ToolTip="Has a source snapshot"/>
                  <FontIcon Glyph="&#xE718;" FontSize="12" Foreground="{ThemeResource AccentTextFillColorPrimaryBrush}" Visibility="{Binding PinVisibility}" ToolTipService.ToolTip="Pinned"/>
                  <TextBlock Text="{Binding Time}" Style="{StaticResource CsTertiaryTextStyle}" ToolTipService.ToolTip="{Binding Timestamp}"/>
                </StackPanel>
              </Grid>
              <Grid Visibility="{Binding PreviewVisibility}">
                <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <FontIcon Glyph="{Binding KindGlyph}" FontSize="14" Margin="0,3,6,0" VerticalAlignment="Top" Foreground="{ThemeResource TextFillColorSecondaryBrush}" Visibility="{Binding KindGlyphVisibility}"/>
                <TextBlock Grid.Column="1" Text="{Binding Preview}" FontFamily="{Binding PreviewFont}" FontSize="{Binding PreviewFontSize}" Style="{StaticResource CsBodyTextStyle}" MaxLines="2" TextWrapping="Wrap" TextTrimming="CharacterEllipsis"/>
              </Grid>
              <Border Visibility="{Binding ThumbnailVisibility}" Style="{StaticResource CsMediaFrameStyle}" HorizontalAlignment="Left" Margin="0,4,0,2">
                <Image Source="{Binding ThumbnailSource}" MaxHeight="96" MaxWidth="260" Stretch="Uniform"/>
              </Border>
            </StackPanel>
          </Grid>
        </DataTemplate>
        """;
}
