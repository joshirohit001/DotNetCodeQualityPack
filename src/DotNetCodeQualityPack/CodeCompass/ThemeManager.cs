using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace DotNetCodeQualityPack.CodeCompass
{
    /// <summary>
    /// Window themes. "Visual Studio" follows the IDE theme live; the others are fixed palettes.
    /// Every brush is stored under a "CC.*" resource key that the XAML uses through DynamicResource.
    /// </summary>
    internal static class ThemeManager
    {
        public const string VisualStudio = "Visual Studio";
        public const string Dark = "Dark";
        public const string Light = "Light";
        public const string Midnight = "Midnight Blue";

        public static readonly IReadOnlyList<string> Themes = new[] { VisualStudio, Dark, Light, Midnight };

        public static bool IsDark { get; private set; }

        public static void Apply(FrameworkElement root, string theme)
        {
            Palette p;
            switch (theme)
            {
                case Dark:
                    p = new Palette("#1E1F22", "#E6E6E6", "#6C8EFF", true);
                    break;
                case Light:
                    p = new Palette("#F7F8FA", "#1F2328", "#3B5BDB", false);
                    break;
                case Midnight:
                    p = new Palette("#0F172A", "#E2E8F0", "#38BDF8", true);
                    break;
                default:
                    p = FromVisualStudio();
                    break;
            }

            IsDark = p.IsDark;
            var r = root.Resources;
            r["CC.Bg"] = Brush(p.Bg);
            r["CC.Surface"] = Brush(Blend(p.Bg, p.Fg, p.IsDark ? 0.06 : 0.0, p.IsDark ? null : (Color?)Colors.White));
            r["CC.SurfaceAlt"] = Brush(Blend(p.Bg, p.Fg, p.IsDark ? 0.10 : 0.04));
            r["CC.Hover"] = Brush(Blend(p.Bg, p.Fg, p.IsDark ? 0.14 : 0.08));
            r["CC.Border"] = Brush(Blend(p.Bg, p.Fg, p.IsDark ? 0.20 : 0.15));
            r["CC.Text"] = Brush(p.Fg);
            r["CC.SubtleText"] = Brush(Blend(p.Fg, p.Bg, 0.40));
            r["CC.Accent"] = Brush(p.Accent);
            r["CC.AccentHover"] = Brush(Blend(p.Accent, p.IsDark ? Colors.White : Colors.Black, 0.15));
            r["CC.AccentText"] = Brush(Luminance(p.Accent) > 0.6 ? Color.FromRgb(0x10, 0x14, 0x1A) : Colors.White);
            r["CC.AccentSoft"] = Brush(Blend(p.Bg, p.Accent, p.IsDark ? 0.22 : 0.12));
            r["CC.UserBubble"] = Brush(Blend(p.Bg, p.Accent, p.IsDark ? 0.28 : 0.14));
            r["CC.AssistantBubble"] = Brush(Blend(p.Bg, p.Fg, p.IsDark ? 0.06 : 0.0, p.IsDark ? null : (Color?)Colors.White));
            r["CC.CodeBg"] = Brush(p.IsDark ? Blend(p.Bg, Colors.Black, 0.35) : Color.FromRgb(0xF0, 0xF2, 0xF5));
            r["CC.CodeText"] = Brush(p.IsDark ? Color.FromRgb(0xD4, 0xD4, 0xD4) : Color.FromRgb(0x1F, 0x23, 0x28));
            r["CC.Error"] = Brush(p.IsDark ? Hex("#F87171") : Hex("#C92A2A"));
            r["CC.Warning"] = Brush(p.IsDark ? Hex("#FBBF24") : Hex("#B7791F"));
            r["CC.Info"] = Brush(p.IsDark ? Hex("#60A5FA") : Hex("#1C7ED6"));
            r["CC.Success"] = Brush(p.IsDark ? Hex("#4ADE80") : Hex("#2B8A3E"));
            r["CC.ErrorSoft"] = Brush(Blend(p.Bg, p.IsDark ? Hex("#F87171") : Hex("#C92A2A"), 0.15));
            r["CC.WarningSoft"] = Brush(Blend(p.Bg, p.IsDark ? Hex("#FBBF24") : Hex("#B7791F"), 0.15));
            r["CC.InfoSoft"] = Brush(Blend(p.Bg, p.IsDark ? Hex("#60A5FA") : Hex("#1C7ED6"), 0.15));

            // Markdown tables.
            r["CC.TableHeader"] = Brush(Blend(p.Bg, p.Fg, p.IsDark ? 0.13 : 0.07));
            r["CC.TableStripe"] = Brush(Blend(p.Bg, p.Fg, p.IsDark ? 0.04 : 0.025));

            // Code colours. "Visual Studio" picks the VS 2022 Dark or Light set to match the IDE.
            ApplySyntax(r, theme == Midnight ? SyntaxMidnight : p.IsDark ? SyntaxDark : SyntaxLight);
        }

        // Order: Plain, Keyword, Control, Type, Method, String, Number, Comment, Preproc, Identifier, Tag, AttrName, Punctuation.
        // VS 2022 "Dark" defaults (the same family VS Code's Dark+ uses).
        private static readonly string[] SyntaxDark =
        {
            "#DCDCDC", "#569CD6", "#D8A0DF", "#4EC9B0", "#DCDCAA", "#D69D85", "#B5CEA8", "#57A64A", "#9B9B9B", "#9CDCFE", "#569CD6", "#9CDCFE", "#B4B4B4",
        };

        // VS 2022 "Light" defaults.
        private static readonly string[] SyntaxLight =
        {
            "#1F1F1F", "#0000FF", "#8F08C4", "#2B91AF", "#74531F", "#A31515", "#098658", "#008000", "#808080", "#1F377F", "#800000", "#E50000", "#3B3B3B",
        };

        // Tuned for the navy background of "Midnight Blue".
        private static readonly string[] SyntaxMidnight =
        {
            "#D6DEEB", "#82AAFF", "#C792EA", "#7FDBCA", "#FFD580", "#ECC48D", "#F78C6C", "#6E8BA3", "#94A3B8", "#BAE6FD", "#7FDBCA", "#C5E478", "#A9B8CC",
        };

        private static void ApplySyntax(ResourceDictionary r, string[] colors)
        {
            var kinds = (SyntaxKind[])Enum.GetValues(typeof(SyntaxKind));
            for (var i = 0; i < kinds.Length && i < colors.Length; i++)
            {
                r[SyntaxHighlighter.ResourceKey(kinds[i])] = Brush(Hex(colors[i]));
            }
        }

        public static Brush Get(FrameworkElement root, string key)
        {
            return root.TryFindResource(key) as Brush ?? Brushes.Gray;
        }

        private static Palette FromVisualStudio()
        {
            var bg = ToMedia(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey));
            var fg = ToMedia(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey));
            var accent = ToMedia(VSColorTheme.GetThemedColor(EnvironmentColors.SystemHighlightColorKey));
            var dark = Luminance(bg) < 0.5;

            // Some VS themes use a grey highlight; fall back to Code Compass blue so the accent stays visible.
            if (Saturation(accent) < 0.25) accent = dark ? Hex("#6C8EFF") : Hex("#3B5BDB");
            return new Palette(bg, fg, accent, dark);
        }

        private static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Color Hex(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        private static Color ToMedia(System.Drawing.Color c)
        {
            return Color.FromArgb(255, c.R, c.G, c.B);
        }

        private static Color Blend(Color from, Color to, double amount, Color? overrideColor = null)
        {
            if (overrideColor.HasValue) return overrideColor.Value;
            return Color.FromRgb(
                (byte)(from.R + (to.R - from.R) * amount),
                (byte)(from.G + (to.G - from.G) * amount),
                (byte)(from.B + (to.B - from.B) * amount));
        }

        private static double Luminance(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        }

        private static double Saturation(Color c)
        {
            var max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
            var min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
            return max <= 0 ? 0 : (max - min) / max;
        }

        private struct Palette
        {
            public Palette(string bg, string fg, string accent, bool isDark)
                : this(Hex(bg), Hex(fg), Hex(accent), isDark)
            {
            }

            public Palette(Color bg, Color fg, Color accent, bool isDark)
            {
                Bg = bg;
                Fg = fg;
                Accent = accent;
                IsDark = isDark;
            }

            public Color Bg { get; }
            public Color Fg { get; }
            public Color Accent { get; }
            public bool IsDark { get; }
        }
    }
}
