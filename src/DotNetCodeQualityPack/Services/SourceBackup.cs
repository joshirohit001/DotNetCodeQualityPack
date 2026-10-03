using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DotNetCodeQualityPack.Services
{
    /// <summary>
    /// Backs up the solution's .cs files before an auto-fix so it can be undone without git.
    /// Backups live in %LOCALAPPDATA%\DotNetCodeQualityPack\Backups (outside the solution, so they are never compiled).
    /// Only files the auto-fix actually changed are kept.
    /// </summary>
    internal static class SourceBackup
    {
        private const string ManifestFileName = "manifest.txt";
        private const string FilesFolderName = "files";

        private static readonly HashSet<string> ExcludedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".vs", ".git", "node_modules", "packages", "TestResults",
        };

        /// <summary>Copies every .cs file under the solution folder into a new backup folder. Returns relative path -> hash.</summary>
        public static Dictionary<string, string> Take(string solutionDirectory, string backupFolder)
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string filesRoot = Path.Combine(backupFolder, FilesFolderName);

            foreach (string file in EnumerateSourceFiles(solutionDirectory))
            {
                string relative = PathHelper.Relative(file, solutionDirectory);
                string target = Path.Combine(filesRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
                hashes[relative] = Hash(file);
            }

            return hashes;
        }

        /// <summary>
        /// Compares the solution with the pre-fix hashes, keeps backups only for changed files, and writes the manifest.
        /// Returns the changed files (relative paths). Deletes the backup folder when nothing changed.
        /// </summary>
        public static List<string> Complete(string solutionDirectory, string backupFolder, Dictionary<string, string> before)
        {
            var changed = new List<string>();
            var manifest = new StringBuilder();
            string filesRoot = Path.Combine(backupFolder, FilesFolderName);

            foreach (KeyValuePair<string, string> entry in before)
            {
                string current = Path.Combine(solutionDirectory, entry.Key);
                string after = File.Exists(current) ? Hash(current) : string.Empty;

                if (string.Equals(after, entry.Value, StringComparison.Ordinal))
                {
                    File.Delete(Path.Combine(filesRoot, entry.Key));
                    continue;
                }

                changed.Add(entry.Key);
                manifest.Append(entry.Key).Append('\t').Append(after).Append("\r\n");
            }

            if (changed.Count == 0)
            {
                TryDeleteDirectory(backupFolder);
                return changed;
            }

            RemoveEmptyDirectories(filesRoot);
            File.WriteAllText(Path.Combine(backupFolder, ManifestFileName), manifest.ToString(), Encoding.UTF8);
            changed.Sort(StringComparer.OrdinalIgnoreCase);
            return changed;
        }

        public static string NewBackupFolder(string solutionPath)
        {
            string folder = Path.Combine(RootFor(solutionPath), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>The most recent completed backup for this solution, or null.</summary>
        public static string FindLatest(string solutionPath)
        {
            string root = RootFor(solutionPath);
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory.GetDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, ManifestFileName)))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .FirstOrDefault();
        }

        public static int CountFiles(string backupFolder)
        {
            return File.ReadAllLines(Path.Combine(backupFolder, ManifestFileName)).Count(l => l.Length > 0);
        }

        /// <summary>
        /// Restores the backed-up files. A file edited again after the auto-fix is skipped, so later work is never lost.
        /// The backup is deleted when everything was restored.
        /// </summary>
        public static void Restore(string solutionDirectory, string backupFolder, List<string> restored, List<string> skipped)
        {
            string filesRoot = Path.Combine(backupFolder, FilesFolderName);

            foreach (string line in File.ReadAllLines(Path.Combine(backupFolder, ManifestFileName)))
            {
                string[] parts = line.Split('\t');
                if (parts.Length != 2)
                {
                    continue;
                }

                string relative = parts[0];
                string expectedHash = parts[1];
                string current = Path.Combine(solutionDirectory, relative);
                string currentHash = File.Exists(current) ? Hash(current) : string.Empty;

                if (!string.Equals(currentHash, expectedHash, StringComparison.Ordinal))
                {
                    skipped.Add(relative);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(current));
                File.Copy(Path.Combine(filesRoot, relative), current, true);
                restored.Add(relative);
            }

            if (skipped.Count == 0)
            {
                TryDeleteDirectory(backupFolder);
            }
            else
            {
                // Keep the backup for the skipped files, but don't offer this undo again.
                File.Move(Path.Combine(backupFolder, ManifestFileName), Path.Combine(backupFolder, ManifestFileName + ".used"));
            }
        }

        private static string RootFor(string solutionPath)
        {
            string fullPath = Path.GetFullPath(solutionPath);
            string pathHash = Hash(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant())).Substring(0, 8);
            string name = Path.GetFileNameWithoutExtension(fullPath) + "-" + pathHash;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DotNetCodeQualityPack",
                "Backups",
                name);
        }

        private static IEnumerable<string> EnumerateSourceFiles(string directory)
        {
            var pending = new Stack<string>();
            pending.Push(directory);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                foreach (string file in Directory.EnumerateFiles(current, "*.cs"))
                {
                    yield return file;
                }

                foreach (string sub in Directory.EnumerateDirectories(current))
                {
                    if (!ExcludedFolders.Contains(Path.GetFileName(sub)))
                    {
                        pending.Push(sub);
                    }
                }
            }
        }

        private static string Hash(string file)
        {
            return Hash(File.ReadAllBytes(file));
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
            }
        }

        private static void RemoveEmptyDirectories(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (string sub in Directory.GetDirectories(directory))
            {
                RemoveEmptyDirectories(sub);
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch (IOException)
            {
                // Best effort; a leftover folder without a manifest is ignored.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
