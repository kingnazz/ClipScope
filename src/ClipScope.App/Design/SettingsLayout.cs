using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ClipScope.App.Design;

// Windows 11 Settings-style building blocks: page headers, group headings and setting cards.
internal static class SettingsLayout
{
    public static StackPanel Page(string title, string description)
    {
        var page = new StackPanel { Spacing = Ui.Space1 + 2 };
        var heading = Ui.Text("CsPageTitleTextStyle", title);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        page.Children.Add(heading);
        page.Children.Add(new TextBlock { Style = Ui.Style("CsBodySecondaryTextStyle"), Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, Ui.Space4) });
        return page;
    }

    public static TextBlock Group(string title)
    {
        var heading = new TextBlock { Style = Ui.Style("CsBodyStrongTextStyle"), Text = title, Margin = new Thickness(0, Ui.Space4, 0, Ui.Space1) };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        return heading;
    }

    // A setting card: icon, header, description and a trailing control. Optional content
    // below (for example a multi-line editor) spans the text and control columns.
    public static Border Card(string glyph, string header, string? description, FrameworkElement? control, FrameworkElement? below = null, string iconStyle = "CsSecondaryIconStyle")
    {
        var grid = new Grid { ColumnSpacing = Ui.Space4, RowSpacing = Ui.Space3 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var icon = Ui.Icon(glyph, 18, iconStyle); icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap });
        if (description is not null)
            text.Children.Add(new TextBlock { Style = Ui.Style("CsCaptionTextStyle"), Text = description, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            if (string.IsNullOrEmpty(AutomationProperties.GetName(control))) AutomationProperties.SetName(control, header);
            if (description is not null) AutomationProperties.SetHelpText(control, description);
            Grid.SetColumn(control, 2); grid.Children.Add(control);
        }
        if (below is not null)
        {
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetRow(below, 1); Grid.SetColumn(below, 1); Grid.SetColumnSpan(below, 2); grid.Children.Add(below);
            // Name only interactive content after the card; text (status, paths) keeps its own text as its name.
            if (below is Control && string.IsNullOrEmpty(AutomationProperties.GetName(below))) AutomationProperties.SetName(below, header);
        }
        return new Border { Style = Ui.Style("CsSettingsCardStyle"), Child = grid, Margin = new Thickness(0, 0, 0, 2) };
    }

    public static ToggleSwitch Toggle(bool value) => new() { IsOn = value, OnContent = "On", OffContent = "Off", MinWidth = 0 };

    public static NumberBox Number(double value, double min, double max)
        => new() { Value = value, Minimum = min, Maximum = max, Width = 148, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten };

    // A number box followed by its unit, e.g. "[ 30 ] days".
    public static StackPanel WithUnit(NumberBox box, string unit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.Space2 };
        var label = new TextBlock { Text = unit, Style = Ui.Style("CsCaptionTextStyle"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 32 };
        row.Children.Add(box); row.Children.Add(label);
        return row;
    }
}
