using System;
using System.IO;

namespace DotNetCodeQualityPack.Services
{
    internal static class PathHelper
    {
        /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> itself or anywhere below it.</summary>
        public static bool IsUnder(string path, string directory)
        {
            string fullPath = Path.GetFullPath(path);
            string fullDirectory = WithTrailingSeparator(Path.GetFullPath(directory));
            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase)
                || string.Equals(WithTrailingSeparator(fullPath), fullDirectory, StringComparison.OrdinalIgnoreCase);
        }

        public static bool SameDirectory(string a, string b)
        {
            return string.Equals(
                WithTrailingSeparator(Path.GetFullPath(a)),
                WithTrailingSeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Path relative to <paramref name="baseDirectory"/>, or the full path when it isn't below it.</summary>
        public static string Relative(string path, string baseDirectory)
        {
            string fullBase = WithTrailingSeparator(Path.GetFullPath(baseDirectory));
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(fullBase.Length)
                : fullPath;
        }

        private static string WithTrailingSeparator(string path)
        {
            char last = path[path.Length - 1];
            return last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar
                ? path
                : path + Path.DirectorySeparatorChar;
        }
    }
}
