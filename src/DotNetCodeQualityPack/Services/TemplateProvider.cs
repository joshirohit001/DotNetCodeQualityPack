using System;
using System.IO;
using System.Reflection;

namespace DotNetCodeQualityPack.Services
{
    /// <summary>Reads the files that get written into the user's solution (embedded in this assembly).</summary>
    internal static class TemplateProvider
    {
        public static string Props => Read("CodeQuality.props");

        public static string Targets => Read("CodeQuality.targets");

        /// <summary>Starter .editorconfig (formatting basics), used only when the solution has none.</summary>
        public static string EditorConfig => Read("editorconfig");

        /// <summary>Rule severities placed in the marked block of .editorconfig.</summary>
        public static string EditorConfigRules => Read("editorconfig-rules");

        /// <summary>Sample Markdown rules file (Team Rules window > New from template).</summary>
        public static string RulesMarkdown => Read("CodeQualityRules.md");

        private static string Read(string name)
        {
            string resourceName = "DotNetCodeQualityPack.Templates." + name;
            Assembly assembly = typeof(TemplateProvider).Assembly;

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("Embedded template not found: " + resourceName);
                }

                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}
