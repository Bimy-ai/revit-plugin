using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Silently deletes non-blocking warnings during the transaction so Revit does not
/// pop a modal dialog on the user while bulk-creating walls.
/// </summary>
internal sealed class SuppressWarningsPreprocessor : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
    {
        foreach (var msg in a.GetFailureMessages())
        {
            if (msg.GetSeverity() == FailureSeverity.Warning)
                a.DeleteWarning(msg);
        }
        return FailureProcessingResult.Continue;
    }
}
