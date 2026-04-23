using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using RevitWallsPlugin.Commands;

namespace RevitWallsPlugin.UI;

internal static class BimyRibbon
{
    private const string PanelName = "BIMy";

    // Single-PNG source for every icon slot. Revit downscales the bitmap
    // for the 16×16 small-image slot; the bundled logo is rectangular but
    // WPF BitmapImage preserves aspect ratio and letterboxes cleanly on the
    // ribbon's grey background.
    private const string LogoResource = "RevitWallsPlugin.Resources.bimy-logo.png";

    public static void Build(UIControlledApplication application)
    {
        var panel = application.CreateRibbonPanel(PanelName);
        var assemblyPath = Assembly.GetExecutingAssembly().Location;

        var logo = LoadEmbeddedImage(LogoResource);
        var largeIcon = logo;
        var smallIcon = logo;

        // Single pulldown — all BIMy actions live inside. Only the pulldown
        // carries the logo; individual menu items stay icon-less so the
        // dropdown reads as a flat list instead of a mini-toolbar.
        var pulldown = (PulldownButton)panel.AddItem(new PulldownButtonData("BimyMenu", "BIMy")
        {
            ToolTip = "BIMy — connect, disconnect, and load projects.",
            LongDescription = "Opens a menu of BIMy actions. Items that aren't available in the current state (e.g. Load from BIMy while disconnected) are greyed out.",
            LargeImage = largeIcon,
            Image = smallIcon,
        });

        // "Set API token…" — available whenever; used both for first-time
        // sign-in and for rotating the token on a live session.
        pulldown.AddPushButton(new PushButtonData(
            "BimySetApiToken",
            "Set API token…",
            assemblyPath,
            typeof(SetApiTokenCommand).FullName)
        {
            ToolTip = "Set or replace the BIMy API token.",
            LongDescription = "Workspace admin can issue API tokens. The environment is locked once a token has been saved.",
        });

        // "Disconnect" — only meaningful while a session exists. Greyed out
        // via DisconnectAvailability when SessionState.IsConnected is false.
        pulldown.AddPushButton(new PushButtonData(
            "BimyDisconnect",
            "Disconnect",
            assemblyPath,
            typeof(DisconnectCommand).FullName)
        {
            ToolTip = "Clear the saved BIMy session.",
            AvailabilityClassName = typeof(DisconnectAvailability).FullName,
        });

        // Visual break between session-management commands and the load
        // workflow — makes the intent of each group obvious at a glance.
        pulldown.AddSeparator();

        // "Load from BIMy" — needs an active session to fetch userObjects.
        // Greying (not hiding) keeps menu layout stable so muscle memory holds.
        pulldown.AddPushButton(new PushButtonData(
            "BimyLoad",
            "Load from BIMy",
            assemblyPath,
            typeof(LoadFromBimyCommand).FullName)
        {
            ToolTip = "Fetch userObjects from a BIMy URL and rebuild walls.",
            LongDescription = "Pastes a URL from your BIMy environment, fetches the userObjects payload, and re-imports walls.",
            AvailabilityClassName = typeof(LoadFromBimyAvailability).FullName,
        });
    }

    // Reads a PNG from the assembly's embedded resources and returns a frozen
    // BitmapImage — frozen so it can be shared across UI threads (Revit builds
    // the ribbon on one thread, renders on another). The stream is fully
    // decoded up front (OnLoad) so we can close it immediately.
    private static BitmapImage LoadEmbeddedImage(string resourceName)
    {
        using var stream = typeof(BimyRibbon).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new System.IO.FileNotFoundException($"Embedded resource not found: {resourceName}");
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
