using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotNetCodeQualityPack.Ai;
using DotNetCodeQualityPack.Services;

namespace DotNetCodeQualityPack.CodeCompass
{
    /// <summary>
    /// Adapts Code Compass conversations (messages with attached files and screenshots) to the provider-neutral
    /// <see cref="AiRequest"/>, and sends them through <see cref="AiProviders"/>. Knows nothing about any provider.
    /// </summary>
    internal sealed class ChatClient
    {
        /// <summary>Models of the providers the user enabled in AI provider settings.</summary>
        public Task<ModelLoadResult> LoadModelsAsync(CancellationToken cancellationToken)
        {
            return AiProviders.LoadModelsAsync(cancellationToken);
        }

        public static bool LooksVisionCapable(AiModel model)
        {
            return model != null && AiProviders.Get(model.Provider).SupportsImages(model.Model);
        }

        /// <summary>Streams an answer; each text chunk is reported through <paramref name="onChunk"/>. Returns the full answer.</summary>
        public Task<string> ChatAsync(
            AiModel model,
            PackSettings settings,
            string systemPrompt,
            IReadOnlyList<ChatMessage> history,
            bool jsonMode,
            IProgress<string> onChunk,
            TokenUsage usage,
            CancellationToken cancellationToken)
        {
            if (model == null)
            {
                throw new AiServiceException("Choose a model first.");
            }

            var request = new AiRequest
            {
                Model = model,
                SystemPrompt = systemPrompt,
                JsonMode = jsonMode,
                ContextSize = Math.Max(4096, settings.ContextSize),
                TimeoutSeconds = settings.TimeoutSeconds,
            };

            foreach (ChatMessage m in history)
            {
                var message = new AiMessage
                {
                    Role = m.Role == ChatMessage.AssistantRole ? AiMessage.Assistant : AiMessage.User,
                    Text = ComposeContent(m),
                };

                if (message.Role == AiMessage.User)
                {
                    message.Images.AddRange(m.Attachments
                        .Where(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64))
                        .Select(a => a.Base64));
                }

                request.Messages.Add(message);
            }

            return AiProviders.CompleteAsync(request, onChunk, usage, cancellationToken);
        }

        /// <summary>Message text plus the contents of any attached text files.</summary>
        internal static string ComposeContent(ChatMessage m)
        {
            List<Attachment> textFiles = m.Attachments.Where(a => a.Kind == AttachmentKind.Text).ToList();
            if (textFiles.Count == 0)
            {
                return m.Content ?? string.Empty;
            }

            var sb = new StringBuilder(m.Content ?? string.Empty);
            foreach (Attachment file in textFiles)
            {
                sb.AppendLine().AppendLine();
                sb.Append("Attached file: ").AppendLine(file.Name);
                sb.Append("```").AppendLine(LanguageHint(file.Name));
                sb.AppendLine(file.TextContent);
                sb.Append("```");
            }

            return sb.ToString();
        }

        internal static string LanguageHint(string fileName)
        {
            switch ((Path.GetExtension(fileName ?? string.Empty) ?? string.Empty).ToLowerInvariant())
            {
                case ".cs": return "csharp";
                case ".vb": return "vb";
                case ".xaml":
                case ".xml":
                case ".csproj":
                case ".config": return "xml";
                case ".json": return "json";
                case ".sql": return "sql";
                case ".js": return "javascript";
                case ".ts": return "typescript";
                case ".py": return "python";
                case ".md": return "markdown";
                case ".html":
                case ".cshtml":
                case ".razor": return "html";
                default: return string.Empty;
            }
        }
    }
}
