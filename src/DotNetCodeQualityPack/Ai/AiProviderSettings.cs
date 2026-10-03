using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DotNetCodeQualityPack.Ai
{
    /// <summary>The end user's settings for one provider.</summary>
    internal sealed class ProviderConfig
    {
        public ProviderConfig(AiProviderKind kind)
        {
            Kind = kind;
        }

        public AiProviderKind Kind { get; }

        /// <summary>Offer this provider's models in the model dropdowns.</summary>
        public bool Enabled { get; set; }

        /// <summary>The API URL the user entered. Empty means the provider's default.</summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>Model names the user typed, comma-separated. They are always offered, listed by the provider or not.</summary>
        public string Models { get; set; } = string.Empty;

        /// <summary>The API key, encrypted with Windows DPAPI for the current user. Never stored in plain text.</summary>
        internal string ProtectedApiKey { get; set; }

        public IEnumerable<string> ModelNames => (Models ?? string.Empty)
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.Trim())
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Which AI providers the end user uses, with their own API URL, key and model names.
    /// Stored per Windows user in %LOCALAPPDATA%\DotNetCodeQualityPack\ai-providers.json; API keys are encrypted with
    /// Windows DPAPI (current user only), so they never appear in plain text on disk, in source control or in logs.
    /// Nothing here is shipped with the extension: every value comes from the user.
    /// </summary>
    internal sealed class AiProviderSettings
    {
        // Extra entropy so other programs using DPAPI for the same user can't trivially decrypt these values by accident.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DotNetCodeQualityPack.ApiKeys.v1");

        private readonly Dictionary<AiProviderKind, ProviderConfig> _providers = new Dictionary<AiProviderKind, ProviderConfig>();

        private AiProviderSettings()
        {
            foreach (AiProviderKind kind in AiProviders.Kinds)
            {
                _providers[kind] = new ProviderConfig(kind);
            }
        }

        public static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DotNetCodeQualityPack",
            "ai-providers.json");

        public IEnumerable<ProviderConfig> All => AiProviders.Kinds.Select(k => _providers[k]);

        public IEnumerable<ProviderConfig> Enabled => All.Where(p => p.Enabled);

        public bool AnyEnabled => All.Any(p => p.Enabled);

        public ProviderConfig Get(AiProviderKind kind) => _providers[kind];

        public static AiProviderSettings Load()
        {
            var settings = new AiProviderSettings();
            try
            {
                if (!File.Exists(FilePath))
                {
                    return settings;
                }

                object root = MiniJson.Parse(File.ReadAllText(FilePath));
                foreach (ProviderConfig config in settings.All)
                {
                    object node = MiniJson.Get(root, Key(config.Kind));
                    if (node == null)
                    {
                        continue;
                    }

                    config.Enabled = MiniJson.Get(node, "enabled") is bool enabled && enabled;
                    config.BaseUrl = MiniJson.GetString(node, "baseUrl") ?? string.Empty;
                    config.Models = MiniJson.GetString(node, "models") ?? string.Empty;
                    config.ProtectedApiKey = MiniJson.GetString(node, "apiKey");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                // Unreadable settings: start empty; the user can configure again.
            }

            return settings;
        }

        public void Save()
        {
            var root = new Dictionary<string, object>();
            foreach (ProviderConfig config in All)
            {
                root[Key(config.Kind)] = new Dictionary<string, object>
                {
                    ["enabled"] = config.Enabled,
                    ["baseUrl"] = (config.BaseUrl ?? string.Empty).Trim(),
                    ["models"] = (config.Models ?? string.Empty).Trim(),
                    ["apiKey"] = config.ProtectedApiKey,
                };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, MiniJson.Serialize(root), new UTF8Encoding(false));
        }

        /// <summary>
        /// One-time move of OpenAI / Anthropic settings saved by earlier versions in settings.json
        /// (keys stay DPAPI-encrypted; they are only copied). Does nothing once ai-providers.json exists.
        /// </summary>
        /// <returns>True when nothing is left to import (done now, done before, or nothing to import).</returns>
        public static bool ImportLegacy(object legacyRoot)
        {
            if (legacyRoot == null || File.Exists(FilePath))
            {
                return true;
            }

            string openAIKey = MiniJson.GetString(legacyRoot, "openAIKey");
            string anthropicKey = MiniJson.GetString(legacyRoot, "anthropicKey");
            if (string.IsNullOrEmpty(openAIKey) && string.IsNullOrEmpty(anthropicKey))
            {
                return true;
            }

            AiProviderSettings settings = Load();
            ProviderConfig openAI = settings.Get(AiProviderKind.OpenAI);
            openAI.ProtectedApiKey = openAIKey;
            openAI.Enabled = !string.IsNullOrEmpty(openAIKey);
            openAI.Models = MiniJson.GetString(legacyRoot, "openAIModels") ?? string.Empty;
            string openAIBase = MiniJson.GetString(legacyRoot, "openAIBaseUrl");
            openAI.BaseUrl = string.Equals(Normalize(openAIBase), AiProviders.Get(AiProviderKind.OpenAI).DefaultBaseUrl, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : openAIBase ?? string.Empty;

            ProviderConfig anthropic = settings.Get(AiProviderKind.Anthropic);
            anthropic.ProtectedApiKey = anthropicKey;
            anthropic.Enabled = !string.IsNullOrEmpty(anthropicKey);
            anthropic.Models = MiniJson.GetString(legacyRoot, "anthropicModels") ?? string.Empty;

            try
            {
                settings.Save();
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false; // settings.json keeps the old values until the move succeeds
            }
        }

        // ------------------------------------------------------------------ keys and connections

        public bool HasApiKey(AiProviderKind kind) => !string.IsNullOrEmpty(Get(kind).ProtectedApiKey);

        /// <summary>The decrypted key, or null when none is saved (or it was saved by another Windows account or PC).</summary>
        public string GetApiKey(AiProviderKind kind) => Unprotect(Get(kind).ProtectedApiKey);

        /// <summary>Encrypts the key for the current Windows user; null or empty removes it. Call <see cref="Save"/> afterwards.</summary>
        public void SetApiKey(AiProviderKind kind, string key)
        {
            Get(kind).ProtectedApiKey = string.IsNullOrWhiteSpace(key)
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));
        }

        /// <summary>The API URL in use: the user's, or the provider's default.</summary>
        public string EffectiveBaseUrl(AiProviderKind kind) => ResolveBaseUrl(kind, Get(kind).BaseUrl);

        /// <summary>The API URL for what the user typed: trimmed, without a trailing slash, or the provider's default when empty.</summary>
        public static string ResolveBaseUrl(AiProviderKind kind, string typed)
        {
            string url = Normalize(typed);
            if (string.IsNullOrEmpty(url))
            {
                url = AiProviders.Get(kind).DefaultBaseUrl;
            }

            // Accept the URLs people paste: a full endpoint, or a server root without the API version.
            foreach (string suffix in new[] { "/chat/completions", "/messages", "/api/chat", "/api/generate", "/api/tags", "/models" })
            {
                if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    url = url.Substring(0, url.Length - suffix.Length);
                    break;
                }
            }

            if (kind == AiProviderKind.Ollama)
            {
                // Ollama's URL is the server root ("http://host:11434/api" and the OpenAI-style "/v1" work too).
                foreach (string suffix in new[] { "/api", "/v1" })
                {
                    if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        url = url.Substring(0, url.Length - suffix.Length);
                    }
                }
            }
            else if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri) && (uri.AbsolutePath == "/" || uri.AbsolutePath.Length == 0))
            {
                // OpenAI and Anthropic APIs (and most compatible servers, e.g. LM Studio) live under /v1.
                url += "/v1";
            }

            return url;
        }

        public AiConnection Connection(AiProviderKind kind) => new AiConnection(EffectiveBaseUrl(kind), GetApiKey(kind));

        /// <summary>True when the URL is a usable http(s) address (empty means "use the default", which is valid).</summary>
        public static bool IsValidUrl(string url)
        {
            string value = Normalize(url);
            return string.IsNullOrEmpty(value)
                || (Uri.TryCreate(value, UriKind.Absolute, out Uri uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
        }

        private static string Normalize(string url) => (url ?? string.Empty).Trim().TrimEnd('/');

        private static string Key(AiProviderKind kind) => kind.ToString().ToLowerInvariant();

        private static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
            {
                return null;
            }
        }
    }
}
