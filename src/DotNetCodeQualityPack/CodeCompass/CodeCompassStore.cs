using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DotNetCodeQualityPack.Ai;

namespace DotNetCodeQualityPack.CodeCompass
{
    /// <summary>
    /// Saves chats in %LOCALAPPDATA%\DotNetCodeQualityPack\CodeCompass so a conversation continues after the
    /// window or Visual Studio is closed. One file per solution (or per folder when no solution is open).
    /// </summary>
    internal sealed class CodeCompassStore
    {
        private const int MaxConversationsKept = 30;

        public CodeCompassStore()
        {
            RootFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DotNetCodeQualityPack",
                "CodeCompass");
            Directory.CreateDirectory(Path.Combine(RootFolder, "Chats"));
        }

        public string RootFolder { get; }

        private string SettingsFile => Path.Combine(RootFolder, "settings.json");

        public ConversationFile Load(string contextKey)
        {
            string file = ChatFile(contextKey);
            try
            {
                if (File.Exists(file))
                {
                    object root = MiniJson.Parse(File.ReadAllText(file));
                    return new ConversationFile
                    {
                        ActiveConversationId = MiniJson.GetString(root, "active"),
                        Conversations = (MiniJson.Get(root, "conversations") as List<object> ?? new List<object>())
                            .Select(Conversation.FromJson)
                            .ToList(),
                    };
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                // Keep a copy of an unreadable file and start fresh.
                TryMove(file, file + ".bad");
            }

            return new ConversationFile();
        }

        public void Save(string contextKey, ConversationFile data)
        {
            data.Conversations = data.Conversations
                .OrderByDescending(c => c.UpdatedUtc)
                .Take(MaxConversationsKept)
                .ToList();

            var root = new Dictionary<string, object>
            {
                ["active"] = data.ActiveConversationId,
                ["conversations"] = data.Conversations.Select(c => (object)c.ToJson()).ToList(),
            };

            string file = ChatFile(contextKey);
            string temp = file + ".tmp";
            File.WriteAllText(temp, MiniJson.Serialize(root), new UTF8Encoding(false));
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            File.Move(temp, file);
        }

        public CodeCompassSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    return CodeCompassSettings.FromJson(MiniJson.Parse(File.ReadAllText(SettingsFile)));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                // Defaults below.
            }

            return new CodeCompassSettings();
        }

        public void SaveSettings(CodeCompassSettings settings)
        {
            try
            {
                File.WriteAllText(SettingsFile, MiniJson.Serialize(settings.ToJson()), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Preferences are a convenience; ignore write failures.
            }
        }

        private string ChatFile(string contextKey)
        {
            if (string.IsNullOrWhiteSpace(contextKey))
            {
                return Path.Combine(RootFolder, "Chats", "no-solution.json");
            }

            string name = Path.GetFileNameWithoutExtension(contextKey.TrimEnd('\\', '/'));
            return Path.Combine(RootFolder, "Chats", SafeName(name) + "-" + Hash(contextKey.ToLowerInvariant()) + ".json");
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                return BitConverter.ToString(bytes, 0, 6).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string SafeName(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string safe = new string((value ?? "chat").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return safe.Length == 0 ? "chat" : safe;
        }

        private static void TryMove(string from, string to)
        {
            try
            {
                if (File.Exists(to))
                {
                    File.Delete(to);
                }

                File.Move(from, to);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Nothing more to do.
            }
        }
    }
}
