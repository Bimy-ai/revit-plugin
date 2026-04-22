using System.Reflection;
using Autodesk.Revit.UI;
using RevitWallsPlugin.Commands;

namespace RevitWallsPlugin.UI;

internal static class BimyRibbon
{
    private const string PanelName = "BIMy";

    public static void Build(UIControlledApplication application)
    {
        var panel = application.CreateRibbonPanel(PanelName);
        var assemblyPath = Assembly.GetExecutingAssembly().Location;

        var connectPulldown = (PulldownButton)panel.AddItem(new PulldownButtonData("BimyConnect", "Connect\nBIMy")
        {
            ToolTip = "Connect to BIMy. Workspace admin can issue API Tokens.",
            LongDescription = "Set or replace the API token used to talk to BIMy. Disconnect to clear the saved session.",
        });

        connectPulldown.AddPushButton(new PushButtonData(
            "BimySetApiToken",
            "Set API token…",
            assemblyPath,
            typeof(SetApiTokenCommand).FullName)
        {
            ToolTip = "Set the BIMy API token and pick the environment.",
            LongDescription = "Workspace admin can issue API tokens. The environment is locked once a token has been saved.",
        });

        connectPulldown.AddPushButton(new PushButtonData(
            "BimyDisconnect",
            "Disconnect",
            assemblyPath,
            typeof(DisconnectCommand).FullName)
        {
            ToolTip = "Clear the saved BIMy session.",
        });

        var loadButton = (PushButton)panel.AddItem(new PushButtonData(
            "BimyLoad",
            "Load from\nBIMy",
            assemblyPath,
            typeof(LoadFromBimyCommand).FullName)
        {
            ToolTip = "Fetch userObjects from a BIMy URL and rebuild walls.",
            LongDescription = "Pastes a URL from your BIMy environment, fetches the userObjects payload, and re-imports walls.",
            AvailabilityClassName = typeof(LoadFromBimyAvailability).FullName,
        });
        _ = loadButton; // kept for clarity / future tweaks
    }
}
