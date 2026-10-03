using System;
using System.Collections.Generic;
using System.IO;
using Community.VisualStudio.Toolkit;
using DotNetCodeQualityPack.Services;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;
using TaskScheduler = System.Threading.Tasks.TaskScheduler;

namespace DotNetCodeQualityPack.Commands
{
    /// <summary>Tools > .NET Code Quality > Undo Last Auto-Fix: restores the files the last auto-fix changed.</summary>
    [Command(PackageGuids.DotNetCodeQualityPackString, PackageIds.UndoAutoFixCommand)]
    internal sealed class UndoAutoFixCommand : BaseCommand<UndoAutoFixCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            try
            {
                SolutionSnapshot snapshot = await CodeQualityWorkflow.TryGetSnapshotAsync();
                if (snapshot == null)
                {
                    return;
                }

                string backup = SourceBackup.FindLatest(snapshot.SolutionPath);
                if (backup == null)
                {
                    await VS.MessageBox.ShowAsync(Vsix.Name, "There is no auto-fix to undo for this solution.");
                    return;
                }

                int fileCount = SourceBackup.CountFiles(backup);
                bool proceed = await VS.MessageBox.ShowConfirmAsync(
                    Vsix.Name,
                    string.Format(
                        "Restore {0} file(s) to how they were before the auto-fix of {1}?\n\n"
                        + "Files you have edited since then are skipped, so your later work is kept.",
                        fileCount,
                        FormatBackupTime(backup)));

                if (!proceed)
                {
                    return;
                }

                await VS.Commands.ExecuteAsync("File.SaveAll");

                await TaskScheduler.Default;
                var restored = new List<string>();
                var skipped = new List<string>();
                SourceBackup.Restore(snapshot.SolutionDirectory, backup, restored, skipped);

                OutputWindowPane pane = await CodeQualityWorkflow.GetPaneAsync();
                await pane.WriteLineAsync(string.Format("Undo last auto-fix: restored {0} file(s).", restored.Count));
                foreach (string file in skipped)
                {
                    await pane.WriteLineAsync("  Skipped (edited after the auto-fix): " + file);
                }

                await pane.ActivateAsync();

                await VS.MessageBox.ShowAsync(
                    Vsix.Name,
                    skipped.Count == 0
                        ? string.Format("Restored {0} file(s).", restored.Count)
                        : string.Format(
                            "Restored {0} file(s). Skipped {1} file(s) you edited after the auto-fix (listed in the Output window).",
                            restored.Count,
                            skipped.Count));
            }
            catch (Exception ex)
            {
                await CodeQualityWorkflow.ShowErrorAsync(ex);
            }
        }

        private static string FormatBackupTime(string backupFolder)
        {
            DateTime time;
            return DateTime.TryParseExact(
                    Path.GetFileName(backupFolder),
                    "yyyyMMdd-HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out time)
                ? time.ToString("g")
                : Path.GetFileName(backupFolder);
        }
    }
}
