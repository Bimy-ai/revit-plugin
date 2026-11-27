using Autodesk.Revit.DB;

namespace BimyRevit.Services.Import;

/// <summary>
/// Silently deletes non-blocking warnings during a build transaction so Revit
/// does not pop a modal dialog on the user while bulk-creating elements
/// (duplicate marks, slightly-off-axis lines, rooms not enclosed, …).
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
