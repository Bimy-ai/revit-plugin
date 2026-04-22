using Autodesk.Revit.UI;
using RevitWallsPlugin.Services;
using RevitWallsPlugin.UI;

namespace RevitWallsPlugin.Commands;

public sealed class BimyApplication : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            BimyRibbon.Build(application);
            Log.Info("BIMy ribbon panel created.");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to build BIMy ribbon panel.", ex);
            return Result.Failed;
        }

        // Warm up the session in the background so the Load button reflects connection state quickly.
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await SessionState.RefreshAsync().ConfigureAwait(false);
                Log.Info(ok ? "Startup session refresh: connected." : "Startup session refresh: not connected.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Startup session refresh threw: {ex.Message}");
            }
        });

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
}
