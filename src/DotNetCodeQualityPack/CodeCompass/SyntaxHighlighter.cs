using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DotNetCodeQualityPack.CodeCompass
{
    /// <summary>
    /// What a piece of code is, for colouring. The order matches the colour lists in <see cref="ThemeManager"/>.
    /// </summary>
    internal enum SyntaxKind
    {
        Plain,
        Keyword,
        Control,
        Type,
        Method,
        String,
        Number,
        Comment,
        Preproc,
        Identifier,
        Tag,
        AttrName,
        Punctuation,
    }

    internal struct SyntaxToken
    {
        public SyntaxToken(string text, SyntaxKind kind)
        {
            Text = text;
            Kind = kind;
        }

        public string Text { get; }

        public SyntaxKind Kind { get; }
    }

    /// <summary>
    /// A small, fast tokenizer that colours code in chat answers the way the Visual Studio 2022 editor does:
    /// C# (keywords, control keywords, types, methods, strings incl. verbatim / interpolated / raw, numbers,
    /// comments, #directives, locals), plus SQL, JSON, XML/XAML/HTML, JavaScript/TypeScript, PowerShell,
    /// Python, shell scripts and diffs. It is a lexer, not a compiler: it guesses types from casing and context.
    /// </summary>
    internal static class SyntaxHighlighter
    {
        private enum Lang
        {
            None,
            CSharp,
            Sql,
            Json,
            Xml,
            Script,
            PowerShell,
            Python,
            Shell,
            Diff,
            Generic,
        }

        private static readonly Regex LooksLikeCSharp = new Regex(
            @"(;\s*$)|=>|\b(public|private|internal|protected|static|class|namespace|using|var|void|async|await|return|new)\b",
            RegexOptions.Multiline);

        private static readonly Regex LooksLikeSql = new Regex(@"^\s*(SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|WITH|DECLARE)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        private static readonly Dictionary<Lang, Spec> Specs = BuildSpecs();

        public static string ResourceKey(SyntaxKind kind)
        {
            return "CC.Syn." + kind;
        }

        /// <summary>A friendly label for the code block header ("C#", "SQL", ...).</summary>
        public static string DisplayName(string language)
        {
            var l = (language ?? string.Empty).Trim();
            switch (l.ToLowerInvariant())
            {
                case "":
                    return "code";
                case "csharp":
                case "cs":
                case "c#":
                case "c-sharp":
                    return "C#";
                case "sql":
                case "tsql":
                case "t-sql":
                    return "SQL";
                case "json":
                    return "JSON";
                case "xml":
                    return "XML";
                case "xaml":
                    return "XAML";
                case "html":
                    return "HTML";
                case "js":
                case "javascript":
                    return "JavaScript";
                case "ts":
                case "typescript":
                    return "TypeScript";
                case "powershell":
                case "ps1":
                case "pwsh":
                    return "PowerShell";
                case "python":
                case "py":
                    return "Python";
                case "bash":
                case "sh":
                case "shell":
                    return "Shell";
                default:
                    return l;
            }
        }

        public static List<SyntaxToken> Tokenize(string code, string language)
        {
            code = (code ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            var tokens = new List<SyntaxToken>();
            var lang = Resolve(language, code);

            switch (lang)
            {
                case Lang.None:
                    tokens.Add(new SyntaxToken(code, SyntaxKind.Plain));
                    break;
                case Lang.Xml:
                    new XmlScanner(code, tokens).Run();
                    break;
                case Lang.Diff:
                    ScanDiff(code, tokens);
                    break;
                default:
                    new Scanner(code, Specs[lang], tokens).Run();
                    break;
            }

            return Merge(tokens);
        }

        private static Lang Resolve(string language, string code)
        {
            switch ((language ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "csharp":
                case "cs":
                case "c#":
                case "c-sharp":
                case "dotnet":
                    return Lang.CSharp;
                case "sql":
                case "tsql":
                case "t-sql":
                case "mssql":
                case "mysql":
                case "postgres":
                case "postgresql":
                case "sqlite":
                case "plsql":
                    return Lang.Sql;
                case "json":
                case "jsonc":
                    return Lang.Json;
                case "xml":
                case "xaml":
                case "html":
                case "htm":
                case "xhtml":
                case "svg":
                case "csproj":
                case "props":
                case "targets":
                case "config":
                case "resx":
                case "vsct":
                case "nuspec":
                case "vsixmanifest":
                case "msbuild":
                    return Lang.Xml;
                case "js":
                case "javascript":
                case "jsx":
                case "ts":
                case "typescript":
                case "tsx":
                case "java":
                case "c":
                case "cpp":
                case "c++":
                case "go":
                case "kotlin":
                case "swift":
                case "rust":
                    return Lang.Script;
                case "powershell":
                case "ps1":
                case "ps":
                case "pwsh":
                    return Lang.PowerShell;
                case "python":
                case "py":
                    return Lang.Python;
                case "bash":
                case "sh":
                case "shell":
                case "zsh":
                case "console":
                case "terminal":
                case "cmd":
                case "bat":
                case "batch":
                    return Lang.Shell;
                case "diff":
                case "patch":
                    return Lang.Diff;
                case "text":
                case "txt":
                case "plaintext":
                case "output":
                case "log":
                    return Lang.None;
                default:
                    return Guess(code);
            }
        }

        /// <summary>For blocks without a (known) language, e.g. "suggested fix" from the review tab.</summary>
        private static Lang Guess(string code)
        {
            var t = code.TrimStart();
            if (t.StartsWith("<", StringComparison.Ordinal)) return Lang.Xml;
            if ((t.StartsWith("{", StringComparison.Ordinal) || t.StartsWith("[", StringComparison.Ordinal)) && t.Contains("\":")) return Lang.Json;
            if (LooksLikeSql.IsMatch(code)) return Lang.Sql;
            if (LooksLikeCSharp.IsMatch(code)) return Lang.CSharp;
            return Lang.Generic;
        }

        private static List<SyntaxToken> Merge(List<SyntaxToken> tokens)
        {
            var merged = new List<SyntaxToken>(tokens.Count);
            foreach (var t in tokens)
            {
                if (t.Text.Length == 0) continue;
                if (merged.Count > 0 && merged[merged.Count - 1].Kind == t.Kind)
                {
                    merged[merged.Count - 1] = new SyntaxToken(merged[merged.Count - 1].Text + t.Text, t.Kind);
                }
                else
                {
                    merged.Add(t);
                }
            }

            return merged;
        }

        private static void ScanDiff(string code, List<SyntaxToken> tokens)
        {
            var lines = code.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                SyntaxKind kind;
                if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) ||
                    line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal))
                {
                    kind = SyntaxKind.Keyword;
                }
                else if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    kind = SyntaxKind.Preproc;
                }
                else if (line.StartsWith("+", StringComparison.Ordinal))
                {
                    kind = SyntaxKind.Type;
                }
                else if (line.StartsWith("-", StringComparison.Ordinal))
                {
                    kind = SyntaxKind.String;
                }
                else
                {
                    kind = SyntaxKind.Plain;
                }

                tokens.Add(new SyntaxToken(line, kind));
                if (i < lines.Length - 1) tokens.Add(new SyntaxToken("\n", SyntaxKind.Plain));
            }
        }

        private static bool IsIdentStart(char c)
        {
            return char.IsLetter(c) || c == '_';
        }

        private static bool IsIdentPart(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        // ------------------------------------------------------------------ language definitions

        private sealed class Spec
        {
            public HashSet<string> Keywords;
            public HashSet<string> Control;
            public HashSet<string> Types;
            public string[] LineComments = new string[0];
            public string BlockOpen;
            public string BlockClose;
            public string Quotes = "\"'";
            public char Escape = '\\';
            public bool DoubledQuotes;
            public bool CSharp;
            public bool Python;
            public bool Json;
            public bool Backtick;
            public bool DollarVariables;
            public bool AtVariables;
            public bool BracketNames;
            public bool Decorators;
            public bool DashedWords;
            public bool CommandWords;
            public bool PascalTypes;
            public bool LowerIdentifiers;
        }

        private static HashSet<string> Words(string words, bool ignoreCase = false)
        {
            return new HashSet<string>(
                words.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries),
                ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }

        private static Dictionary<Lang, Spec> BuildSpecs()
        {
            var specs = new Dictionary<Lang, Spec>();

            specs[Lang.CSharp] = new Spec
            {
                Keywords = Words(
                    "abstract as base bool byte char checked class const decimal default delegate double enum event explicit extern " +
                    "false fixed float implicit in int interface internal is lock long namespace new null object operator out override " +
                    "params private protected public readonly ref sbyte sealed short sizeof stackalloc static string struct this true " +
                    "typeof uint ulong unchecked unsafe ushort using virtual void volatile var async dynamic nameof get set init partial " +
                    "record required global where when with and or not nint nuint unmanaged notnull"),
                Control = Words("if else switch case do for foreach while break continue goto return throw try catch finally yield await"),
                LineComments = new[] { "//" },
                BlockOpen = "/*",
                BlockClose = "*/",
                CSharp = true,
                PascalTypes = true,
                LowerIdentifiers = true,
            };

            specs[Lang.Sql] = new Spec
            {
                Keywords = Words(
                    "select from where and or not in is null like between join inner left right full outer cross apply on as group by " +
                    "order having distinct top insert into values update set delete create alter drop table view index procedure proc " +
                    "function trigger primary key foreign references constraint default unique check exists union all case when then " +
                    "else end declare begin commit rollback transaction tran exec execute with nolock asc desc offset fetch next rows " +
                    "only limit returns return if while go use database schema identity output inserted deleted merge using matched " +
                    "truncate over partition nocount off print raiserror throw try catch",
                    ignoreCase: true),
                Control = Words(string.Empty),
                Types = Words(
                    "int bigint smallint tinyint bit decimal numeric money smallmoney float real date datetime datetime2 datetimeoffset " +
                    "smalldatetime time char varchar nchar nvarchar text ntext uniqueidentifier varbinary binary image xml boolean " +
                    "serial bigserial uuid json jsonb timestamp timestamptz integer sysname rowversion",
                    ignoreCase: true),
                LineComments = new[] { "--" },
                BlockOpen = "/*",
                BlockClose = "*/",
                Quotes = "'",
                Escape = '\0',
                DoubledQuotes = true,
                AtVariables = true,
                BracketNames = true,
            };

            specs[Lang.Json] = new Spec
            {
                Keywords = Words("true false null"),
                Control = Words(string.Empty),
                LineComments = new[] { "//" },
                BlockOpen = "/*",
                BlockClose = "*/",
                Quotes = "\"",
                Json = true,
            };

            specs[Lang.Script] = new Spec
            {
                Keywords = Words(
                    "var let const function class extends implements new this super import export from default as typeof instanceof " +
                    "in of void delete null undefined true false async static get set interface type enum private public protected " +
                    "readonly declare namespace module keyof abstract package final override fn func impl pub mut struct trait use"),
                Control = Words("if else for while do switch case break continue return throw try catch finally await yield match"),
                Types = Words("string number boolean any unknown never object bigint symbol int long double float char byte short bool"),
                LineComments = new[] { "//" },
                BlockOpen = "/*",
                BlockClose = "*/",
                Backtick = true,
                PascalTypes = true,
                LowerIdentifiers = true,
            };

            specs[Lang.PowerShell] = new Spec
            {
                Keywords = Words("function param begin process end filter class enum using module dynamicparam hidden static workflow", ignoreCase: true),
                Control = Words("if elseif else switch foreach for while do until break continue return throw try catch finally trap exit", ignoreCase: true),
                LineComments = new[] { "#" },
                BlockOpen = "<#",
                BlockClose = "#>",
                Escape = '`',
                DoubledQuotes = true,
                DollarVariables = true,
                DashedWords = true,
                PascalTypes = false,
            };

            specs[Lang.Python] = new Spec
            {
                Keywords = Words("def class lambda import from as global nonlocal pass None True False and or not in is del assert async self"),
                Control = Words("if elif else for while break continue return try except finally raise with yield await match case"),
                LineComments = new[] { "#" },
                Python = true,
                Decorators = true,
                PascalTypes = true,
                LowerIdentifiers = true,
            };

            specs[Lang.Shell] = new Spec
            {
                Keywords = Words("function export local readonly declare unset source alias in set rem"),
                Control = Words("if then else elif fi for do done while until case esac return exit break continue goto"),
                LineComments = new[] { "#" },
                DollarVariables = true,
                CommandWords = true,
            };

            specs[Lang.Generic] = new Spec
            {
                Keywords = Words("true false null"),
                Control = Words(string.Empty),
                LineComments = new[] { "//" },
                BlockOpen = "/*",
                BlockClose = "*/",
            };

            return specs;
        }

        // ------------------------------------------------------------------ the scanner for C-like languages

        private sealed class Scanner
        {
            private static readonly HashSet<string> TypeIntroducers = Words("class struct interface enum record new typeof is as sizeof default");

            private readonly string _code;
            private readonly Spec _spec;
            private readonly List<SyntaxToken> _tokens;
            private readonly int _n;
            private string _prevWord = string.Empty;
            private bool _lineStart = true;
            private bool _commandPosition = true;
            private int _angle;
            private bool _namespaceName;

            public Scanner(string code, Spec spec, List<SyntaxToken> tokens)
            {
                _code = code;
                _spec = spec;
                _tokens = tokens;
                _n = code.Length;
            }

            public void Run()
            {
                var i = 0;
                while (i < _n)
                {
                    var c = _code[i];

                    if (c == '\n')
                    {
                        Add(i, i + 1, SyntaxKind.Plain);
                        i++;
                        _lineStart = true;
                        _commandPosition = true;
                        continue;
                    }

                    if (char.IsWhiteSpace(c))
                    {
                        var j = i;
                        while (j < _n && _code[j] != '\n' && char.IsWhiteSpace(_code[j])) j++;
                        Add(i, j, SyntaxKind.Plain);
                        i = j;
                        continue;
                    }

                    var atLineStart = _lineStart;
                    _lineStart = false;

                    // C# #region, #if, #nullable ...
                    if (_spec.CSharp && atLineStart && c == '#')
                    {
                        i = Emit(i, LineEnd(i), SyntaxKind.Preproc);
                        continue;
                    }

                    if (TryComment(i, out var commentEnd))
                    {
                        i = Emit(i, commentEnd, SyntaxKind.Comment);
                        continue;
                    }

                    if (_spec.CSharp && TryCSharpString(i, out var stringEnd))
                    {
                        i = Emit(i, stringEnd, SyntaxKind.String);
                        continue;
                    }

                    if (_spec.Quotes.IndexOf(c) >= 0 || (_spec.Backtick && c == '`'))
                    {
                        var end = _spec.Python ? PythonString(i) : Quoted(i, c);
                        var kind = SyntaxKind.String;
                        if (_spec.Json && NextSignificant(end) == ':') kind = SyntaxKind.Identifier;
                        i = Emit(i, end, kind);
                        continue;
                    }

                    if (char.IsDigit(c) || (c == '.' && i + 1 < _n && char.IsDigit(_code[i + 1]) && (i == 0 || !IsIdentPart(_code[i - 1]))))
                    {
                        var j = i + 1;
                        while (j < _n && (IsIdentPart(_code[j]) || (_code[j] == '.' && j + 1 < _n && char.IsDigit(_code[j + 1])))) j++;
                        i = Emit(i, j, SyntaxKind.Number);
                        continue;
                    }

                    if (_spec.DollarVariables && c == '$' && i + 1 < _n)
                    {
                        var j = i + 1;
                        if (_code[j] == '{')
                        {
                            var close = _code.IndexOf('}', j);
                            j = close < 0 || close > LineEnd(i) ? j + 1 : close + 1;
                        }
                        else
                        {
                            while (j < _n && (IsIdentPart(_code[j]) || _code[j] == ':' || "?#@*!".IndexOf(_code[j]) >= 0 && j == i + 1)) j++;
                        }

                        if (j > i + 1)
                        {
                            i = Emit(i, j, SyntaxKind.Identifier);
                            continue;
                        }
                    }

                    if (_spec.AtVariables && c == '@' && i + 1 < _n && (IsIdentStart(_code[i + 1]) || _code[i + 1] == '@'))
                    {
                        var j = i + 1;
                        while (j < _n && (IsIdentPart(_code[j]) || _code[j] == '@')) j++;
                        i = Emit(i, j, SyntaxKind.Identifier);
                        continue;
                    }

                    if (_spec.Decorators && c == '@' && i + 1 < _n && IsIdentStart(_code[i + 1]))
                    {
                        var j = i + 1;
                        while (j < _n && (IsIdentPart(_code[j]) || _code[j] == '.')) j++;
                        i = Emit(i, j, SyntaxKind.Preproc);
                        continue;
                    }

                    if (_spec.BracketNames && c == '[')
                    {
                        var close = _code.IndexOf(']', i);
                        if (close > 0 && close < LineEnd(i))
                        {
                            i = Emit(i, close + 1, SyntaxKind.Plain);
                            continue;
                        }
                    }

                    if (IsIdentStart(c) || (_spec.CSharp && c == '@' && i + 1 < _n && IsIdentStart(_code[i + 1])))
                    {
                        var j = i + 1;
                        while (j < _n && (IsIdentPart(_code[j]) || (_spec.DashedWords && _code[j] == '-' && j + 1 < _n && char.IsLetter(_code[j + 1])))) j++;

                        // Python string prefixes: f"...", r'...', b"..."
                        if (_spec.Python && j - i <= 2 && j < _n && (_code[j] == '"' || _code[j] == '\'') && IsPythonPrefix(_code.Substring(i, j - i)))
                        {
                            i = Emit(i, PythonString(j), SyntaxKind.String);
                            continue;
                        }

                        var word = _code.Substring(i, j - i);
                        var kind = _namespaceName && !_spec.Keywords.Contains(word) ? SyntaxKind.Plain : Classify(word, i, j);
                        Add(i, j, kind);
                        if (word == "var") _namespaceName = false; // using var stream = ...

                        // "using System.Text;" and "namespace Demo.Api": the names after them are namespaces, not types.
                        if (_spec.CSharp && (word == "using" || word == "namespace") && NextSignificant(j) != '(')
                        {
                            _namespaceName = true;
                        }

                        // Keep "new" across "System.Text." so StringBuilder in new System.Text.StringBuilder() is a type.
                        if (!(_spec.CSharp && TypeIntroducers.Contains(_prevWord) && NextSignificant(j) == '.')) _prevWord = word;
                        _commandPosition = false;
                        i = j;
                        continue;
                    }

                    // Punctuation and operators.
                    if (_spec.CSharp)
                    {
                        if (c == '<' && i > 0 && IsIdentPart(_code[i - 1])) _angle++;
                        else if (c == '>' && _angle > 0) _angle--;
                        else if (c == ';' || c == '{' || c == '}' || c == '=' || c == '(') _angle = 0;
                        if (c == ';' || c == '{' || c == '=' || c == '(') _namespaceName = false;
                    }

                    if (c == '|' || c == ';' || c == '&' || c == '(') _commandPosition = true;
                    if (c != '.') _prevWord = string.Empty;
                    Add(i, i + 1, SyntaxKind.Punctuation);
                    i++;
                }
            }

            private SyntaxKind Classify(string word, int start, int end)
            {
                var prevChar = PrevSignificant(start);
                var next = NextSignificant(end);
                var memberAccess = prevChar == '.' || (prevChar == '>' && start >= 2 && _code[start - 2] == '-');

                if (!memberAccess || !_spec.CSharp)
                {
                    if (_spec.Control.Contains(word)) return SyntaxKind.Control;
                    if (_spec.Keywords.Contains(word)) return SyntaxKind.Keyword;
                    if (_spec.Types != null && _spec.Types.Contains(word)) return SyntaxKind.Type;
                }

                if (_spec.CommandWords && _commandPosition) return SyntaxKind.Method;
                if (_spec.DashedWords && word.IndexOf('-') > 0) return SyntaxKind.Method;

                if (_spec.CSharp) return ClassifyCSharp(word, start, prevChar, next);

                if (next == '(') return SyntaxKind.Method;
                if (_spec.PascalTypes && char.IsUpper(word[0]) && HasLower(word)) return SyntaxKind.Type;
                if (_spec.LowerIdentifiers && !memberAccess && char.IsLower(word[0])) return SyntaxKind.Identifier;
                return SyntaxKind.Plain;
            }

            private SyntaxKind ClassifyCSharp(string word, int start, char prevChar, char next)
            {
                var name = word.TrimStart('@');
                if (name.Length == 0) return SyntaxKind.Plain;
                var upper = char.IsUpper(name[0]);

                if (TypeIntroducers.Contains(_prevWord) && upper) return next == '.' ? SyntaxKind.Plain : SyntaxKind.Type;
                if (next == '(') return SyntaxKind.Method;
                if (next == '<' && IsGenericCall(start + word.Length)) return SyntaxKind.Method;
                if (!upper) return prevChar == '.' ? SyntaxKind.Plain : SyntaxKind.Identifier;

                if (_angle > 0) return SyntaxKind.Type;
                if (next == '<') return SyntaxKind.Type;
                if (prevChar == ',' && next == '{') return SyntaxKind.Type; // last base type: class A : B, IC {
                if (prevChar == '[' || prevChar == ':') return SyntaxKind.Type;
                if (prevChar == '(' && next == ')') return SyntaxKind.Type;
                if (prevChar == '.') return SyntaxKind.Plain;

                if (next == '{' || next == ';' || next == ',' || next == ')' || next == '}' || next == '\0') return SyntaxKind.Plain;
                if (next == '=' && !NextIs(start + word.Length, "=>") && !NextIs(start + word.Length, "==")) return SyntaxKind.Plain;

                // A name with no lower-case letters (MAX_SIZE, ID) is usually a constant.
                if (!HasLower(name) && name.Length > 1) return SyntaxKind.Plain;
                return SyntaxKind.Type;
            }

            /// <summary>Name&lt;T, U&gt;( : a generic method call such as GetService&lt;T&gt;() or Empty&lt;Order&gt;().</summary>
            private bool IsGenericCall(int i)
            {
                while (i < _n && (_code[i] == ' ' || _code[i] == '\t')) i++;
                if (i >= _n || _code[i] != '<') return false;

                var depth = 0;
                for (var k = i; k < _n && k - i < 200; k++)
                {
                    var ch = _code[k];
                    if (ch == '<') depth++;
                    else if (ch == '>')
                    {
                        depth--;
                        if (depth == 0) return NextSignificant(k + 1) == '(';
                    }
                    else if (!(IsIdentPart(ch) || ch == '.' || ch == ',' || ch == ' ' || ch == '?' || ch == '[' || ch == ']'))
                    {
                        return false;
                    }
                }

                return false;
            }

            private static bool HasLower(string s)
            {
                foreach (var ch in s)
                {
                    if (char.IsLower(ch)) return true;
                }

                return false;
            }

            private static bool IsPythonPrefix(string s)
            {
                foreach (var ch in s)
                {
                    if ("rRbBuUfF".IndexOf(ch) < 0) return false;
                }

                return true;
            }

            private bool TryComment(int i, out int end)
            {
                foreach (var lc in _spec.LineComments)
                {
                    if (StartsAt(i, lc))
                    {
                        // "#" only starts a comment at the start of a word (so a#b, $# and URLs stay intact).
                        if (lc == "#" && i > 0 && !char.IsWhiteSpace(_code[i - 1]) && _code[i - 1] != ';') continue;
                        end = LineEnd(i);
                        return true;
                    }
                }

                if (_spec.BlockOpen != null && StartsAt(i, _spec.BlockOpen))
                {
                    var close = _code.IndexOf(_spec.BlockClose, i + _spec.BlockOpen.Length, StringComparison.Ordinal);
                    end = close < 0 ? _n : close + _spec.BlockClose.Length;
                    return true;
                }

                end = i;
                return false;
            }

            /// <summary>"..", @"..", $"..", $@"..", """raw""", $$"""raw""" and 'c'.</summary>
            private bool TryCSharpString(int i, out int end)
            {
                end = i;
                var p = i;
                var verbatim = false;
                var interpolated = false;
                while (p < _n && p - i < 4 && (_code[p] == '$' || _code[p] == '@'))
                {
                    if (_code[p] == '@') verbatim = true;
                    else interpolated = true;
                    p++;
                }

                if (p >= _n) return false;

                if (_code[p] == '\'' && p == i)
                {
                    var j = p + 1;
                    while (j < _n && j - p < 12)
                    {
                        if (_code[j] == '\\') j += 2;
                        else if (_code[j] == '\'') { end = j + 1; return true; }
                        else if (_code[j] == '\n') break;
                        else j++;
                    }

                    end = Math.Min(j, _n);
                    return end > p + 1;
                }

                if (_code[p] != '"') return false;

                // Raw string literal (C# 11): three or more quotes, closed by the same number.
                if (StartsAt(p, "\"\"\""))
                {
                    var q = p;
                    while (q < _n && _code[q] == '"') q++;
                    var fence = new string('"', q - p);
                    var close = _code.IndexOf(fence, q, StringComparison.Ordinal);
                    end = close < 0 ? _n : close + fence.Length;
                    return true;
                }

                var k = p + 1;
                while (k < _n)
                {
                    var ch = _code[k];
                    if (!verbatim && ch == '\\') { k += 2; continue; }
                    if (!verbatim && ch == '\n') { end = k; return true; }
                    if (ch == '"')
                    {
                        if (verbatim && k + 1 < _n && _code[k + 1] == '"') { k += 2; continue; }
                        end = k + 1;
                        return true;
                    }

                    if (interpolated && ch == '{')
                    {
                        if (k + 1 < _n && _code[k + 1] == '{') { k += 2; continue; }
                        k = SkipHole(k + 1);
                        continue;
                    }

                    k++;
                }

                end = _n;
                return true;
            }

            /// <summary>Skips an interpolation hole {expr} including nested braces and strings.</summary>
            private int SkipHole(int k)
            {
                var depth = 1;
                while (k < _n && depth > 0)
                {
                    var ch = _code[k];
                    if (ch == '\n') return k;
                    if (ch == '{') depth++;
                    else if (ch == '}') depth--;
                    else if (ch == '"' || ch == '\'')
                    {
                        k++;
                        while (k < _n && _code[k] != ch && _code[k] != '\n')
                        {
                            if (_code[k] == '\\') k++;
                            k++;
                        }
                    }

                    k++;
                }

                return k;
            }

            private int Quoted(int i, char quote)
            {
                var j = i + 1;
                while (j < _n)
                {
                    var ch = _code[j];
                    // PowerShell's backtick escape doesn't apply inside single-quoted strings.
                    var escape = _spec.Escape != '\0' && ch == _spec.Escape && !(_spec.Escape == '`' && quote == '\'');
                    if (escape)
                    {
                        j += 2;
                        continue;
                    }

                    if (ch == quote)
                    {
                        if (_spec.DoubledQuotes && j + 1 < _n && _code[j + 1] == quote) { j += 2; continue; }
                        return j + 1;
                    }

                    if (ch == '\n' && quote != '`' && !(_spec.DoubledQuotes && quote == '\'')) return j;
                    j++;
                }

                return _n;
            }

            private int PythonString(int i)
            {
                var quote = _code[i];
                var triple = new string(quote, 3);
                if (StartsAt(i, triple))
                {
                    var close = _code.IndexOf(triple, i + 3, StringComparison.Ordinal);
                    return close < 0 ? _n : close + 3;
                }

                return Quoted(i, quote);
            }

            private int Emit(int start, int end, SyntaxKind kind)
            {
                if (end <= start) end = start + 1;
                if (end > _n) end = _n;
                Add(start, end, kind);
                if (kind != SyntaxKind.Comment) _commandPosition = false;
                return end;
            }

            private void Add(int start, int end, SyntaxKind kind)
            {
                _tokens.Add(new SyntaxToken(_code.Substring(start, end - start), kind));
            }

            private int LineEnd(int i)
            {
                var nl = _code.IndexOf('\n', i);
                return nl < 0 ? _n : nl;
            }

            private bool StartsAt(int i, string s)
            {
                return i + s.Length <= _n && string.CompareOrdinal(_code, i, s, 0, s.Length) == 0;
            }

            private bool NextIs(int i, string s)
            {
                while (i < _n && (_code[i] == ' ' || _code[i] == '\t')) i++;
                return i < _n && StartsAt(i, s);
            }

            private char NextSignificant(int i)
            {
                while (i < _n && char.IsWhiteSpace(_code[i])) i++;
                return i < _n ? _code[i] : '\0';
            }

            private char PrevSignificant(int i)
            {
                i--;
                while (i >= 0 && char.IsWhiteSpace(_code[i])) i--;
                return i >= 0 ? _code[i] : '\0';
            }
        }

        // ------------------------------------------------------------------ XML, XAML, HTML, csproj

        private sealed class XmlScanner
        {
            private readonly string _code;
            private readonly List<SyntaxToken> _tokens;
            private readonly int _n;

            public XmlScanner(string code, List<SyntaxToken> tokens)
            {
                _code = code;
                _tokens = tokens;
                _n = code.Length;
            }

            public void Run()
            {
                var i = 0;
                while (i < _n)
                {
                    if (StartsAt(i, "<!--"))
                    {
                        i = Until(i, "-->", SyntaxKind.Comment);
                        continue;
                    }

                    if (StartsAt(i, "<![CDATA["))
                    {
                        i = Until(i, "]]>", SyntaxKind.String);
                        continue;
                    }

                    if (_code[i] == '<' && i + 1 < _n && (IsIdentStart(_code[i + 1]) || "/?!".IndexOf(_code[i + 1]) >= 0))
                    {
                        i = Tag(i);
                        continue;
                    }

                    var j = i + 1;
                    while (j < _n && _code[j] != '<') j++;
                    Add(i, j, SyntaxKind.Plain);
                    i = j;
                }
            }

            private int Tag(int i)
            {
                var j = i + 1;
                while (j < _n && "/?!".IndexOf(_code[j]) >= 0) j++;
                Add(i, j, SyntaxKind.Punctuation);
                i = j;
                j = NameEnd(i);
                Add(i, j, SyntaxKind.Tag);
                i = j;

                while (i < _n)
                {
                    var c = _code[i];
                    if (c == '>')
                    {
                        Add(i, i + 1, SyntaxKind.Punctuation);
                        return i + 1;
                    }

                    if (c == '<') return i; // broken tag: let the main loop continue

                    if (char.IsWhiteSpace(c))
                    {
                        j = i + 1;
                        while (j < _n && char.IsWhiteSpace(_code[j])) j++;
                        Add(i, j, SyntaxKind.Plain);
                        i = j;
                        continue;
                    }

                    if (c == '"' || c == '\'')
                    {
                        var close = _code.IndexOf(c, i + 1);
                        j = close < 0 ? _n : close + 1;
                        Add(i, j, SyntaxKind.String);
                        i = j;
                        continue;
                    }

                    if (IsIdentStart(c))
                    {
                        j = NameEnd(i);
                        Add(i, j, SyntaxKind.AttrName);
                        i = j;
                        continue;
                    }

                    Add(i, i + 1, SyntaxKind.Punctuation);
                    i++;
                }

                return i;
            }

            private int NameEnd(int i)
            {
                while (i < _n && (IsIdentPart(_code[i]) || _code[i] == ':' || _code[i] == '.' || _code[i] == '-')) i++;
                return i;
            }

            private int Until(int i, string close, SyntaxKind kind)
            {
                var at = _code.IndexOf(close, i + 1, StringComparison.Ordinal);
                var end = at < 0 ? _n : at + close.Length;
                Add(i, end, kind);
                return end;
            }

            private void Add(int start, int end, SyntaxKind kind)
            {
                if (end > start) _tokens.Add(new SyntaxToken(_code.Substring(start, end - start), kind));
            }

            private bool StartsAt(int i, string s)
            {
                return i + s.Length <= _n && string.CompareOrdinal(_code, i, s, 0, s.Length) == 0;
            }
        }
    }
}
