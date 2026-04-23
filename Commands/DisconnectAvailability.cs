using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWallsPlugin.Services;

namespace RevitWallsPlugin.Commands;

public sealed class DisconnectAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        => SessionState.IsConnected;
}
