using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace DotNetCodeQualityPack.Rules
{
    /// <summary>
    /// File globs relative to the solution folder: ** matches any folders, * any characters except '/', ? one character.
    /// A glob without '/' matches the file name in any folder ("*.cs"); a glob with '/' is matched from the solution folder.
    /// </summary>
    internal static class Glob
    {
        private static readonly ConcurrentDictionary<string, Regex> Cache = new ConcurrentDictionary<string, Regex>();

        public static bool IsMatch(string glob, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(glob) || relativePath == null)
            {
                return false;
            }

            return Cache.GetOrAdd(glob, ToRegex).IsMatch(relativePath.Replace('\\', '/'));
        }

        internal static Regex ToRegex(string glob)
        {
            string g = glob.Trim().Replace('\\', '/');
            if (g.StartsWith("./"))
            {
                g = g.Substring(2);
            }

            if (g.StartsWith("/"))
            {
                g = g.Substring(1);
            }
            else if (!g.Contains("/"))
            {
                g = "**/" + g;
            }

            var sb = new StringBuilder("^");
            for (int i = 0; i < g.Length; i++)
            {
                char c = g[i];
                if (c == '*')
                {
                    bool isDouble = i + 1 < g.Length && g[i + 1] == '*';
                    if (isDouble && i + 2 < g.Length && g[i + 2] == '/')
                    {
                        sb.Append("(?:.*/)?");
                        i += 2;
                    }
                    else if (isDouble)
                    {
                        sb.Append(".*");
                        i += 1;
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }

            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
