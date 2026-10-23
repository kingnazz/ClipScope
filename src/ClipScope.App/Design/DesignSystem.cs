using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace ClipScope.App.Design;

// ClipScope's small visual system. Colors always come from Windows semantic theme
// resources through the styles below, so light, dark and high-contrast themes,
// accent color and per-window theme overrides are honored without hard-coded values.
internal static class Ui
{
    // Spacing scale. Use these instead of ad-hoc values.
    public const double Space1 = 4, Space2 = 8, Space3 = 12, Space4 = 16, Space5 = 24, Space6 = 32;
    public const double SidebarWidth = 208, CompactSidebarWidth = 60;
    public const double CompactWidthThreshold = 1020;
    public const string MonoFont = "Cascadia Mono, Consolas";
    public const string TextFont = "Segoe UI Variable Text, Segoe UI";

    public static Thickness Pad(double uniform) => new(uniform);
    public static Thickness Pad(double horizontal, double vertical) => new(horizontal, vertical, horizontal, vertical);

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static TextBlock Text(string style, string text = "", bool wrap = false)
        => new() { Style = Style(style), Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis };

    public static FontIcon Icon(string glyph, double size = 16, string? style = null)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = size };
        if (style is not null) icon.Style = Style(style);
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return icon;
    }

    public static Border Divider(bool vertical = false)
    {
        var divider = new Border { Style = Style("CsDividerStyle") };
        if (vertical) { divider.Width = 1; divider.Height = double.NaN; divider.VerticalAlignment = VerticalAlignment.Stretch; }
        return divider;
    }

    // A keycap such as [Ctrl]. Decorative: screen readers get the hint text from its owner.
    public static Border Keycap(string key)
    {
        var cap = new Border { Style = Style("CsKeycapStyle"), Child = new TextBlock { Style = Style("CsKeycapTextStyle"), Text = key } };
        AutomationProperties.SetAccessibilityView(cap, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return cap;
    }

    public static StackPanel Keycaps(IEnumerable<string> keys, double spacing = 3)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var key in keys) row.Children.Add(Keycap(key));
        return row;
    }

    // "[↵] Paste" style hint used in the footer.
    public static StackPanel KeyHint(string label, string description, params string[] keys)
    {
        var hint = Keycaps(keys);
        hint.Spacing = 3;
        var text = new TextBlock { Style = Style("CsTertiaryTextStyle"), Text = label, Margin = new Thickness(Space1, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        // The label's accessible name is the full description, never a bare action word.
        AutomationProperties.SetName(text, description);
        hint.Children.Add(text);
        return hint;
    }

    public static Button SubtleIconButton(string glyph, string name, string tooltip, double iconSize = 16)
    {
        var button = new Button { Style = Style("SubtleButtonStyle"), Content = Icon(glyph, iconSize), Padding = Pad(Space2), MinWidth = 36, MinHeight = 36 };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    public static StackPanel IconLabel(string glyph, string label, double spacing = Space2)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        row.Children.Add(Icon(glyph, 14));
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    public static ResourceDictionary LoadResources() => (ResourceDictionary)XamlReader.Load(Resources);

    // Styles reference only built-in Windows theme resources (Fluent semantic brushes).
    private const string Resources = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <x:Double x:Key="CsSearchHeight">36</x:Double>

          <Style x:Key="CsCaptionTextStyle" TargetType="TextBlock" BasedOn="{StaticResource CaptionTextBlockStyle}">
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}"/>
          </Style>
          <Style x:Key="CsTertiaryTextStyle" TargetType="TextBlock" BasedOn="{StaticResource CaptionTextBlockStyle}">
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorTertiaryBrush}"/>
          </Style>
          <Style x:Key="CsErrorTextStyle" TargetType="TextBlock" BasedOn="{StaticResource CaptionTextBlockStyle}">
            <Setter Property="Foreground" Value="{ThemeResource SystemFillColorCriticalBrush}"/>
          </Style>
          <Style x:Key="CsBodyTextStyle" TargetType="TextBlock" BasedOn="{StaticResource BodyTextBlockStyle}"/>
          <Style x:Key="CsBodySecondaryTextStyle" TargetType="TextBlock" BasedOn="{StaticResource BodyTextBlockStyle}">
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}"/>
          </Style>
          <Style x:Key="CsBodyStrongTextStyle" TargetType="TextBlock" BasedOn="{StaticResource BodyStrongTextBlockStyle}"/>
          <Style x:Key="CsSectionLabelStyle" TargetType="TextBlock" BasedOn="{StaticResource CaptionTextBlockStyle}">
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}"/>
          </Style>
          <Style x:Key="CsHeaderTextStyle" TargetType="TextBlock" BasedOn="{StaticResource SubtitleTextBlockStyle}">
            <Setter Property="FontSize" Value="18"/>
            <Setter Property="LineHeight" Value="24"/>
          </Style>
          <Style x:Key="CsPageTitleTextStyle" TargetType="TextBlock" BasedOn="{StaticResource TitleTextBlockStyle}"/>
          <Style x:Key="CsEmptyTitleTextStyle" TargetType="TextBlock" BasedOn="{StaticResource BodyStrongTextBlockStyle}">
            <Setter Property="FontSize" Value="16"/>
            <Setter Property="TextAlignment" Value="Center"/>
            <Setter Property="HorizontalAlignment" Value="Center"/>
          </Style>
          <Style x:Key="CsEmptyBodyTextStyle" TargetType="TextBlock" BasedOn="{StaticResource BodyTextBlockStyle}">
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}"/>
            <Setter Property="TextAlignment" Value="Center"/>
            <Setter Property="HorizontalAlignment" Value="Center"/>
            <Setter Property="TextWrapping" Value="Wrap"/>
            <Setter Property="MaxWidth" Value="320"/>
          </Style>
          <Style x:Key="CsPreviewTextStyle" TargetType="TextBlock" BasedOn="{StaticResource BodyTextBlockStyle}">
            <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI"/>
            <Setter Property="FontSize" Value="15"/>
            <Setter Property="LineHeight" Value="24"/>
            <Setter Property="TextWrapping" Value="Wrap"/>
            <Setter Property="IsTextSelectionEnabled" Value="True"/>
          </Style>
          <Style x:Key="CsCodeTextStyle" TargetType="TextBlock" BasedOn="{StaticResource BodyTextBlockStyle}">
            <Setter Property="FontFamily" Value="Cascadia Mono, Consolas"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="LineHeight" Value="21"/>
            <Setter Property="TextWrapping" Value="Wrap"/>
            <Setter Property="IsTextSelectionEnabled" Value="True"/>
          </Style>
          <Style x:Key="CsKeycapTextStyle" TargetType="TextBlock">
            <Setter Property="FontSize" Value="11"/>
            <Setter Property="LineHeight" Value="14"/>
            <Setter Property="FontFamily" Value="Segoe UI Variable Small, Segoe UI"/>
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}"/>
            <Setter Property="HorizontalAlignment" Value="Center"/>
            <Setter Property="VerticalAlignment" Value="Center"/>
          </Style>

          <Style x:Key="CsKeycapStyle" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource ControlFillColorSecondaryBrush}"/>
            <Setter Property="BorderBrush" Value="{ThemeResource ControlStrokeColorDefaultBrush}"/>
            <Setter Property="BorderThickness" Value="1,1,1,2"/>
            <Setter Property="CornerRadius" Value="4"/>
            <Setter Property="Padding" Value="5,1,5,1"/>
            <Setter Property="MinWidth" Value="20"/>
            <Setter Property="Height" Value="20"/>
            <Setter Property="VerticalAlignment" Value="Center"/>
          </Style>
          <Style x:Key="CsContentSurfaceStyle" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource LayerFillColorDefaultBrush}"/>
            <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}"/>
            <Setter Property="BorderThickness" Value="1,1,0,0"/>
            <Setter Property="CornerRadius" Value="8,0,0,0"/>
          </Style>
          <Style x:Key="CsCardStyle" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource CardBackgroundFillColorDefaultBrush}"/>
            <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="CornerRadius" Value="8"/>
          </Style>
          <Style x:Key="CsSettingsCardStyle" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource CardBackgroundFillColorDefaultBrush}"/>
            <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="CornerRadius" Value="{ThemeResource ControlCornerRadius}"/>
            <Setter Property="Padding" Value="16,12,16,12"/>
            <Setter Property="MinHeight" Value="64"/>
          </Style>
          <Style x:Key="CsIconTileStyle" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource CardBackgroundFillColorSecondaryBrush}"/>
            <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="CornerRadius" Value="8"/>
          </Style>
          <Style x:Key="CsMediaFrameStyle" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource SubtleFillColorSecondaryBrush}"/>
            <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="CornerRadius" Value="6"/>
          </Style>
          <Style x:Key="CsEmptyGlyphTileStyle" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource SubtleFillColorSecondaryBrush}"/>
            <Setter Property="Width" Value="56"/>
            <Setter Property="Height" Value="56"/>
            <Setter Property="CornerRadius" Value="28"/>
            <Setter Property="HorizontalAlignment" Value="Center"/>
          </Style>
          <Style x:Key="CsDividerStyle" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource DividerStrokeColorDefaultBrush}"/>
            <Setter Property="Height" Value="1"/>
          </Style>
          <Style x:Key="CsBadgeStyle" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource SubtleFillColorSecondaryBrush}"/>
            <Setter Property="CornerRadius" Value="4"/>
            <Setter Property="Padding" Value="6,2,6,2"/>
          </Style>

          <Style x:Key="CsSecondaryIconStyle" TargetType="FontIcon">
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}"/>
          </Style>
          <Style x:Key="CsTertiaryIconStyle" TargetType="FontIcon">
            <Setter Property="Foreground" Value="{ThemeResource TextFillColorTertiaryBrush}"/>
          </Style>
          <Style x:Key="CsAccentIconStyle" TargetType="FontIcon">
            <Setter Property="Foreground" Value="{ThemeResource AccentTextFillColorPrimaryBrush}"/>
          </Style>
          <Style x:Key="CsCautionIconStyle" TargetType="FontIcon">
            <Setter Property="Foreground" Value="{ThemeResource SystemFillColorCriticalBrush}"/>
          </Style>
          <Style x:Key="CsRecordingDotStyle" TargetType="Ellipse">
            <Setter Property="Fill" Value="{ThemeResource SystemFillColorSuccessBrush}"/>
            <Setter Property="Width" Value="8"/>
            <Setter Property="Height" Value="8"/>
          </Style>
          <Style x:Key="CsPausedDotStyle" TargetType="Ellipse">
            <Setter Property="Fill" Value="{ThemeResource SystemFillColorCautionBrush}"/>
            <Setter Property="Width" Value="8"/>
            <Setter Property="Height" Value="8"/>
          </Style>

          <Style x:Key="CsSearchBoxStyle" TargetType="TextBox" BasedOn="{StaticResource DefaultTextBoxStyle}">
            <Setter Property="FontSize" Value="14"/>
            <Setter Property="MinHeight" Value="36"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="CornerRadius" Value="6"/>
          </Style>
          <Style x:Key="CsHistoryItemStyle" TargetType="ListViewItem" BasedOn="{StaticResource DefaultListViewItemStyle}">
            <Setter Property="Padding" Value="12,10,12,10"/>
            <Setter Property="Margin" Value="6,1,6,1"/>
            <Setter Property="MinHeight" Value="56"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="VerticalContentAlignment" Value="Top"/>
          </Style>
          <Style x:Key="CsNavItemStyle" TargetType="ListViewItem" BasedOn="{StaticResource DefaultListViewItemStyle}">
            <Setter Property="Padding" Value="12,0,8,0"/>
            <Setter Property="MinHeight" Value="36"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
          </Style>
        </ResourceDictionary>
        """;
}

// Segoe Fluent Icons code points used across the app.
internal static class Glyph
{
    public const string Brand = "\uE77F", Search = "\uE721", Settings = "\uE713", Back = "\uE72B";
    public const string AllClips = "\uE81C", Text = "\uE8E4", Image = "\uE8B9", Files = "\uE8B7", Pinned = "\uE718";
    public const string Pin = "\uE718", Unpin = "\uE77A", Delete = "\uE74D", Copy = "\uE8C8", Paste = "\uE77F";
    public const string Snapshot = "\uE7F4", Clock = "\uE823", Info = "\uE946", Pause = "\uE769", Play = "\uE768";
    public const string Document = "\uE8A5", Folder = "\uE8B7", Video = "\uE714", Audio = "\uE8D6", Archive = "\uF012";
    public const string Code = "\uE943", App = "\uE71D", Program = "\uE756", ChevronLeft = "\uE76B", ChevronRight = "\uE76C";
    public const string Keyboard = "\uE765", Capture = "\uE722", Shield = "\uEA18", Storage = "\uEDA2", Theme = "\uE790";
    public const string Startup = "\uE7E8", Tray = "\uE921", OpenFolder = "\uE838", Warning = "\uE7BA", Lock = "\uE72E";
    public const string Calendar = "\uE787", Size = "\uE740";

    public static string ForFile(Core.FileCategory category) => category switch
    {
        Core.FileCategory.Folder => Folder, Core.FileCategory.Image => Image, Core.FileCategory.Video => Video,
        Core.FileCategory.Audio => Audio, Core.FileCategory.Archive => Archive, Core.FileCategory.Code => Code,
        Core.FileCategory.Executable => Program, _ => Document
    };
}
