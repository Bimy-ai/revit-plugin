using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BimyRevit.Models;

namespace BimyRevit.UI;

internal sealed class SetApiTokenResult
{
    public BimyEnvironment Environment { get; init; }
    public string Token { get; init; } = string.Empty;
}

internal static class SetApiTokenDialog
{
    public static SetApiTokenResult? Show(
        IntPtr revitMainWindow,
        BimyEnvironment defaultEnv,
        bool environmentLocked,
        string? lockedReasonHint)
    {
        SetApiTokenResult? result = null;

        var window = new Window
        {
            Title = "Connect BIMy",
            Width = 520,
            Height = 290,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Manual,
        };

        var root = new Grid { Margin = new Thickness(16) };
        for (var i = 0; i < 7; i++)
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var envLabel = new TextBlock
        {
            Text = "Environment",
            Margin = new Thickness(0, 0, 0, 4),
        };
        Grid.SetRow(envLabel, 0);
        root.Children.Add(envLabel);

        var envCombo = new ComboBox
        {
            IsEnabled = !environmentLocked,
            Padding = new Thickness(6, 4, 6, 4),
        };
        foreach (var env in BimyEnvironments.All)
        {
            envCombo.Items.Add(new ComboBoxItem
            {
                Content = BimyEnvironments.DisplayName(env) + "  —  " + BimyEnvironments.BaseUrl(env),
                Tag = env,
                IsSelected = env == defaultEnv,
            });
        }
        Grid.SetRow(envCombo, 1);
        root.Children.Add(envCombo);

        if (environmentLocked)
        {
            var lockNote = new TextBlock
            {
                Text = lockedReasonHint ?? "Environment is locked once a session has been established. Disconnect to change it.",
                Margin = new Thickness(0, 4, 0, 0),
                FontStyle = FontStyles.Italic,
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(lockNote, 2);
            root.Children.Add(lockNote);
        }

        var tokenLabel = new TextBlock
        {
            Text = "API token",
            Margin = new Thickness(0, 12, 0, 4),
        };
        Grid.SetRow(tokenLabel, 3);
        root.Children.Add(tokenLabel);

        var tokenBox = new PasswordBox
        {
            Padding = new Thickness(6, 4, 6, 4),
        };
        Grid.SetRow(tokenBox, 4);
        root.Children.Add(tokenBox);

        var hint = new TextBlock
        {
            Text = "Workspace admin can issue API tokens.",
            Margin = new Thickness(0, 6, 0, 0),
            FontStyle = FontStyles.Italic,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(hint, 5);
        root.Children.Add(hint);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var ok = new Button
        {
            Content = "Connect",
            Width = 110,
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
        Grid.SetRow(buttons, 8);
        root.Children.Add(buttons);

        ok.Click += (_, _) =>
        {
            var token = tokenBox.Password?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                MessageBox.Show(window, "Please enter an API token.", "Connect BIMy",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selected = envCombo.SelectedItem as ComboBoxItem;
            var env = (selected?.Tag as BimyEnvironment?) ?? defaultEnv;

            result = new SetApiTokenResult { Environment = env, Token = token };
            window.DialogResult = true;
        };

        window.Content = root;

        if (revitMainWindow != IntPtr.Zero)
            new WindowInteropHelper(window) { Owner = revitMainWindow };

        tokenBox.Focus();

        return window.ShowDialog() == true ? result : null;
    }
}
