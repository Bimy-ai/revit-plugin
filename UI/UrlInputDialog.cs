using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace RevitWallsPlugin.UI;

internal static class UrlInputDialog
{
    public static string? Show(IntPtr revitMainWindow, string title, string defaultUrl)
    {
        string? result = null;

        var window = new Window
        {
            Title = title,
            Width = 520,
            Height = 180,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Manual,
        };

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = "Provide url",
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(label, 0);
        root.Children.Add(label);

        var textBox = new TextBox
        {
            Text = defaultUrl,
            Padding = new Thickness(6, 4, 6, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(textBox, 1);
        root.Children.Add(textBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button
        {
            Content = "OK",
            Width = 90,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Width = 90,
            Height = 28,
            IsCancel = true,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        ok.Click += (_, _) =>
        {
            result = textBox.Text?.Trim();
            window.DialogResult = true;
        };

        window.Content = root;

        if (revitMainWindow != IntPtr.Zero)
            new WindowInteropHelper(window) { Owner = revitMainWindow };

        textBox.Focus();
        textBox.SelectAll();

        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(result)
            ? result
            : null;
    }
}
