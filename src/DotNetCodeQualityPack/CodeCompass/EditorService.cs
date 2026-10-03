using System;
using System.IO;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace DotNetCodeQualityPack.CodeCompass
{
    internal sealed class EditorSnapshot
    {
        public string Text { get; set; }
        public string FilePath { get; set; }
        public int StartLine { get; set; }
        public bool IsSelection { get; set; }

        public string FileName
        {
            get { return string.IsNullOrEmpty(FilePath) ? null : Path.GetFileName(FilePath); }
        }
    }

    /// <summary>Reads from and writes to the Visual Studio editor. All members must run on the UI thread.</summary>
    internal static class EditorService
    {
        private static DTE2 Dte
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE2;
            }
        }

        public static string GetSolutionPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var path = Dte?.Solution?.FullName;
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Where chats and rules belong: the open .sln and its folder; with File > Open Folder (no .sln) the folder;
        /// with only loose files open, the active file's folder (or its repository root).
        /// </summary>
        public static void GetContext(out string solutionPath, out string rootFolder)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            solutionPath = null;
            rootFolder = null;

            string full = GetSolutionPath();
            if (!string.IsNullOrEmpty(full))
            {
                if (File.Exists(full) && full.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    solutionPath = full;
                    rootFolder = Path.GetDirectoryName(full);
                    return;
                }

                if (Directory.Exists(full))
                {
                    rootFolder = full.TrimEnd('\\', '/');
                    return;
                }

                if (File.Exists(full))
                {
                    rootFolder = Path.GetDirectoryName(full);
                    return;
                }
            }

            string active = null;
            try
            {
                active = Dte?.ActiveDocument?.FullName;
            }
            catch (Exception)
            {
                // No active document.
            }

            if (string.IsNullOrEmpty(active) || !File.Exists(active))
            {
                return;
            }

            // Prefer the repository root so .github/instructions and docs/ are found.
            var dir = new DirectoryInfo(Path.GetDirectoryName(active));
            for (var d = dir; d != null; d = d.Parent)
            {
                if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                {
                    rootFolder = d.FullName;
                    return;
                }
            }

            rootFolder = dir.FullName;
        }

        /// <summary>The selected text, or null when nothing is selected.</summary>
        public static EditorSnapshot GetSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var doc = Dte?.ActiveDocument;
            var selection = doc?.Selection as TextSelection;
            if (selection == null || selection.IsEmpty || string.IsNullOrWhiteSpace(selection.Text)) return null;

            return new EditorSnapshot
            {
                Text = selection.Text,
                FilePath = doc.FullName,
                StartLine = Math.Min(selection.TopPoint.Line, selection.BottomPoint.Line),
                IsSelection = true
            };
        }

        public static EditorSnapshot GetActiveDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var doc = Dte?.ActiveDocument;
            var textDoc = doc?.Object("TextDocument") as TextDocument;
            if (textDoc == null) return null;

            var text = textDoc.StartPoint.CreateEditPoint().GetText(textDoc.EndPoint);
            return new EditorSnapshot
            {
                Text = text,
                FilePath = doc.FullName,
                StartLine = 1,
                IsSelection = false
            };
        }

        public static bool GoToLine(string filePath, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(filePath) || line <= 0) return false;
            try
            {
                var window = Dte.ItemOperations.OpenFile(filePath);
                window?.Activate();
                var selection = Dte.ActiveDocument?.Selection as TextSelection;
                if (selection == null) return false;
                selection.GotoLine(line, true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Replaces the current selection (or inserts at the caret) as one undoable step.</summary>
        public static bool InsertOrReplaceSelection(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = Dte;
            var selection = dte?.ActiveDocument?.Selection as TextSelection;
            if (selection == null) return false;

            var openedUndo = false;
            try
            {
                if (!dte.UndoContext.IsOpen)
                {
                    dte.UndoContext.Open("Code Compass: apply suggestion");
                    openedUndo = true;
                }

                selection.Insert(text, (int)vsInsertFlags.vsInsertFlagsContainNewText);
                selection.SmartFormat();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (openedUndo) dte.UndoContext.Close();
            }
        }
    }
}
