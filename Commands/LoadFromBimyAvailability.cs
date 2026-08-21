using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BimyRevit.Services;

namespace BimyRevit.Commands;

public sealed class LoadFromBimyAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        => SessionState.IsConnected;
}
