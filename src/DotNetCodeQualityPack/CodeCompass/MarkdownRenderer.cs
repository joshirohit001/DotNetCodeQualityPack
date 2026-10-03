using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DotNetCodeQualityPack.CodeCompass
{
    /// <summary>
    /// Markdown renderer for chat answers: headings, bullet and numbered lists (nested), tables, block quotes,
    /// rules, **bold**, *italic*, ~~strike~~, `inline code`, links, and fenced code blocks with VS-style
    /// syntax colours plus Copy / Insert buttons. Everything follows the window theme live and stays selectable.
    /// </summary>
    internal static class MarkdownRenderer
    {
        public static readonly FontFamily CodeFont = new FontFamily("Cascadia Mono, Cascadia Code, Consolas, Courier New");
        public static readonly FontFamily TextFont = new FontFamily("Segoe UI Variable Text, Segoe UI");

        private const double CodeFontSize = 12.5;

        private static readonly Regex Fence = new Regex(@"^[ \t]*(?<fence>```|~~~)[ \t]*(?<lang>[\w#+.-]*)[^\n]*\n(?<code>[\s\S]*?)(?:^[ \t]*\k<fence>[ \t]*$|\z)", RegexOptions.Multiline);
        private static readonly Regex InlinePattern = new Regex(
            @"(?<code>`(?<c>[^`\n]+)`)" +
            @"|(\*\*\*(?<bi>[^*\n]+?)\*\*\*)" +
            @"|(\*\*(?<b>[^\n]+?)\*\*)" +
            @"|((?<![\w])__(?<b2>[^\n]+?)__(?![\w]))" +
            @"|(~~(?<s>[^\n]+?)~~)" +
            @"|(\[(?<lt>[^\]\n]+)\]\((?<lu>[^)\s]+)\))" +
            @"|((?<![\w*])\*(?<i>[^*\s](?:[^*\n]*?[^*\s])?)\*(?![\w*]))" +
            @"|((?<![\w])_(?<i2>[^_\s](?:[^_\n]*?[^_\s])?)_(?![\w]))" +
            @"|(?<url>https?://[^\s<>()\[\]]+[^\s<>()\[\].,;:!?'""])");

        private static readonly Regex ListLine = new Regex(@"^(?<indent>[ \t]*)(?:(?<bullet>[-*+•])|(?<num>\d{1,9})[.)])[ \t]+(?<t>.*)$");
        private static readonly Regex Heading = new Regex(@"^[ \t]{0,3}(?<h>#{1,6})[ \t]+(?<t>.*?)[ \t#]*$");
        private static readonly Regex Rule = new Regex(@"^[ \t]{0,3}([-*_])([ \t]*\1){2,}[ \t]*$");
        private static readonly Regex Quote = new Regex(@"^[ \t]{0,3}>[ \t]?(?<t>.*)$");
        private static readonly Regex TableSeparator = new Regex(@"^[ \t]*\|?[ \t]*:?-{1,}:?[ \t]*(\|[ \t]*:?-{1,}:?[ \t]*)*\|?[ \t]*$");

        public static Panel Render(string markdown, Action<string> copy, Action<string> insert)
        {
            var panel = new StackPanel();
            var text = (markdown ?? string.Empty).Replace("\r\n", "\n");
            var position = 0;

            foreach (Match m in Fence.Matches(text))
            {
                if (m.Index > position)
                {
                    AddText(panel, text.Substring(position, m.Index - position));
                }

                panel.Children.Add(CreateCodeBlock(m.Groups["code"].Value.TrimEnd('\n'), m.Groups["lang"].Value, copy, insert));
                position = m.Index + m.Length;
            }

            if (position < text.Length)
            {
                AddText(panel, text.Substring(position));
            }

            return panel;
        }

        /// <summary>A themed code block with a header bar (language, Copy, Insert) and syntax colours.</summary>
        public static FrameworkElement CreateCodeBlock(string code, string language, Action<string> copy, Action<string> insert, string insertLabel = "Insert in editor")
        {
            code = (code ?? string.Empty).Replace("\r\n", "\n");

            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 8)
            };
            border.SetResourceReference(Border.BackgroundProperty, "CC.CodeBg");
            border.SetResourceReference(Border.BorderBrushProperty, "CC.Border");

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new DockPanel { Margin = new Thickness(10, 4, 4, 0), LastChildFill = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right);

            if (copy != null)
            {
                var copyButton = SmallButton("Copy", "Copy code to clipboard");
                copyButton.Click += (s, e) =>
                {
                    copy(code.Replace("\n", Environment.NewLine));
                    copyButton.Content = "Copied ✓";
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    timer.Tick += (s2, e2) => { copyButton.Content = "Copy"; timer.Stop(); };
                    timer.Start();
                };
                buttons.Children.Add(copyButton);
            }

            if (insert != null)
            {
                var insertButton = SmallButton(insertLabel, "Replace the selection in the editor (or insert at the caret). Ctrl+Z undoes it.");
                insertButton.Click += (s, e) => insert(code.Replace("\n", Environment.NewLine));
                buttons.Children.Add(insertButton);
            }

            header.Children.Add(buttons);
            var label = new TextBlock
            {
                Text = SyntaxHighlighter.DisplayName(language),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "CC.SubtleText");
            header.Children.Add(label);
            grid.Children.Add(header);

            var box = CreateCodeView(code, language);
            Grid.SetRow(box, 1);
            grid.Children.Add(box);

            border.Child = grid;
            return border;
        }

        public static Button SmallButton(string content, string tooltip)
        {
            var b = new Button { Content = content, ToolTip = tooltip, Margin = new Thickness(2, 0, 0, 0) };
            b.SetResourceReference(FrameworkElement.StyleProperty, "CC.GhostButton");
            return b;
        }

        // ------------------------------------------------------------------ code

        /// <summary>
        /// Read-only, selectable, coloured code. Each run takes its brush from a CC.Syn.* resource, so a theme
        /// switch recolours code that is already on screen. Lines never wrap; long lines scroll sideways.
        /// </summary>
        private static RichTextBox CreateCodeView(string code, string language)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            var longest = 0;
            var column = 0;

            foreach (var token in SyntaxHighlighter.Tokenize(code, language))
            {
                var parts = token.Text.Replace("\t", "    ").Split('\n');
                for (var i = 0; i < parts.Length; i++)
                {
                    if (i > 0)
                    {
                        paragraph.Inlines.Add(new LineBreak());
                        column = 0;
                    }

                    if (parts[i].Length == 0) continue;

                    var run = new Run(parts[i]);
                    run.SetResourceReference(TextElement.ForegroundProperty, SyntaxHighlighter.ResourceKey(token.Kind));
                    paragraph.Inlines.Add(run);
                    column += parts[i].Length;
                    if (column > longest) longest = column;
                }
            }

            var doc = new FlowDocument(paragraph)
            {
                PagePadding = new Thickness(0),
                FontFamily = CodeFont,
                FontSize = CodeFontSize,
                LineHeight = CodeFontSize * 1.45,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Left,

                // No wrapping: the page is as wide as the longest line.
                // (WPF allows at most 1,000,000; longer lines, e.g. minified JSON, wrap at that width.)
                PageWidth = Math.Min(MeasureCodeWidth(longest), 1000000)
            };

            var box = new RichTextBox
            {
                Document = doc,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(8, 4, 8, 8),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                AutoWordSelection = false
            };
            box.SetResourceReference(Control.ForegroundProperty, "CC.Syn.Plain");
            box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "CC.Accent");
            box.PreviewMouseWheel += ForwardMouseWheel;
            return box;
        }

        private static double MeasureCodeWidth(int characters)
        {
            var sample = new FormattedText(
                new string('M', Math.Max(1, characters)),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(CodeFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                CodeFontSize,
                Brushes.Black,
                1.0);

            // Slack for the caret and font fallback, so a line never wraps by a pixel.
            return Math.Ceiling(sample.WidthIncludingTrailingWhitespace) + 24;
        }

        /// <summary>
        /// Text boxes swallow the mouse wheel even when they can't scroll. Pass it to the chat's scroll viewer
        /// instead; Shift+wheel scrolls a wide code block sideways. Ctrl+wheel (zoom) is handled further up.
        /// </summary>
        private static void ForwardMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            var box = (TextBoxBase)sender;

            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                for (var i = 0; i < 3; i++)
                {
                    if (e.Delta > 0) box.LineLeft();
                    else box.LineRight();
                }

                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers != ModifierKeys.None) return;

            e.Handled = true;
            var parent = VisualTreeHelper.GetParent(box) as UIElement;
            parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = box
            });
        }

        // ------------------------------------------------------------------ prose

        private static void AddText(Panel panel, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = TextFont,
                FontSize = 13.5,
                LineHeight = 20
            };

            var lines = text.Trim('\n').Split('\n');
            Paragraph current = null;
            var lists = new List<ListLevel>();
            var i = 0;

            while (i < lines.Length)
            {
                var line = lines[i].TrimEnd();

                if (line.Trim().Length == 0)
                {
                    current = null;

                    // A blank line between items of the same list keeps the list going.
                    if (lists.Count > 0 && !(i + 1 < lines.Length && ListLine.IsMatch(lines[i + 1])))
                    {
                        lists.Clear();
                    }

                    i++;
                    continue;
                }

                // Table: a row of cells followed by a separator line such as |---|:---:|
                if (line.IndexOf('|') >= 0 && i + 1 < lines.Length && TableSeparator.IsMatch(lines[i + 1]) &&
                    lines[i + 1].IndexOf('-') >= 0 && lines[i + 1].IndexOf('|') >= 0)
                {
                    var header = SplitRow(line);
                    var aligns = ParseAlignments(lines[i + 1], header.Count);
                    var rows = new List<List<string>>();
                    i += 2;
                    while (i < lines.Length && lines[i].Trim().Length > 0 && lines[i].IndexOf('|') >= 0)
                    {
                        rows.Add(SplitRow(lines[i].TrimEnd()));
                        i++;
                    }

                    doc.Blocks.Add(CreateTable(header, aligns, rows));
                    current = null;
                    lists.Clear();
                    continue;
                }

                var heading = Heading.Match(line);
                if (heading.Success)
                {
                    lists.Clear();
                    var level = heading.Groups["h"].Value.Length;
                    var p = new Paragraph
                    {
                        Margin = new Thickness(0, level <= 2 ? 10 : 8, 0, 4),
                        FontWeight = FontWeights.SemiBold,
                        FontSize = level == 1 ? 17 : level == 2 ? 15.5 : 14.5
                    };
                    AddInlines(p.Inlines, heading.Groups["t"].Value);
                    doc.Blocks.Add(p);
                    current = null;
                    i++;
                    continue;
                }

                if (Rule.IsMatch(line))
                {
                    var hr = new Paragraph { Margin = new Thickness(0, 6, 0, 10), FontSize = 1, LineHeight = 1, BorderThickness = new Thickness(0, 0, 0, 1) };
                    hr.SetResourceReference(Block.BorderBrushProperty, "CC.Border");
                    doc.Blocks.Add(hr);
                    current = null;
                    lists.Clear();
                    i++;
                    continue;
                }

                var quote = Quote.Match(line);
                if (quote.Success)
                {
                    var section = new Section { Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(10, 2, 0, 2), BorderThickness = new Thickness(3, 0, 0, 0) };
                    section.SetResourceReference(Block.BorderBrushProperty, "CC.Accent");
                    section.SetResourceReference(TextElement.ForegroundProperty, "CC.SubtleText");
                    var p = new Paragraph { Margin = new Thickness(0) };
                    var first = true;
                    while (i < lines.Length)
                    {
                        var q = Quote.Match(lines[i].TrimEnd());
                        if (!q.Success) break;
                        if (!first) p.Inlines.Add(new LineBreak());
                        AddInlines(p.Inlines, q.Groups["t"].Value);
                        first = false;
                        i++;
                    }

                    section.Blocks.Add(p);
                    doc.Blocks.Add(section);
                    current = null;
                    lists.Clear();
                    continue;
                }

                var item = ListLine.Match(line);
                if (item.Success)
                {
                    AddListItem(doc, lists, item);
                    current = null;
                    i++;
                    continue;
                }

                // An indented line right after a list item continues that item.
                if (lists.Count > 0 && char.IsWhiteSpace(lines[i][0]))
                {
                    var last = lists[lists.Count - 1].List.ListItems.LastListItem;
                    if (last?.Blocks.LastBlock is Paragraph lp)
                    {
                        lp.Inlines.Add(new LineBreak());
                        AddInlines(lp.Inlines, line.Trim());
                        i++;
                        continue;
                    }
                }

                lists.Clear();
                if (current == null)
                {
                    current = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                    doc.Blocks.Add(current);
                }
                else
                {
                    current.Inlines.Add(new LineBreak());
                }

                AddInlines(current.Inlines, line);
                i++;
            }

            var box = new RichTextBox
            {
                Document = doc,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                IsDocumentEnabled = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Margin = new Thickness(-2, 0, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            box.SetResourceReference(Control.ForegroundProperty, "CC.Text");
            box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "CC.Accent");
            box.PreviewMouseWheel += ForwardMouseWheel;
            panel.Children.Add(box);
        }

        private sealed class ListLevel
        {
            public int Indent;
            public bool Numbered;
            public List List;
        }

        private static void AddListItem(FlowDocument doc, List<ListLevel> lists, Match item)
        {
            var indent = item.Groups["indent"].Value.Replace("\t", "    ").Length;
            var numbered = item.Groups["num"].Success;

            // Close deeper levels.
            while (lists.Count > 0 && indent < lists[lists.Count - 1].Indent)
            {
                lists.RemoveAt(lists.Count - 1);
            }

            // Same level but the other kind of list (bullets after numbers): start a new list.
            if (lists.Count > 0 && indent == lists[lists.Count - 1].Indent && lists[lists.Count - 1].Numbered != numbered)
            {
                lists.RemoveAt(lists.Count - 1);
            }

            if (lists.Count == 0 || indent > lists[lists.Count - 1].Indent)
            {
                var list = new List
                {
                    MarkerStyle = numbered ? TextMarkerStyle.Decimal : lists.Count == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle,
                    Margin = new Thickness(0, lists.Count == 0 ? 2 : 0, 0, lists.Count == 0 ? 6 : 0),
                    Padding = new Thickness(numbered ? 22 : 18, 0, 0, 0)
                };

                if (numbered && int.TryParse(item.Groups["num"].Value, out var start) && start > 1)
                {
                    list.StartIndex = start;
                }

                var parentItem = lists.Count > 0 ? lists[lists.Count - 1].List.ListItems.LastListItem : null;
                if (parentItem != null)
                {
                    parentItem.Blocks.Add(list);
                }
                else
                {
                    doc.Blocks.Add(list);
                }

                lists.Add(new ListLevel { Indent = indent, Numbered = numbered, List = list });
            }

            var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            AddInlines(p.Inlines, item.Groups["t"].Value);
            lists[lists.Count - 1].List.ListItems.Add(new ListItem(p));
        }

        // ------------------------------------------------------------------ tables

        private static List<string> SplitRow(string line)
        {
            var cells = new List<string>();
            var s = line.Trim();
            if (s.StartsWith("|", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.EndsWith("|", StringComparison.Ordinal) && !s.EndsWith("\\|", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);

            var cell = new StringBuilder();
            var inCode = false;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '\\' && i + 1 < s.Length && s[i + 1] == '|')
                {
                    cell.Append('|');
                    i++;
                    continue;
                }

                if (c == '`') inCode = !inCode;
                if (c == '|' && !inCode)
                {
                    cells.Add(cell.ToString().Trim());
                    cell.Clear();
                    continue;
                }

                cell.Append(c);
            }

            cells.Add(cell.ToString().Trim());
            return cells;
        }

        private static List<TextAlignment> ParseAlignments(string separator, int columns)
        {
            var result = new List<TextAlignment>();
            foreach (var spec in SplitRow(separator))
            {
                var left = spec.StartsWith(":", StringComparison.Ordinal);
                var right = spec.EndsWith(":", StringComparison.Ordinal);
                result.Add(left && right ? TextAlignment.Center : right ? TextAlignment.Right : TextAlignment.Left);
            }

            while (result.Count < columns) result.Add(TextAlignment.Left);
            return result;
        }

        private static Table CreateTable(List<string> header, List<TextAlignment> aligns, List<List<string>> rows)
        {
            var columns = header.Count;
            foreach (var row in rows) columns = Math.Max(columns, row.Count);
            while (aligns.Count < columns) aligns.Add(TextAlignment.Left);

            var table = new Table
            {
                CellSpacing = 0,
                BorderThickness = new Thickness(1, 1, 0, 0),
                Margin = new Thickness(0, 4, 0, 10)
            };
            table.SetResourceReference(Block.BorderBrushProperty, "CC.Border");

            // FlowDocument tables can't size columns to their content, so share the width by content length:
            // a "#" column stays narrow while a "Description" column gets the room.
            for (var c = 0; c < columns; c++)
            {
                var longest = c < header.Count ? PlainLength(header[c]) : 1;
                foreach (var row in rows)
                {
                    if (c < row.Count) longest = Math.Max(longest, PlainLength(row[c]));
                }

                var weight = Math.Max(2.5, Math.Min(40, longest));
                table.Columns.Add(new TableColumn { Width = new GridLength(weight, GridUnitType.Star) });
            }

            var headerGroup = new TableRowGroup();
            var headerRow = new TableRow { FontWeight = FontWeights.SemiBold };
            headerRow.SetResourceReference(TextElement.BackgroundProperty, "CC.TableHeader");
            for (var c = 0; c < columns; c++)
            {
                headerRow.Cells.Add(CreateCell(c < header.Count ? header[c] : string.Empty, aligns[c], isHeader: true));
            }

            headerGroup.Rows.Add(headerRow);
            table.RowGroups.Add(headerGroup);

            var body = new TableRowGroup();
            for (var r = 0; r < rows.Count; r++)
            {
                var row = new TableRow();
                if (r % 2 == 1) row.SetResourceReference(TextElement.BackgroundProperty, "CC.TableStripe");
                for (var c = 0; c < columns; c++)
                {
                    row.Cells.Add(CreateCell(c < rows[r].Count ? rows[r][c] : string.Empty, aligns[c], isHeader: false));
                }

                body.Rows.Add(row);
            }

            table.RowGroups.Add(body);
            return table;
        }

        private static TableCell CreateCell(string text, TextAlignment alignment, bool isHeader)
        {
            var p = new Paragraph { Margin = new Thickness(0), TextAlignment = alignment, LineHeight = 18 };
            var fragments = text.Split(new[] { "<br>", "<br/>", "<br />" }, StringSplitOptions.None);
            for (var i = 0; i < fragments.Length; i++)
            {
                if (i > 0) p.Inlines.Add(new LineBreak());
                AddInlines(p.Inlines, fragments[i]);
            }

            if (!isHeader)
            {
                var severity = SeverityBrushKey(text);
                if (severity != null)
                {
                    p.FontWeight = FontWeights.SemiBold;
                    p.SetResourceReference(TextElement.ForegroundProperty, severity);
                }
            }

            var cell = new TableCell(p)
            {
                Padding = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(0, 0, 1, 1)
            };
            cell.SetResourceReference(TableCell.BorderBrushProperty, "CC.Border");
            return cell;
        }

        /// <summary>Colours cells such as "Error", "🔴 High", "Warning", "Info", "✅ Fixed" like the review findings.</summary>
        private static string SeverityBrushKey(string cell)
        {
            var word = new StringBuilder();
            foreach (var ch in cell)
            {
                if (char.IsLetter(ch) || ch == ' ') word.Append(ch);
            }

            switch (word.ToString().Trim().ToLowerInvariant())
            {
                case "error":
                case "critical":
                case "blocker":
                case "high":
                case "severe":
                case "fail":
                case "failed":
                    return "CC.Error";
                case "warning":
                case "warn":
                case "medium":
                case "major":
                case "moderate":
                    return "CC.Warning";
                case "info":
                case "information":
                case "low":
                case "minor":
                case "message":
                case "suggestion":
                case "hint":
                    return "CC.Info";
                case "ok":
                case "pass":
                case "passed":
                case "fixed":
                case "success":
                case "done":
                case "yes":
                    return "CC.Success";
                default:
                    return null;
            }
        }

        private static int PlainLength(string cell)
        {
            var length = 0;
            foreach (var ch in cell)
            {
                if (ch != '*' && ch != '`' && ch != '_') length++;
            }

            return length;
        }

        // ------------------------------------------------------------------ inline text

        private static void AddInlines(InlineCollection inlines, string text)
        {
            var position = 0;
            foreach (Match m in InlinePattern.Matches(text))
            {
                if (m.Index > position) inlines.Add(new Run(text.Substring(position, m.Index - position)));

                if (m.Groups["c"].Success)
                {
                    var run = new Run(" " + m.Groups["c"].Value + " ") { FontFamily = CodeFont, FontSize = 12.5 };
                    run.SetResourceReference(TextElement.BackgroundProperty, "CC.CodeBg");
                    inlines.Add(run);
                }
                else if (m.Groups["bi"].Success)
                {
                    var bold = new Bold { FontStyle = FontStyles.Italic };
                    AddInlines(bold.Inlines, m.Groups["bi"].Value);
                    inlines.Add(bold);
                }
                else if (m.Groups["b"].Success || m.Groups["b2"].Success)
                {
                    var bold = new Bold();
                    AddInlines(bold.Inlines, m.Groups["b"].Success ? m.Groups["b"].Value : m.Groups["b2"].Value);
                    inlines.Add(bold);
                }
                else if (m.Groups["s"].Success)
                {
                    var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                    AddInlines(span.Inlines, m.Groups["s"].Value);
                    inlines.Add(span);
                }
                else if (m.Groups["i"].Success || m.Groups["i2"].Success)
                {
                    var italic = new Italic();
                    AddInlines(italic.Inlines, m.Groups["i"].Success ? m.Groups["i"].Value : m.Groups["i2"].Value);
                    inlines.Add(italic);
                }
                else if (m.Groups["lt"].Success)
                {
                    inlines.Add(CreateLink(m.Groups["lt"].Value, m.Groups["lu"].Value));
                }
                else if (m.Groups["url"].Success)
                {
                    inlines.Add(CreateLink(m.Groups["url"].Value, m.Groups["url"].Value));
                }

                position = m.Index + m.Length;
            }

            if (position < text.Length) inlines.Add(new Run(text.Substring(position)));
        }

        /// <summary>Opens only web and mail links, in the default browser. Anything else stays plain text.</summary>
        private static Inline CreateLink(string text, string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeMailto))
            {
                return new Run(text);
            }

            var link = new Hyperlink(new Run(text)) { NavigateUri = uri, ToolTip = uri.AbsoluteUri, Cursor = Cursors.Hand };
            link.SetResourceReference(TextElement.ForegroundProperty, "CC.Accent");
            link.RequestNavigate += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // No default browser: nothing useful to do from a chat bubble.
                }

                e.Handled = true;
            };
            return link;
        }
    }
}
