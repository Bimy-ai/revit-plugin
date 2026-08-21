using System.IO;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace BimyRevit.Services;

/// <summary>
/// Decides where a pulled model's .rvt is written.
///
/// The obvious implementation — always overwrite the file we wrote last time —
/// is the one that quietly destroys work: between two pulls the user has very
/// likely opened that file in Revit and changed something. The obvious
/// alternative — always write a new file — buries them in
/// "Tower (3).rvt". So this asks, once, only when there is genuinely something
/// to lose, and states what each choice costs.
/// </summary>
internal static class TargetPath
{
    /// <summary>
    /// Returns the absolute .rvt path to write, or null if the user cancelled.
    /// Never returns a path that already exists unless the user explicitly chose
    /// to replace it.
    /// </summary>
    public static string? Resolve(string projectName, string? previousPath)
    {
        Directory.CreateDirectory(BimyPaths.DefaultSaveRoot);
        var preferred = previousPath is not null && Directory.Exists(Path.GetDirectoryName(previousPath))
            ? previousPath
            : Path.Combine(BimyPaths.DefaultSaveRoot, BimyPaths.Sanitize(projectName) + ".rvt");

        if (!File.Exists(preferred)) return preferred;

        // The file exists. If Revit (or anything else) is holding it open,
        // replacing it cannot work — don't offer a choice that will fail.
        if (IsLocked(preferred))
        {
            var next = NextFreePath(preferred);
            var answer = new TaskDialog("Load from BIMy")
            {
                MainIcon = TaskDialogIcon.TaskDialogIconInformation,
                MainInstruction = "Your previous copy is open",
                MainContent =
                    $"{preferred}\n\nis in use, so it can't be replaced. The pulled model will be saved "
                    + $"alongside it as:\n\n{next}",
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Ok,
            }.Show();
            // Honour Cancel. Returning the path regardless would import anyway
            // after the user said no, which is the one thing a Cancel button
            // must never do.
            return answer == TaskDialogResult.Ok ? next : null;
        }

        var dialog = new TaskDialog("Load from BIMy")
        {
            MainIcon = TaskDialogIcon.TaskDialogIconWarning,
            MainInstruction = $"You already have a Revit file for “{projectName}”",
            MainContent = preferred,
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.CommandLink1,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Replace it with the new pull",
            "Any edits you made to that Revit file are lost. Choose this when the file is just a copy of the BIMy model.");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            "Save the new pull alongside it",
            NextFreePath(preferred));
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3,
            "Choose where to save…");

        return dialog.Show() switch
        {
            TaskDialogResult.CommandLink1 => preferred,
            TaskDialogResult.CommandLink2 => NextFreePath(preferred),
            TaskDialogResult.CommandLink3 => Browse(preferred),
            _ => null,
        };
    }

    private static string? Browse(string suggested)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the BIMy model as",
            Filter = "Revit project (*.rvt)|*.rvt",
            DefaultExt = ".rvt",
            AddExtension = true,
            FileName = Path.GetFileName(suggested),
            InitialDirectory = Path.GetDirectoryName(suggested) ?? BimyPaths.DefaultSaveRoot,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>"Tower.rvt" → "Tower (2).rvt" → "Tower (3).rvt" — first one free.</summary>
    private static string NextFreePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? BimyPaths.DefaultSaveRoot;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        // Don't compound suffixes: "Tower (2).rvt" should yield "Tower (3).rvt",
        // not "Tower (2) (2).rvt".
        var match = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*?) \((\d+)\)$");
        if (match.Success) stem = match.Groups[1].Value;

        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        // A thousand copies of one model is not a case worth handling well; fall
        // back to something guaranteed unique rather than looping forever.
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    private static bool IsLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only or denied: not "locked" in the open-in-Revit sense, but
            // equally unwritable, so treat it the same way.
            return true;
        }
    }
}
