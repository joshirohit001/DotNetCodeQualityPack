using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DotNetCodeQualityPack.Ai;

namespace DotNetCodeQualityPack.Services
{
    /// <summary>
    /// Per-user settings in %LOCALAPPDATA%\DotNetCodeQualityPack\settings.json: the selected model, review options,
    /// rules files and token counters. AI provider URLs and API keys are kept separately in <see cref="AiProviderSettings"/>.
    /// </summary>
    internal sealed class PackSettings
    {
        public string SelectedModel { get; set; }

        // Provider settings from versions before 2.0, kept in settings.json until they were moved to ai-providers.json.
        private static readonly string[] LegacyKeys = { "openAIKey", "anthropicKey", "openAIBaseUrl", "openAIModels", "anthropicModels" };
        private readonly Dictionary<string, object> _legacy = new Dictionary<string, object>();

        /// <summary>How many lines are sent to the model per request.</summary>
        public int ChunkLines { get; set; } = 150;

        public int TimeoutSeconds { get; set; } = 300;

        /// <summary>Context window requested from the model (Ollama num_ctx).</summary>
        public int ContextSize { get; set; } = 8192;

        /// <summary>How many guidelines are sent per request; more rules are split over several requests.</summary>
        public int GuidelinesPerRequest { get; set; } = 40;

        /// <summary>Use .github/instructions/*.instructions.md in AI review.</summary>
        public bool UseCopilotInstructions { get; set; } = true;

        /// <summary>Instruction files (by name, e.g. "wpf") switched off for AI review.</summary>
        public HashSet<string> DisabledInstructionFiles { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Solution path -> rules .md path chosen by the user.</summary>
        public Dictionary<string, string> RulesFiles { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ---- AI token usage (counts only; no code, prompts or keys are stored) ----

        /// <summary>Local date (yyyy-MM-dd) the "today" counters belong to.</summary>
        public string UsageDay { get; private set; }

        public long DayInput { get; private set; }

        public long DayOutput { get; private set; }

        public int DayRequests { get; private set; }

        public long AllInput { get; private set; }

        public long AllOutput { get; private set; }

        public int AllRequests { get; private set; }

        public int AllEstimatedRequests { get; private set; }

        /// <summary>Local date the all-time counters started (first review, or the last reset).</summary>
        public string UsageSince { get; private set; }

        /// <summary>Model name -> all-time [input, output, requests].</summary>
        public Dictionary<string, long[]> ModelUsage { get; } = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);

        private static string Today => DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        public bool UsageIsToday => UsageDay == Today;

        public long TodayInput => UsageIsToday ? DayInput : 0;

        public long TodayOutput => UsageIsToday ? DayOutput : 0;

        public int TodayRequests => UsageIsToday ? DayRequests : 0;

        public void RecordUsage(string modelName, Ai.TokenUsage usage)
        {
            if (usage == null || usage.IsEmpty)
            {
                return;
            }

            if (!UsageIsToday)
            {
                UsageDay = Today;
                DayInput = DayOutput = 0;
                DayRequests = 0;
            }

            if (string.IsNullOrEmpty(UsageSince))
            {
                UsageSince = Today;
            }

            DayInput += usage.Input;
            DayOutput += usage.Output;
            DayRequests += usage.Requests;
            AllInput += usage.Input;
            AllOutput += usage.Output;
            AllRequests += usage.Requests;
            AllEstimatedRequests += usage.EstimatedRequests;

            string key = string.IsNullOrWhiteSpace(modelName) ? "(unknown model)" : modelName.Trim();
            if (!ModelUsage.TryGetValue(key, out long[] counts))
            {
                counts = new long[3];
                ModelUsage[key] = counts;
            }

            counts[0] += usage.Input;
            counts[1] += usage.Output;
            counts[2] += usage.Requests;
            Save();
        }

        public void ResetUsage()
        {
            UsageDay = Today;
            UsageSince = Today;
            DayInput = DayOutput = AllInput = AllOutput = 0;
            DayRequests = AllRequests = AllEstimatedRequests = 0;
            ModelUsage.Clear();
            Save();
        }

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DotNetCodeQualityPack",
            "settings.json");

        public static PackSettings Load()
        {
            var settings = new PackSettings();

            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return settings;
                }

                object root = MiniJson.Parse(File.ReadAllText(SettingsPath));
                // Earlier versions kept OpenAI / Anthropic keys here; move them to the AI provider settings once.
                if (!AiProviderSettings.ImportLegacy(root))
                {
                    foreach (string key in LegacyKeys)
                    {
                        if (MiniJson.Get(root, key) is object value)
                        {
                            settings._legacy[key] = value;
                        }
                    }
                }

