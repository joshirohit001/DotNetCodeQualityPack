using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DotNetCodeQualityPack.Services
{
    /// <summary>Runs "dotnet format" on a solution to apply every automatic fix (formatting, code style and analyzer code fixes).</summary>
    internal static class DotnetFormatRunner
    {
        public const string ReportFileName = "format-report.json";

        private static readonly Regex DiagnosticIdPattern = new Regex(@"""DiagnosticId""\s*:\s*""([^""]+)""");

        /// <summary>Full path to dotnet.exe, or null when the .NET SDK isn't installed.</summary>
        public static string FindDotnet()
        {
            string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string directory in pathVariable.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim().Trim('"'), "dotnet.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry.
                }
            }

            string programFiles = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            return File.Exists(programFiles) ? programFiles : null;
        }

        /// <summary>
        /// Starts dotnet format. Output lines are queued to <paramref name="output"/> as they arrive.
        /// Returns the process exit code (0 = success).
        /// </summary>
        public static Task<int> RunAsync(string dotnetPath, string solutionPath, string reportDirectory, ConcurrentQueue<string> output)
        {
            string arguments = string.Format(
                "format \"{0}\" --severity info --verbosity normal --report \"{1}\"",
                solutionPath,
                reportDirectory);

            output.Enqueue("> dotnet " + arguments);

            var startInfo = new ProcessStartInfo(dotnetPath, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(solutionPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // Keep the CLI from printing its first-run banner into the log.
            startInfo.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
            startInfo.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

            return Task.Run(() =>
            {
                using (var process = new Process { StartInfo = startInfo })
                {
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) { output.Enqueue(e.Data); } };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) { output.Enqueue(e.Data); } };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();

                    return process.ExitCode;
                }
            });
        }

        /// <summary>Counts applied fixes per rule ID from dotnet format's JSON report (empty when there is no report).</summary>
        public static List<KeyValuePair<string, int>> ReadFixesByRule(string reportDirectory)
        {
            string reportPath = Path.Combine(reportDirectory, ReportFileName);
            if (!File.Exists(reportPath))
            {
                return new List<KeyValuePair<string, int>>();
            }

            return DiagnosticIdPattern.Matches(File.ReadAllText(reportPath))
                .Cast<Match>()
                .GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
                .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
