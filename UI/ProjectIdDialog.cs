using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace RevitWallsPlugin.UI;

internal static class ProjectIdDialog
{
    private static readonly Regex _idFromUrl = new(
        @"/api/data/([0-9a-fA-F]{24})",
        RegexOptions.Compiled);

    private static readonly Regex _bareId = new(
        @"^[0-9a-fA-F]{24}$",
        RegexOptions.Compiled);

    public static string? Show(IntPtr revitMainWindow, string title, string envLabel, string? defaultProjectId)
    {
        string? result = null;

        var window = new Window
        {
            Title = title,
            Width = 460,
            Height = 210,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Manual,
        };

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = $"Project ID (environment: {envLabel})",
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(label, 0);
        root.Children.Add(label);

        var textBox = new TextBox
        {
            Text = defaultProjectId ?? string.Empty,
            Padding = new Thickness(6, 4, 6, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(textBox, 1);
        root.Children.Add(textBox);

        var hint = new TextBlock
        {
            Text = "Paste a project ID (24-char hex) or a full BIMy URL — the ID is extracted automatically.",
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        };
        Grid.SetRow(hint, 2);
        root.Children.Add(hint);

        var error = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Firebrick,
        };
        Grid.SetRow(error, 3);
        root.Children.Add(error);

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
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        ok.Click += (_, _) =>
        {
            var extracted = Extract(textBox.Text);
            if (extracted is null)
            {
                error.Text = "Please enter a 24-character hex project ID (or a BIMy URL containing one).";
                textBox.Focus();
                textBox.SelectAll();
                return;
            }
            result = extracted;
            window.DialogResult = true;
        };

        window.Content = root;

        if (revitMainWindow != IntPtr.Zero)
            new WindowInteropHelper(window) { Owner = revitMainWindow };

        textBox.Focus();
        textBox.SelectAll();

        return window.ShowDialog() == true ? result : null;
    }

    public static string? Extract(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();

        if (_bareId.IsMatch(trimmed))
            return trimmed.ToLowerInvariant();

        var match = _idFromUrl.Match(trimmed);
        if (match.Success)
            return match.Groups[1].Value.ToLowerInvariant();

        return null;
    }
}
