using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DotNetCodeQualityPack.Fixer
{
    /// <summary>
    /// Runs a project's analyzers (Roslynator, SonarAnalyzer, StyleCop, Meziantou, ... whatever the project references)
    /// and finds the code fix providers that ship in the same analyzer assemblies.
    /// </summary>
    internal sealed class AnalyzerHost
    {
        private readonly ConcurrentDictionary<string, ImmutableArray<CodeFixProvider>> _fixersByAssembly =
            new ConcurrentDictionary<string, ImmutableArray<CodeFixProvider>>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<ProjectId, ProjectAnalyzers> _projects =
            new ConcurrentDictionary<ProjectId, ProjectAnalyzers>();

        // Weak keys: sessions go away with their compilation, so old versions of the code aren't kept in memory.
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, CompilationWithAnalyzers> _sessions =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, CompilationWithAnalyzers>();

        internal sealed class ProjectAnalyzers
        {
            public string Key { get; set; }

            public ImmutableArray<DiagnosticAnalyzer> Analyzers { get; set; }

            public Dictionary<string, List<CodeFixProvider>> FixersById { get; set; }

            public IReadOnlyList<CodeFixProvider> GetFixers(string diagnosticId)
            {
                return FixersById.TryGetValue(diagnosticId, out List<CodeFixProvider> list)
                    ? (IReadOnlyList<CodeFixProvider>)list
                    : Array.Empty<CodeFixProvider>();
            }
        }

        public ProjectAnalyzers For(Project project)
        {
            string key = string.Join("|", project.AnalyzerReferences.Select(r => r.FullPath ?? r.Display));

            if (_projects.TryGetValue(project.Id, out ProjectAnalyzers cached) && cached.Key == key)
            {
                return cached;
            }

            var analyzers = new List<DiagnosticAnalyzer>();
            var fixersById = new Dictionary<string, List<CodeFixProvider>>(StringComparer.OrdinalIgnoreCase);

            foreach (AnalyzerReference reference in project.AnalyzerReferences)
            {
                try
                {
                    analyzers.AddRange(reference.GetAnalyzers(project.Language));
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    // An analyzer package that fails to load is skipped, like the compiler does.
                }

                foreach (CodeFixProvider fixer in GetFixers(reference, project.Language))
                {
                    foreach (string id in SafeFixableIds(fixer))
                    {
                        if (!fixersById.TryGetValue(id, out List<CodeFixProvider> list))
                        {
                            list = new List<CodeFixProvider>();
                            fixersById[id] = list;
                        }

                        list.Add(fixer);
                    }
                }
            }

            var result = new ProjectAnalyzers
            {
                Key = key,
                Analyzers = analyzers.ToImmutableArray(),
                FixersById = fixersById,
            };

            _projects[project.Id] = result;
            return result;
        }

        /// <summary>All reportable issues in one document, using the current text of that document.</summary>
        public async Task<List<Diagnostic>> AnalyzeDocumentAsync(Document document, CancellationToken cancellationToken)
        {
            SyntaxTree tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            Compilation compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (tree == null || compilation == null)
            {
                return new List<Diagnostic>();
            }

            var diagnostics = new List<Diagnostic>();
            diagnostics.AddRange(compilation.GetSemanticModel(tree).GetDiagnostics(null, cancellationToken));

            ProjectAnalyzers project = For(document.Project);
            if (project.Analyzers.Length > 0)
            {
                CompilationWithAnalyzers withAnalyzers = GetOrCreate(compilation, project, document.Project);
                SemanticModel model = withAnalyzers.Compilation.GetSemanticModel(tree);

                diagnostics.AddRange(await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken).ConfigureAwait(false));
                diagnostics.AddRange(await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, null, cancellationToken).ConfigureAwait(false));
            }

            return Filter(diagnostics).Where(d => d.Location.SourceTree == tree).ToList();
        }

        /// <summary>All reportable issues in a project.</summary>
        public async Task<List<Diagnostic>> AnalyzeProjectAsync(Project project, CancellationToken cancellationToken)
        {
            Compilation compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation == null)
            {
                return new List<Diagnostic>();
            }

            var diagnostics = new List<Diagnostic>(compilation.GetDiagnostics(cancellationToken));

            ProjectAnalyzers analyzers = For(project);
            if (analyzers.Analyzers.Length > 0)
            {
                CompilationWithAnalyzers withAnalyzers = GetOrCreate(compilation, analyzers, project);
                diagnostics.AddRange(await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false));
            }

            return Filter(diagnostics).ToList();
        }

        /// <summary>
        /// Reuses one analyzer session per compilation. An unchanged project keeps the same Compilation object,
        /// so clicking through issues in a file doesn't re-run every analyzer each time; an edit creates a new
        /// Compilation and therefore a fresh session.
        /// </summary>
        private CompilationWithAnalyzers GetOrCreate(Compilation compilation, ProjectAnalyzers analyzers, Project project)
        {
            lock (_sessions)
            {
                if (_sessions.TryGetValue(compilation, out CompilationWithAnalyzers existing) && existing.Analyzers == analyzers.Analyzers)
                {
                    return existing;
                }

                CompilationWithAnalyzers created = compilation.WithAnalyzers(analyzers.Analyzers, Options(project));
                _sessions.Remove(compilation);
                _sessions.Add(compilation, created);
                return created;
            }
        }

        public static IssueSeverity ToSeverity(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: return IssueSeverity.Error;
                case DiagnosticSeverity.Warning: return IssueSeverity.Warning;
                default: return IssueSeverity.Message;
            }
        }

        public IssueInfo ToIssue(Diagnostic diagnostic, Document document, SourceText text)
        {
            FileLinePositionSpan position = diagnostic.Location.GetLineSpan();
            int lineIndex = Math.Min(position.StartLinePosition.Line, text.Lines.Count - 1);

            return new IssueInfo
            {
                Id = diagnostic.Id,
                Message = diagnostic.GetMessage(),
                RuleTitle = diagnostic.Descriptor.Title.ToString(),
                Severity = ToSeverity(diagnostic.Severity),
                FilePath = document.FilePath ?? document.Name,
                DocumentId = document.Id,
                Line = lineIndex + 1,
                LineText = lineIndex >= 0 ? text.Lines[lineIndex].ToString().Trim() : string.Empty,
                Span = diagnostic.Location.SourceSpan,
                HasFix = For(document.Project).GetFixers(diagnostic.Id).Count > 0,
                HelpLink = diagnostic.Descriptor.HelpLinkUri,
            };
        }

        private static IEnumerable<Diagnostic> Filter(IEnumerable<Diagnostic> diagnostics)
        {
            var seen = new HashSet<string>();

            foreach (Diagnostic d in diagnostics)
            {
                if (d.IsSuppressed || d.Severity == DiagnosticSeverity.Hidden || !d.Location.IsInSource)
                {
                    continue;
                }

                // Compiler errors are build breaks, not code quality; AD0001 is an analyzer crash.
                bool isCompiler = d.Id.StartsWith("CS", StringComparison.Ordinal);
                if ((isCompiler && d.Severity == DiagnosticSeverity.Error) || d.Id == "AD0001")
                {
                    continue;
                }

                string key = d.Id + "|" + d.Location.SourceTree?.FilePath + "|" + d.Location.SourceSpan;
                if (seen.Add(key))
                {
                    yield return d;
                }
            }
        }

        private static CompilationWithAnalyzersOptions Options(Project project)
        {
            return new CompilationWithAnalyzersOptions(
                project.AnalyzerOptions,
                onAnalyzerException: null,
                concurrentAnalysis: true,
                logAnalyzerExecutionTime: false,
                reportSuppressedDiagnostics: false);
        }

        private IEnumerable<CodeFixProvider> GetFixers(AnalyzerReference reference, string language)
        {
            string path = reference.FullPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return Array.Empty<CodeFixProvider>();
            }

            return _fixersByAssembly.GetOrAdd(path + "|" + language, _ => LoadFixers(reference, path, language));
        }

        private static ImmutableArray<CodeFixProvider> LoadFixers(AnalyzerReference reference, string path, string language)
        {
            Assembly assembly;
            try
            {
                assembly = reference is AnalyzerFileReference fileReference
                    ? fileReference.GetAssembly()
                    : Assembly.LoadFrom(path);
            }
            catch (Exception)
            {
                return ImmutableArray<CodeFixProvider>.Empty;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }

            ImmutableArray<CodeFixProvider>.Builder fixers = ImmutableArray.CreateBuilder<CodeFixProvider>();

            foreach (Type type in types)
            {
                try
                {
                    if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
                    {
                        continue;
                    }

                    ExportCodeFixProviderAttribute export = type.GetCustomAttribute<ExportCodeFixProviderAttribute>();
                    if (export == null || !export.Languages.Contains(language))
                    {
                        continue;
                    }

                    fixers.Add((CodeFixProvider)Activator.CreateInstance(type));
                }
                catch (Exception)
                {
                    // A fixer that can't be created is skipped; its warnings show as "manual".
                }
            }

            return fixers.ToImmutable();
        }

        private static IEnumerable<string> SafeFixableIds(CodeFixProvider fixer)
        {
            try
            {
                return fixer.FixableDiagnosticIds;
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }
    }
}
