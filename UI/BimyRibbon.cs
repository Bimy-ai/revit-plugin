using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using BimyRevit.Commands;

namespace BimyRevit.UI;

internal static class BimyRibbon
{
    private const string PanelName = "BIMy";

    // Single-PNG source for every icon slot. Revit downscales the bitmap for the
    // 16×16 small-image slot; the bundled logo is rectangular but WPF
    // BitmapImage preserves aspect ratio and letterboxes cleanly on the ribbon's
    // grey background.
    private const string LogoResource = "BimyRevit.Resources.bimy-logo.png";

    public static void Build(UIControlledApplication application)
    {
        var panel = application.CreateRibbonPanel(PanelName);
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var logo = LoadEmbeddedImage(LogoResource);

        // The one thing this add-in exists to do gets the large button. The
        // previous layout buried it inside a pulldown next to token management,
        // which made the daily action cost the same two clicks as the
        // once-a-quarter one.
        panel.AddItem(new PushButtonData(
            "BimyLoad",
            "Load from\nBIMy",
            assemblyPath,
            typeof(LoadFromBimyCommand).FullName)
        {
            ToolTip = "Pull a BIMy project into Revit as a native model.",
            LongDescription =
                "Downloads the model you exported from BIMy and opens it as a native Revit project — "
                + "walls, floors, ceilings, doors, windows, openings, rooms, materials and properties, "
                + "all as real Revit elements. You can also link it into the project you already have open.\n\n"
                + "Greyed out until you connect: BIMy → Set API token…",
            LargeImage = logo,
            Image = logo,
            // Availability governs the enabled state — setting .Enabled here as
            // well would fight it.
            AvailabilityClassName = typeof(LoadFromBimyAvailability).FullName,
        });

        panel.AddSeparator();

        // Session management, stacked small: three rarely-pressed items in the
        // vertical space one large button would take.
        panel.AddStackedItems(
            new PushButtonData(
                "BimySetApiToken",
                "Set API token…",
                assemblyPath,
                typeof(SetApiTokenCommand).FullName)
            {
                ToolTip = "Connect this Revit to your BIMy workspace, or replace the saved token.",
                LongDescription =
                    "Generate an API token in BIMy under Settings → API tokens, then paste it here and "
                    + "pick the matching environment. The token is stored encrypted for your Windows "
                    + "account only. The environment is locked while a session exists — disconnect to change it.",
            },
            new PushButtonData(
                "BimyDisconnect",
                "Disconnect",
                assemblyPath,
                typeof(DisconnectCommand).FullName)
            {
                ToolTip = "Clear the saved BIMy session on this machine.",
                AvailabilityClassName = typeof(DisconnectAvailability).FullName,
            },
            new PushButtonData(
                "BimyStatus",
                "Status & log",
                assemblyPath,
                typeof(BimyStatusCommand).FullName)
            {
                ToolTip = "Who's connected, which environment, add-in version, and where the log is.",
            });
    }

    // Reads a PNG from the assembly's embedded resources and returns a frozen
    // BitmapImage — frozen so it can be shared across UI threads (Revit builds
    // the ribbon on one thread, renders on another). The stream is fully decoded
    // up front (OnLoad) so we can close it immediately.
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
