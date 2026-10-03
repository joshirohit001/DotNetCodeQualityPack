using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DotNetCodeQualityPack.Ai;

namespace DotNetCodeQualityPack.CodeCompass
{
    internal enum AttachmentKind
    {
        Image,
        Text,
    }

    /// <summary>A file, pasted image or editor selection attached to a chat message.</summary>
    internal sealed class Attachment
    {
        public string Name { get; set; }

        public AttachmentKind Kind { get; set; }

        /// <summary>PNG bytes as base64 (images only).</summary>
        public string Base64 { get; set; }

        /// <summary>File contents (text attachments only).</summary>
        public string TextContent { get; set; }

        public long SizeBytes { get; set; }

        public string DisplaySize
        {
            get
            {
                if (SizeBytes < 1024)
                {
                    return SizeBytes + " B";
                }

                return SizeBytes < 1024 * 1024
                    ? (SizeBytes / 1024.0).ToString("0.#") + " KB"
                    : (SizeBytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
            }
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["name"] = Name,
            ["kind"] = Kind == AttachmentKind.Image ? "image" : "text",
            ["base64"] = Base64,
            ["text"] = TextContent,
            ["size"] = SizeBytes,
        };

        public static Attachment FromJson(object node) => new Attachment
        {
            Name = MiniJson.GetString(node, "name") ?? "attachment",
            Kind = MiniJson.GetString(node, "kind") == "image" ? AttachmentKind.Image : AttachmentKind.Text,
            Base64 = MiniJson.GetString(node, "base64"),
            TextContent = MiniJson.GetString(node, "text"),
            SizeBytes = MiniJson.Get(node, "size") is double d ? (long)d : 0,
        };
    }

    internal sealed class ChatMessage
    {
        public const string UserRole = "user";
        public const string AssistantRole = "assistant";

        public string Role { get; set; }

        public string Content { get; set; }

        public string Model { get; set; }

        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        public List<Attachment> Attachments { get; set; } = new List<Attachment>();

        public bool IsError { get; set; }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["role"] = Role,
            ["content"] = Content,
            ["model"] = Model,
            ["time"] = TimestampUtc.ToString("o"),
            ["error"] = IsError,
            ["attachments"] = Attachments.Select(a => (object)a.ToJson()).ToList(),
        };

        public static ChatMessage FromJson(object node)
        {
            DateTime.TryParse(MiniJson.GetString(node, "time"), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime time);
            return new ChatMessage
            {
                Role = MiniJson.GetString(node, "role") ?? UserRole,
                Content = MiniJson.GetString(node, "content") ?? string.Empty,
                Model = MiniJson.GetString(node, "model"),
                TimestampUtc = time == default ? DateTime.UtcNow : time.ToUniversalTime(),
                IsError = MiniJson.Get(node, "error") is bool b && b,
                Attachments = (MiniJson.Get(node, "attachments") as List<object> ?? new List<object>()).Select(Attachment.FromJson).ToList(),
            };
        }
    }

    internal sealed class Conversation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Title { get; set; } = "New chat";

        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();

        /// <summary>Shown in the chat dropdown.</summary>
        public string DisplayTitle => Title + "  ·  " + UpdatedUtc.ToLocalTime().ToString("dd MMM HH:mm");

