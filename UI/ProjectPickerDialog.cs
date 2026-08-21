using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using BimyRevit.Models;
using BimyRevit.Services;

namespace BimyRevit.UI;

/// <summary>How the pulled model should land in Revit.</summary>
internal enum PullMode
{
    /// <summary>Open it as a new native Revit project (the default).</summary>
    OpenNew,
    /// <summary>Link the pulled model into the document that's already open.</summary>
    LinkIntoCurrent,
}

internal sealed class PickResult
{
    public string ProjectId { get; init; } = string.Empty;
    public string? ProjectName { get; init; }
    public PullMode Mode { get; init; }
}

/// <summary>
/// Picks which BIMy project to pull, and how.
///
/// The version this replaces asked for a 24-character hex id, which meant every
/// pull started with a trip to the browser to copy one — for a plugin whose
/// entire job is "get my building into Revit in two clicks". This lists the
/// workspace's real projects by name, marks the ones actually exported to Revit
/// (and how long ago), remembers the last one pulled, and keeps the paste field
/// as a fallback for the cases a list can't cover: a project shared by id, or a
/// deployment whose list call didn't answer.
/// </summary>
internal static class ProjectPickerDialog
{
    // Row template. Written as XAML rather than assembled from C# objects
    // because a two-line list row with an ellipsised title and a status pill is
    // twenty lines of declarative markup and ~80 of imperative WPF.
    private const string RowTemplateXaml = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid Margin="2,5,2,5">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="Auto"/>
              <ColumnDefinition Width="*"/>
              <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="{Binding Emoji}" FontSize="17"
                       Margin="2,0,10,0" VerticalAlignment="Center"/>
            <StackPanel Grid.Column="1" VerticalAlignment="Center">
              <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
              <TextBlock Text="{Binding Subtitle}" FontSize="11" Opacity="0.6"
                         TextTrimming="CharacterEllipsis" Margin="0,1,0,0"/>
            </StackPanel>
            <Border Grid.Column="2" CornerRadius="9" Padding="8,2,8,2" Margin="8,0,2,0"
                    VerticalAlignment="Center"
                    Background="{Binding BadgeBackground}"
                    Visibility="{Binding BadgeVisibility}">
              <TextBlock Text="{Binding Badge}" FontSize="10" FontWeight="SemiBold"
                         Foreground="{Binding BadgeForeground}"/>
            </Border>
          </Grid>
        </DataTemplate>
        """;

    private static readonly Brush PublishedBackground = Frozen(Color.FromRgb(0xE3, 0xF3, 0xE7));
    private static readonly Brush PublishedForeground = Frozen(Color.FromRgb(0x1B, 0x6B, 0x37));
    private static readonly Brush MutedBackground = Frozen(Color.FromRgb(0xEE, 0xEE, 0xEE));
    private static readonly Brush MutedForeground = Frozen(Color.FromRgb(0x77, 0x77, 0x77));
    private static readonly Brush Subtle = Frozen(Color.FromRgb(0x78, 0x78, 0x78));

    /// <summary>One list row. Public getters only — the list is rebuilt, never mutated.</summary>
    internal sealed class Row
    {
        public string ProjectId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Emoji { get; init; } = "🏗";
        public string Subtitle { get; init; } = string.Empty;
        public string Badge { get; init; } = string.Empty;
        public bool IsPublished { get; init; }
        public Brush BadgeBackground => IsPublished ? PublishedBackground : MutedBackground;
        public Brush BadgeForeground => IsPublished ? PublishedForeground : MutedForeground;
        public Visibility BadgeVisibility => string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;
        /// <summary>Lower-cased haystack the search box filters against.</summary>
        public string SearchKey { get; init; } = string.Empty;
    }

    public static PickResult? Show(
        IntPtr revitMainWindow,
        BimyEnvironment env,
        string? accountLabel,
        IReadOnlyList<BimyProject> projects,
        IReadOnlyDictionary<string, BimyPublishedModel> published,
        string? preselectProjectId,
        bool canLink)
    {
        PickResult? result = null;
        var envLabel = BimyEnvironments.DisplayName(env);

        var window = new Window
        {
            Title = "Load from BIMy",
            Width = 620,
            Height = 560,
            MinWidth = 480,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var root = new Grid { Margin = new Thickness(16) };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });

        // ── Header: which workspace/environment this is pulling from ─────────
        var header = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        };
        header.Inlines.Add(new Run("Pulling from ") { Foreground = Subtle });
        header.Inlines.Add(new Run(envLabel) { FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(accountLabel))
        {
            header.Inlines.Add(new Run("  ·  ") { Foreground = Subtle });
            header.Inlines.Add(new Run(accountLabel) { Foreground = Subtle });
        }
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // ── Search ───────────────────────────────────────────────────────────
        var search = new TextBox
        {
            Padding = new Thickness(6, 5, 6, 5),
            Margin = new Thickness(0, 0, 0, 8),
            ToolTip = "Filter by project name or id",
        };
        Grid.SetRow(search, 1);
        root.Children.Add(search);
        AddPlaceholder(search, "Search projects…");

        // ── The list ─────────────────────────────────────────────────────────
        var rows = BuildRows(projects, published, env);
        var list = new ListBox
        {
            ItemTemplate = (DataTemplate)XamlReader.Parse(RowTemplateXaml),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemsSource = rows,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        Grid.SetRow(list, 2);
        root.Children.Add(list);

        // Empty state — a blank box with no explanation is the worst possible
        // answer to "I connected and I see nothing".
        var emptyNote = new TextBlock
        {
            Text = projects.Count == 0
                ? "No projects came back for this token. Check that the token belongs to the workspace you expect, or paste a project id below."
                : "No project matches that search.",
            Foreground = Subtle,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 12, 4, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        Grid.SetRow(emptyNote, 2);
        root.Children.Add(emptyNote);

        var view = (CollectionView)CollectionViewSource.GetDefaultView(list.ItemsSource);
        search.TextChanged += (_, _) =>
        {
            var needle = search.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            view.Filter = needle.Length == 0
                ? null
                : o => o is Row r && r.SearchKey.Contains(needle, StringComparison.Ordinal);
            var anyVisible = view.Cast<object>().Any();
            emptyNote.Visibility = anyVisible ? Visibility.Collapsed : Visibility.Visible;
            if (anyVisible && list.SelectedItem is null) list.SelectedIndex = 0;
        };

        // ── Options: manual id, and how to bring the model in ────────────────
        var options = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(options, 3);
        root.Children.Add(options);

        var manualLabel = new TextBlock
        {
            Text = "Not listed? Paste a project id or a BIMy link",
            Foreground = Subtle,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
        };
        options.Children.Add(manualLabel);

        var manual = new TextBox { Padding = new Thickness(6, 4, 6, 4) };
        options.Children.Add(manual);
        AddPlaceholder(manual, "https://…/projects/…  or  24-character id");

        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var openRadio = new RadioButton
        {
            Content = "Open as a new Revit project",
            IsChecked = true,
            Margin = new Thickness(0, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var linkRadio = new RadioButton
        {
            Content = "Link into the open project",
            IsEnabled = canLink,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = canLink
                ? "Bring the BIMy model in as a Revit link, leaving the current model untouched."
                : "Open a Revit project first to link a BIMy model into it.",
        };
        modeRow.Children.Add(openRadio);
        modeRow.Children.Add(linkRadio);
        options.Children.Add(modeRow);

        // ── Buttons ──────────────────────────────────────────────────────────
        var buttons = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        var openInBimy = new Button
        {
            Content = "Open in BIMy ↗",
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            ToolTip = "Open the selected project in your browser — that's where \"Export to Revit\" lives.",
            IsEnabled = false,
        };
        Grid.SetColumn(openInBimy, 0);
        buttons.Children.Add(openInBimy);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var load = new Button { Content = "Load", Width = 110, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 90, Height = 28, IsCancel = true };
        right.Children.Add(load);
        right.Children.Add(cancel);
        Grid.SetColumn(right, 2);
        buttons.Children.Add(right);

        // ── Wiring ───────────────────────────────────────────────────────────
        list.SelectionChanged += (_, _) =>
        {
            openInBimy.IsEnabled = list.SelectedItem is Row;
            // Picking from the list and typing an id are two ways to say the
            // same thing; letting both hold a value at once means the dialog
            // has to guess. Selecting clears the box.
            if (list.SelectedItem is Row && !string.IsNullOrWhiteSpace(manual.Text)) manual.Clear();
        };

        manual.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(manual.Text)) list.SelectedItem = null;
        };

        openInBimy.Click += (_, _) =>
        {
            if (list.SelectedItem is not Row row) return;
            OpenInBrowser(BimyEnvironments.ProjectWebUrl(env, row.ProjectId));
        };

        void Commit()
        {
            var typed = BimyId.Extract(manual.Text);
            var row = list.SelectedItem as Row;

            // Order matters: text that was typed but doesn't parse gets the
            // specific complaint, not the generic "pick something" — the user
            // clearly tried, and needs to know what was wrong with it.
            if (typed is null && !string.IsNullOrWhiteSpace(manual.Text))
            {
                MessageBox.Show(window,
                    "That doesn't contain a project id. A BIMy id is 24 hexadecimal characters — copy it from the project's URL, or from Export to Revit in the app.",
                    "Load from BIMy", MessageBoxButton.OK, MessageBoxImage.Warning);
                manual.Focus();
                manual.SelectAll();
                return;
            }

            if (typed is null && row is null)
            {
                MessageBox.Show(window,
                    "Pick a project from the list, or paste a project id / BIMy link.",
                    "Load from BIMy", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            result = new PickResult
            {
                ProjectId = typed ?? row!.ProjectId,
                ProjectName = typed is null ? row!.Name : null,
                Mode = linkRadio.IsChecked == true ? PullMode.LinkIntoCurrent : PullMode.OpenNew,
            };
            window.DialogResult = true;
        }

        load.Click += (_, _) => Commit();
        list.MouseDoubleClick += (_, e) =>
        {
            // Only a double-click ON a row counts; on empty list space it would
            // otherwise load whatever happened to still be selected.
            if (list.SelectedItem is Row && e.OriginalSource is DependencyObject src && HitRow(src)) Commit();
        };

        window.Content = root;
        if (revitMainWindow != IntPtr.Zero)
            new WindowInteropHelper(window) { Owner = revitMainWindow };

        // Land on the project this machine pulled last: the overwhelmingly
        // common next action is "pull it again after editing it in BIMy".
        var preselect = rows.FirstOrDefault(r => string.Equals(r.ProjectId, preselectProjectId, StringComparison.OrdinalIgnoreCase));
        if (preselect is not null)
        {
            list.SelectedItem = preselect;
            list.ScrollIntoView(preselect);
        }
        else if (rows.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        window.Loaded += (_, _) => search.Focus();

        return window.ShowDialog() == true ? result : null;
    }

    private static List<Row> BuildRows(
        IReadOnlyList<BimyProject> projects,
        IReadOnlyDictionary<string, BimyPublishedModel> published,
        BimyEnvironment env)
    {
        var rows = new List<Row>(projects.Count);
        var pulls = PullCache.GetAll(env);

        foreach (var p in projects)
        {
            if (string.IsNullOrWhiteSpace(p.Id)) continue;

            published.TryGetValue(p.Id!, out var pub);
            pulls.TryGetValue(p.Id!, out var pulled);

            var parts = new List<string>();
            if (pub is not null)
                parts.Add("Exported " + Ago(pub.UpdatedAt));
            else if (published.Count > 0)
                parts.Add("Not exported to Revit yet");
            else if (p.Touched is not null)
                parts.Add("Edited " + Ago(p.Touched));
            if (pulled?.PulledAt is not null)
                parts.Add("pulled here " + Ago(pulled.PulledAt));
            parts.Add(p.Id!);

            rows.Add(new Row
            {
                ProjectId = p.Id!,
                Name = p.DisplayName,
                Emoji = string.IsNullOrWhiteSpace(p.Emoji) ? "🏗" : p.Emoji!,
                Subtitle = string.Join("  ·  ", parts),
                // With no index available every badge would say the same thing,
                // which is noise; show them only when the index answered.
                Badge = published.Count == 0 ? string.Empty : (pub is not null ? "READY" : "NOT EXPORTED"),
                IsPublished = pub is not null,
                SearchKey = (p.DisplayName + " " + p.Id).ToLowerInvariant(),
            });
        }

        // Pullable projects first — everything else in this dialog is a detour.
        // Within each group keep the server's newest-first order.
        return rows.OrderByDescending(r => r.IsPublished).ToList();
    }

    /// <summary>Compact relative time ("3 minutes ago", "yesterday", "12 Mar 2026").</summary>
    private static string Ago(DateTimeOffset? when)
    {
        if (when is null) return "at an unknown time";
        var delta = DateTimeOffset.Now - when.Value;
        if (delta < TimeSpan.Zero) return "just now";
        if (delta.TotalMinutes < 1) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} h ago";
        if (delta.TotalDays < 2) return "yesterday";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays} days ago";
        return when.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>True when the clicked element sits inside a list row (not the empty area below).</summary>
    private static bool HitRow(DependencyObject source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ListBoxItem) return true;
        return false;
    }

    private static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open {url}: {ex.Message}"); }
    }

    /// <summary>
    /// Grey hint text that disappears once the box has content. WPF has no
    /// built-in placeholder; an adorner is the standard way and keeps the
    /// TextBox's own Text property clean (a "clear the prompt on focus" hack
    /// eventually submits the prompt as data).
    /// </summary>
    private static void AddPlaceholder(TextBox box, string text)
    {
        void Sync()
        {
            var layer = AdornerLayer.GetAdornerLayer(box);
            if (layer is null) return;
            foreach (var existing in layer.GetAdorners(box)?.OfType<PlaceholderAdorner>().ToList() ?? new List<PlaceholderAdorner>())
                layer.Remove(existing);
            if (string.IsNullOrEmpty(box.Text)) layer.Add(new PlaceholderAdorner(box, text));
        }

        box.Loaded += (_, _) => Sync();
        box.TextChanged += (_, _) => Sync();
    }

    private sealed class PlaceholderAdorner : Adorner
    {
        private readonly string _text;

        public PlaceholderAdorner(UIElement adorned, string text) : base(adorned)
        {
            _text = text;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var box = (TextBox)AdornedElement;
            var formatted = new FormattedText(
                _text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch),
                box.FontSize,
                Subtle,
                VisualTreeHelper.GetDpi(box).PixelsPerDip);
            drawingContext.DrawText(formatted, new Point(box.Padding.Left + 2, box.Padding.Top + 1));
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
