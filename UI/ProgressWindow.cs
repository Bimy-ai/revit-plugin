using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace BimyRevit.UI;

/// <summary>
/// A modal "this is happening" window for the network part of a pull.
///
/// A published building is tens of megabytes of STEP text, and a Revit add-in
/// that blocks its host for thirty seconds with no window and no cursor change
/// is indistinguishable from a crash — users kill Revit. Running the download
/// inside <see cref="Window.ShowDialog"/> means the dispatcher keeps pumping, so
/// the bar animates and the byte count moves while the transfer runs on the same
/// thread the command was invoked on.
///
/// Scope is deliberately just the transfer: Revit's own IFC importer puts up its
/// progress bar for the conversion that follows, and stacking a second modal on
/// top of it would fight Revit for the foreground.
/// </summary>
internal static class ProgressWindow
{
    /// <summary>
    /// Runs <paramref name="work"/> with a modal progress window up, and returns
    /// its result. Exceptions from the work propagate to the caller unchanged —
    /// the window is only presentation, never an error boundary.
    /// </summary>
    public static T Run<T>(
        IntPtr revitMainWindow,
        string title,
        string initialStatus,
        Func<IProgress<string>, Task<T>> work)
    {
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            // No close button semantics to honour: the work isn't cancellable
            // yet, and a window you can dismiss while the transfer continues is
            // a lie. Escape / the X are both suppressed below.
            WindowStyle = WindowStyle.ToolWindow,
        };

        var panel = new StackPanel { Margin = new Thickness(18) };

        var status = new TextBlock
        {
            Text = initialStatus,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        panel.Children.Add(status);

        var bar = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 6,
            Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x7A, 0xE5)),
        };
        panel.Children.Add(bar);

        window.Content = panel;
        if (revitMainWindow != IntPtr.Zero)
            new WindowInteropHelper(window) { Owner = revitMainWindow };

        var closing = false;
        window.Closing += (_, e) => { if (!closing) e.Cancel = true; };

        T result = default!;
        Exception? failure = null;

        window.Loaded += async (_, _) =>
        {
            var progress = new Progress<string>(text => status.Text = text);
            try { result = await work(progress); }
            catch (Exception ex) { failure = ex; }
            finally
            {
                closing = true;
                window.Close();
            }
        };

        window.ShowDialog();

        if (failure is not null)
        {
            // Rethrow with the original stack intact — the caller's catch
            // clauses filter on BimyFetchException.StatusCode and would miss a
            // wrapped one.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result;
    }
}