                settings.SelectedModel = MiniJson.GetString(root, "selectedModel");
                settings.ChunkLines = MiniJson.AsInt(MiniJson.Get(root, "chunkLines")) ?? settings.ChunkLines;
                settings.TimeoutSeconds = MiniJson.AsInt(MiniJson.Get(root, "timeoutSeconds")) ?? settings.TimeoutSeconds;
                settings.ContextSize = MiniJson.AsInt(MiniJson.Get(root, "contextSize")) ?? settings.ContextSize;
                settings.GuidelinesPerRequest = MiniJson.AsInt(MiniJson.Get(root, "guidelinesPerRequest")) ?? settings.GuidelinesPerRequest;

                if (MiniJson.Get(root, "useCopilotInstructions") is bool useCopilot)
                {
                    settings.UseCopilotInstructions = useCopilot;
                }

                if (MiniJson.Get(root, "disabledInstructionFiles") is List<object> disabled)
                {
                    foreach (object name in disabled)
                    {
                        string value = MiniJson.AsString(name);
                        if (!string.IsNullOrEmpty(value))
                        {
                            settings.DisabledInstructionFiles.Add(value);
                        }
                    }
                }

                if (MiniJson.Get(root, "usage") is Dictionary<string, object> usage)
                {
                    settings.UsageDay = MiniJson.GetString(usage, "day");
                    settings.UsageSince = MiniJson.GetString(usage, "since");
                    settings.DayInput = Long(usage, "dayInput");
                    settings.DayOutput = Long(usage, "dayOutput");
                    settings.DayRequests = (int)Long(usage, "dayRequests");
                    settings.AllInput = Long(usage, "input");
                    settings.AllOutput = Long(usage, "output");
                    settings.AllRequests = (int)Long(usage, "requests");
                    settings.AllEstimatedRequests = (int)Long(usage, "estimatedRequests");
                    if (MiniJson.Get(usage, "models") is Dictionary<string, object> models)
                    {
                        foreach (KeyValuePair<string, object> pair in models)
                        {
                            if (pair.Value is List<object> list && list.Count >= 3)
                            {
                                settings.ModelUsage[pair.Key] = new[] { AsLong(list[0]), AsLong(list[1]), AsLong(list[2]) };
                            }
                        }
                    }
                }

                if (MiniJson.Get(root, "rulesFiles") is Dictionary<string, object> files)
                {
                    foreach (KeyValuePair<string, object> pair in files)
                    {
                        string value = MiniJson.AsString(pair.Value);
                        if (!string.IsNullOrEmpty(value))
                        {
                            settings.RulesFiles[pair.Key] = value;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                // Unreadable settings fall back to defaults.
            }

            return settings;
        }

        public void Save()
        {
            try
            {
                var files = new Dictionary<string, object>();
                foreach (KeyValuePair<string, string> pair in RulesFiles)
                {
                    files[pair.Key] = pair.Value;
                }

                var root = new Dictionary<string, object>
                {
                    ["selectedModel"] = SelectedModel,
                    ["chunkLines"] = ChunkLines,
                    ["timeoutSeconds"] = TimeoutSeconds,
                    ["contextSize"] = ContextSize,
                    ["guidelinesPerRequest"] = GuidelinesPerRequest,
                    ["useCopilotInstructions"] = UseCopilotInstructions,
                    ["disabledInstructionFiles"] = DisabledInstructionFiles.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList<object>(),
                    ["rulesFiles"] = files,
                    ["usage"] = new Dictionary<string, object>
                    {
                        ["day"] = UsageDay,
                        ["since"] = UsageSince,
                        ["dayInput"] = DayInput,
                        ["dayOutput"] = DayOutput,
                        ["dayRequests"] = DayRequests,
                        ["input"] = AllInput,
                        ["output"] = AllOutput,
                        ["requests"] = AllRequests,
                        ["estimatedRequests"] = AllEstimatedRequests,
                        ["models"] = ModelUsage.ToDictionary(p => p.Key, p => (object)p.Value.Select(v => (object)v).ToList(), StringComparer.OrdinalIgnoreCase),
                    },
                };

                foreach (KeyValuePair<string, object> pair in _legacy)
                {
                    root[pair.Key] = pair.Value;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, MiniJson.Serialize(root), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Settings are a convenience; failing to save them must not break the window.
            }
        }

        private static long Long(object node, string key) => AsLong(MiniJson.Get(node, key));

        private static long AsLong(object value) => value is double d && d >= 0 && d < 1e15 ? (long)d : 0;
    }
}
