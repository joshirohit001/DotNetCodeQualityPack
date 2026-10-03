using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace DotNetCodeQualityPack.Fixer
{
    /// <summary>
    /// Finds warnings, computes fixes as previews (nothing is changed until Apply), applies approved previews
    /// to the Visual Studio workspace, and undoes them.
    /// </summary>
    internal sealed class FixEngine
    {
        // Safety limit when a fix has to be applied one occurrence at a time.
        private const int MaxSequentialFixes = 200;

        private readonly AnalyzerHost _host = new AnalyzerHost();
        private readonly Stack<List<FileChange>> _undo = new Stack<List<FileChange>>();

        public bool CanUndo => _undo.Count > 0;

        private sealed class FixCandidate
        {
            public CodeAction Action { get; set; }

            public CodeFixProvider Provider { get; set; }

            public Solution ChangedSolution { get; set; }
        }

        // ------------------------------------------------------------------ scanning

        public async Task<List<IssueInfo>> ScanSolutionAsync(Solution solution, IProgress<string> progress, CancellationToken cancellationToken)
        {
            var issues = new List<IssueInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<Project> projects = solution.Projects.Where(p => p.Language == LanguageNames.CSharp).ToList();

            for (int i = 0; i < projects.Count; i++)
            {
                Project project = projects[i];
                progress?.Report(string.Format("Analyzing {0} ({1} of {2})...", project.Name, i + 1, projects.Count));

                List<Diagnostic> diagnostics = await _host.AnalyzeProjectAsync(project, cancellationToken).ConfigureAwait(false);

                foreach (IGrouping<SyntaxTree, Diagnostic> byTree in diagnostics.GroupBy(d => d.Location.SourceTree))
                {
                    Document document = byTree.Key == null ? null : solution.GetDocument(byTree.Key);
                    if (document == null)
                    {
                        continue;
                    }

                    SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    foreach (Diagnostic diagnostic in byTree)
                    {
                        IssueInfo issue = _host.ToIssue(diagnostic, document, text);

                        // Multi-targeted projects report the same issue once per target framework.
                        if (seen.Add(issue.Key + "|" + issue.Span.Start))
                        {
                            issues.Add(issue);
                        }
                    }
                }
            }

            return issues;
        }

        public async Task<List<IssueInfo>> ScanDocumentAsync(Solution solution, DocumentId documentId, CancellationToken cancellationToken)
        {
            Document document = solution.GetDocument(documentId);
            if (document == null)
            {
                return new List<IssueInfo>();
            }

            SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            List<Diagnostic> diagnostics = await _host.AnalyzeDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            return diagnostics.Select(d => _host.ToIssue(d, document, text)).ToList();
        }

        /// <summary>Code around the issue with the issue line highlighted (for warnings without an automatic fix).</summary>
        public async Task<List<DiffLine>> GetSnippetAsync(Solution solution, IssueInfo issue, CancellationToken cancellationToken)
        {
            Document document = solution.GetDocument(issue.DocumentId);
            if (document == null)
            {
                return new List<DiffLine>();
            }

            SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            return LineDiff.Snippet(text.ToString(), issue.Line);
        }

        // ------------------------------------------------------------------ previews

        /// <summary>Preview of the fix for one warning.</summary>
        public async Task<FixPreview> PreviewIssueAsync(Solution solution, IssueInfo issue, CancellationToken cancellationToken)
        {
            Document document = solution.GetDocument(issue.DocumentId);
            if (document == null)
            {
                return new FixPreview { Problem = "The file is no longer part of the solution." };
            }

            Diagnostic diagnostic = await RelocateAsync(document, issue, cancellationToken).ConfigureAwait(false);
            if (diagnostic == null)
            {
                return new FixPreview { Problem = "This warning is gone. It may already have been fixed." };
            }

            FixCandidate candidate = await FindFixAsync(document, diagnostic, cancellationToken).ConfigureAwait(false);
            if (candidate == null)
            {
                return new FixPreview { Problem = "No automatic fix is available for this warning. Use Go to code to fix it by hand." };
            }

            return await BuildPreviewAsync(solution, candidate.ChangedSolution, candidate.Action.Title, 1, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Preview of fixing every occurrence of the given rules in one file (one "page").</summary>
        public async Task<FixPreview> PreviewDocumentAsync(Solution solution, DocumentId documentId, IReadOnlyList<string> ruleIds, CancellationToken cancellationToken)
        {
            Solution current = solution;
            int fixes = 0;

            foreach (string ruleId in ruleIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FixOutcome outcome = await FixRuleInDocumentAsync(current, documentId, ruleId, cancellationToken).ConfigureAwait(false);
                current = outcome.Solution;
                fixes += outcome.Fixed;
            }

            if (fixes == 0)
            {
                return new FixPreview { Problem = "None of the ticked rules could be fixed automatically on this page." };
            }

            return await BuildPreviewAsync(solution, current, "Fix ticked rules on this page", fixes, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Preview of fixing one rule in every file that has it.</summary>
        public async Task<FixPreview> PreviewRuleAsync(
            Solution solution,
            string ruleId,
            IReadOnlyList<DocumentId> documentIds,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            Solution current = solution;
            int fixes = 0;

            for (int i = 0; i < documentIds.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(string.Format("Fixing {0} in file {1} of {2}...", ruleId, i + 1, documentIds.Count));

                FixOutcome outcome = await FixRuleInDocumentAsync(current, documentIds[i], ruleId, cancellationToken).ConfigureAwait(false);
                current = outcome.Solution;
                fixes += outcome.Fixed;
            }

            if (fixes == 0)
            {
                return new FixPreview { Problem = ruleId + " couldn't be fixed automatically in any file." };
            }

            return await BuildPreviewAsync(solution, current, "Fix " + ruleId + " in the whole solution", fixes, cancellationToken).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ apply / undo

        /// <summary>
        /// Builds the solution to commit. Each file is only changed if it still has the exact text the preview was
        /// made from (or, for undo, the text the fix produced), so edits made in the meantime are never overwritten.
        /// Safe to call on a background thread.
        /// </summary>
        public async Task<PreparedChange> PrepareAsync(Solution current, IReadOnlyList<FileChange> changes, bool revert, CancellationToken cancellationToken)
        {
            var prepared = new PreparedChange { Solution = current, IsUndo = revert };

            foreach (FileChange change in changes)
            {
                SourceText expected = revert ? change.NewText : change.OldText;
                SourceText replacement = revert ? change.OldText : change.NewText;

                Document document = prepared.Solution.GetDocument(change.DocumentId);
                SourceText text = document == null ? null : await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

                if (text == null || !text.ContentEquals(expected))
                {
                    prepared.Result.Conflicts.Add(change.FilePath);
                    continue;
                }

                prepared.Solution = prepared.Solution.WithDocumentText(change.DocumentId, replacement);
                prepared.Result.Applied.Add(change);
            }

            return prepared;
        }

        /// <summary>Writes a prepared change into Visual Studio. Must be called on the UI thread.</summary>
        public bool Commit(Workspace workspace, PreparedChange prepared)
        {
            if (prepared.Result.Applied.Count == 0)
            {
                return false;
            }

            prepared.Result.Succeeded = workspace.TryApplyChanges(prepared.Solution);
            if (prepared.Result.Succeeded)
            {
                if (prepared.IsUndo)
                {
                    _undo.Pop();
                }
                else
                {
                    _undo.Push(prepared.Result.Applied);
                }
            }

            return prepared.Result.Succeeded;
        }

        /// <summary>The changes the next undo would revert, or null.</summary>
        public IReadOnlyList<FileChange> PeekUndo()
        {
            return _undo.Count > 0 ? _undo.Peek() : null;
        }

        public void ForgetHistory()
        {
            _undo.Clear();
        }

        // ------------------------------------------------------------------ internals

        private sealed class FixOutcome
        {
            public Solution Solution { get; set; }

            public int Fixed { get; set; }
        }

        private async Task<FixOutcome> FixRuleInDocumentAsync(Solution solution, DocumentId documentId, string ruleId, CancellationToken cancellationToken)
        {
            var outcome = new FixOutcome { Solution = solution };
            Document document = solution.GetDocument(documentId);
            if (document == null)
            {
                return outcome;
            }

            List<Diagnostic> before = (await _host.AnalyzeDocumentAsync(document, cancellationToken).ConfigureAwait(false))
                .Where(d => d.Id == ruleId)
                .ToList();
            if (before.Count == 0)
            {
                return outcome;
            }

            FixCandidate first = await FindFixAsync(document, before[0], cancellationToken).ConfigureAwait(false);
            if (first == null)
            {
                return outcome;
            }

            // Preferred: the analyzer's own "fix all in document", which fixes every occurrence in one pass.
            Solution fixedSolution = await TryFixAllAsync(document, first, ruleId, cancellationToken).ConfigureAwait(false)
                ?? await FixOneByOneAsync(solution, documentId, ruleId, cancellationToken).ConfigureAwait(false);

            if (fixedSolution == null)
            {
                return outcome;
            }

            Document after = fixedSolution.GetDocument(documentId);
            int remaining = after == null
                ? 0
                : (await _host.AnalyzeDocumentAsync(after, cancellationToken).ConfigureAwait(false)).Count(d => d.Id == ruleId);

            outcome.Solution = fixedSolution;
            outcome.Fixed = Math.Max(0, before.Count - remaining);
            return outcome;
        }

        private async Task<Solution> TryFixAllAsync(Document document, FixCandidate candidate, string ruleId, CancellationToken cancellationToken)
        {
            try
            {
                FixAllProvider fixAll = candidate.Provider.GetFixAllProvider();
                if (fixAll == null || !fixAll.GetSupportedFixAllScopes().Contains(FixAllScope.Document))
                {
                    return null;
                }

                var context = new FixAllContext(
                    document,
                    candidate.Provider,
                    FixAllScope.Document,
                    candidate.Action.EquivalenceKey,
                    new[] { ruleId },
                    new HostDiagnosticProvider(_host),
                    cancellationToken);

                CodeAction action = await fixAll.GetFixAsync(context).ConfigureAwait(false);
                return action == null ? null : await GetChangedSolutionAsync(action, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return null;
            }
        }

        private async Task<Solution> FixOneByOneAsync(Solution solution, DocumentId documentId, string ruleId, CancellationToken cancellationToken)
        {
            Solution current = solution;
            bool changed = false;

            for (int i = 0; i < MaxSequentialFixes; i++)
            {
                Document document = current.GetDocument(documentId);
                Diagnostic next = (await _host.AnalyzeDocumentAsync(document, cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(d => d.Id == ruleId);
                if (next == null)
                {
                    break;
                }

                FixCandidate candidate = await FindFixAsync(document, next, cancellationToken).ConfigureAwait(false);
                if (candidate == null)
                {
                    break;
                }

                SourceText before = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                Document updated = candidate.ChangedSolution.GetDocument(documentId);
                SourceText after = updated == null ? null : await updated.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (after == null || after.ContentEquals(before))
                {
                    break; // The fix didn't change this file; stop instead of looping forever.
                }

                current = candidate.ChangedSolution;
                changed = true;
            }

            return changed ? current : null;
        }

        private async Task<FixCandidate> FindFixAsync(Document document, Diagnostic diagnostic, CancellationToken cancellationToken)
        {
            foreach (CodeFixProvider provider in _host.For(document.Project).GetFixers(diagnostic.Id))
            {
                var actions = new List<CodeAction>();
                var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), cancellationToken);

                try
                {
                    await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    continue;
                }

                foreach (CodeAction action in actions)
                {
                    Solution changed = await GetChangedSolutionAsync(action, cancellationToken).ConfigureAwait(false);
                    if (changed != null)
                    {
                        return new FixCandidate { Action = action, Provider = provider, ChangedSolution = changed };
                    }
                }
            }

            return null;
        }

        private static async Task<Solution> GetChangedSolutionAsync(CodeAction action, CancellationToken cancellationToken)
        {
            try
            {
                IEnumerable<CodeActionOperation> operations = await action.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
                return operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return null;
            }
        }

        /// <summary>Finds the issue again in the document's current text (lines may have moved since the scan).</summary>
        private async Task<Diagnostic> RelocateAsync(Document document, IssueInfo issue, CancellationToken cancellationToken)
        {
            List<Diagnostic> current = (await _host.AnalyzeDocumentAsync(document, cancellationToken).ConfigureAwait(false))
                .Where(d => d.Id == issue.Id)
                .ToList();

            return current
                .OrderBy(d => Math.Abs(d.Location.GetLineSpan().StartLinePosition.Line + 1 - issue.Line))
                .ThenBy(d => d.GetMessage() == issue.Message ? 0 : 1)
                .FirstOrDefault();
        }

        private static async Task<FixPreview> BuildPreviewAsync(Solution original, Solution changed, string title, int fixCount, CancellationToken cancellationToken)
        {
            var preview = new FixPreview { Title = title, FixCount = fixCount };
            SolutionChanges changes = changed.GetChanges(original);

            foreach (ProjectChanges projectChanges in changes.GetProjectChanges())
            {
                foreach (DocumentId id in projectChanges.GetChangedDocuments())
                {
                    Document oldDocument = original.GetDocument(id);
                    Document newDocument = changed.GetDocument(id);
                    if (oldDocument == null || newDocument == null)
                    {
                        continue;
                    }

                    SourceText oldText = await oldDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    SourceText newText = await newDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    if (oldText.ContentEquals(newText))
                    {
                        continue;
                    }

                    List<DiffLine> diff = LineDiff.Compute(oldText.ToString(), newText.ToString());
                    LineDiff.Count(diff, out int added, out int removed);

                    preview.Files.Add(new FileChange
                    {
                        DocumentId = id,
                        FilePath = oldDocument.FilePath ?? oldDocument.Name,
                        OldText = oldText,
                        NewText = newText,
                        Diff = diff,
                        Added = added,
                        Removed = removed,
                    });
                }
            }

            if (preview.Files.Count == 0)
            {
                preview.Problem = "The fix didn't change any code.";
            }

            preview.Files.Sort((x, y) => string.Compare(x.FilePath, y.FilePath, StringComparison.OrdinalIgnoreCase));
            return preview;
        }

        /// <summary>Feeds the analyzer's fix-all machinery with diagnostics from our own analysis.</summary>
        private sealed class HostDiagnosticProvider : FixAllContext.DiagnosticProvider
        {
            private readonly AnalyzerHost _host;

            public HostDiagnosticProvider(AnalyzerHost host)
            {
                _host = host;
            }

            public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken)
            {
                return await _host.AnalyzeDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            }

            public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            {
                return Task.FromResult(Enumerable.Empty<Diagnostic>());
            }

            public override async Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            {
                return await _host.AnalyzeProjectAsync(project, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