        public override string ToString() => DisplayTitle;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["id"] = Id,
            ["title"] = Title,
            ["updated"] = UpdatedUtc.ToString("o"),
            ["messages"] = Messages.Select(m => (object)m.ToJson()).ToList(),
        };

        public static Conversation FromJson(object node)
        {
            DateTime.TryParse(MiniJson.GetString(node, "updated"), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime updated);
            return new Conversation
            {
                Id = MiniJson.GetString(node, "id") ?? Guid.NewGuid().ToString("N"),
                Title = MiniJson.GetString(node, "title") ?? "Chat",
                UpdatedUtc = updated == default ? DateTime.UtcNow : updated.ToUniversalTime(),
                Messages = (MiniJson.Get(node, "messages") as List<object> ?? new List<object>()).Select(ChatMessage.FromJson).ToList(),
            };
        }
    }

    internal sealed class ConversationFile
    {
        public string ActiveConversationId { get; set; }

        public List<Conversation> Conversations { get; set; } = new List<Conversation>();
    }

    internal sealed class ReviewFinding
    {
        public string Severity { get; set; }

        public string RuleId { get; set; }

        public string Title { get; set; }

        public int? Line { get; set; }

        public string Problem { get; set; }

        public string Suggestion { get; set; }

        public string FixedCode { get; set; }

        public string NormalizedSeverity
        {
            get
            {
                string s = (Severity ?? string.Empty).Trim().ToLowerInvariant();
                if (s.StartsWith("err", StringComparison.Ordinal) || s == "critical" || s == "high")
                {
                    return "Error";
                }

                return s.StartsWith("warn", StringComparison.Ordinal) || s == "medium" ? "Warning" : "Info";
            }
        }

        public static ReviewFinding FromJson(object node)
        {
            int? line = null;
            Match m = Regex.Match(MiniJson.AsString(MiniJson.Get(node, "line")) ?? string.Empty, @"\d+");
            if (m.Success && int.TryParse(m.Value, out int value) && value > 0)
            {
                line = value;
            }

            return new ReviewFinding
            {
                Severity = MiniJson.GetString(node, "severity"),
                RuleId = MiniJson.GetString(node, "ruleId") ?? MiniJson.GetString(node, "rule") ?? MiniJson.GetString(node, "guideline"),
                Title = MiniJson.GetString(node, "title"),
                Line = line,
                Problem = MiniJson.GetString(node, "problem") ?? MiniJson.GetString(node, "message") ?? MiniJson.GetString(node, "description"),
                Suggestion = MiniJson.GetString(node, "suggestion") ?? MiniJson.GetString(node, "fix"),
                FixedCode = MiniJson.GetString(node, "fixedCode") ?? MiniJson.GetString(node, "replacement") ?? MiniJson.GetString(node, "code"),
            };
        }
    }

    internal sealed class ReviewResult
    {
        public string Summary { get; set; }

        public string Verdict { get; set; }

        public List<ReviewFinding> Findings { get; set; } = new List<ReviewFinding>();

        public string ImprovedCode { get; set; }

        /// <summary>Set when the model did not return valid JSON; holds its raw answer.</summary>
        public string RawText { get; set; }
    }

    /// <summary>Window-only preferences. Models URL and API keys are shared with Team Rules (PackSettings).</summary>
    internal sealed class CodeCompassSettings
    {
        public string Theme { get; set; } = "Visual Studio";

        /// <summary>Name of the model last used in this window (as shown in the dropdown).</summary>
        public string SelectedModel { get; set; }

        public double Zoom { get; set; } = 1.0;

        /// <summary>How many earlier messages are sent back to the model so the chat continues.</summary>
        public int HistoryMessagesToSend { get; set; } = 20;

        /// <summary>Character budget for rules sent with a review (small models have small context windows).</summary>
        public int RuleCharBudget { get; set; } = 6000;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["theme"] = Theme,
            ["selectedModel"] = SelectedModel,
            ["zoom"] = Zoom,
            ["historyMessages"] = HistoryMessagesToSend,
            ["ruleCharBudget"] = RuleCharBudget,
        };

        public static CodeCompassSettings FromJson(object node)
        {
            var s = new CodeCompassSettings();
            s.Theme = MiniJson.GetString(node, "theme") ?? s.Theme;
            s.SelectedModel = MiniJson.GetString(node, "selectedModel");
            if (MiniJson.Get(node, "zoom") is double zoom)
            {
                s.Zoom = zoom;
            }

            s.HistoryMessagesToSend = MiniJson.AsInt(MiniJson.Get(node, "historyMessages")) ?? s.HistoryMessagesToSend;
            s.RuleCharBudget = MiniJson.AsInt(MiniJson.Get(node, "ruleCharBudget")) ?? s.RuleCharBudget;
            return s;
        }
    }
}
