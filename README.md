# .NET Code Quality Pack (Visual Studio 2022 extension)

One VSIX that gives every .NET developer on the team the same code-quality setup:

1. **Installs four free extensions** (extension pack): Roslynator 2022, SonarQube for Visual Studio, CodeMaid VS2022, Fine Code Coverage 2022.
2. **Adds a Tools > .NET Code Quality menu** (also on the solution's right-click menu in Solution Explorer):
   - **Open Team Rules**: your team's own rules from a Markdown file: severity overrides and settings synced to .editorconfig, custom pattern rules with auto-fixes, and AI review against plain-language guidelines (also under View > Other Windows).
   - **Open Code Quality Fixer**: a docked window to review and fix warnings page by page with a before/after preview (also under View > Other Windows).
   - **Open Code Compass (AI Chat & Review)**: chat with every configured model (paste screenshots, attach files, the conversation continues where you left off), and review the selection or file against your team rules with code fixes (also under View > Other Windows, and **Review Selection with Code Compass** on the editor's right-click menu).
   - **Check Solution**: read-only report in the Output window showing which projects are ready and what to fix.
   - **Apply Code Quality Standards...**: wires Roslynator, SonarAnalyzer, StyleCop and Meziantou analyzers plus shared rule severities into the open solution.
   - **Remove Code Quality Standards...**: undoes it.
   - **Auto-Fix Warnings...**: applies every automatic fix (formatting, code style, analyzer code fixes) across the solution, after backing up your files.
   - **Undo Last Auto-Fix...**: puts the files back the way they were.

Works for **.NET 5+ / .NET Core / .NET Standard** and **.NET Framework** projects in the same solution.

---

## Build and install

Requirements: Visual Studio 2022 17.13 or later (Fine Code Coverage 2022 needs 17.13) with the **Visual Studio extension development** workload.

1. Open `DotNetCodeQualityPack.sln` in Visual Studio 2022.
2. Build in **Release**. The installer is `src\DotNetCodeQualityPack\bin\Release\DotNetCodeQualityPack.vsix`.
3. To try it safely first, press **F5**. This opens an Experimental Instance of VS with the extension loaded.
4. To install for real, close Visual Studio and double-click the `.vsix`. On first start, VS installs the four bundled extensions from the Marketplace (internet access needed).

## Installer for your team

The `installer` folder turns the `.vsix` into a setup wizard:

1. On a Windows computer with Visual Studio 2022 and the **Visual Studio extension development** workload, double-click `installer\Build-Installers.cmd`. For `Setup.exe`, also install [Inno Setup 6](https://jrsoftware.org/isdl.php) (free) first.
2. It builds the project in Release and writes to `dist\`:
   - `DotNetCodeQualityPack-Setup-<version>.exe`: a normal setup wizard.
   - `DotNetCodeQualityPack-<version>.zip`: the same without Setup.exe (unzip, double-click `Install.cmd`).
3. Share either file. Developers close Visual Studio and run it. It finds every Visual Studio 2022 on the computer with `vswhere`, installs the extension into each one, and needs no administrator rights.
4. To remove it: Settings > Apps > ".NET Code Quality Pack for Visual Studio", or `Uninstall.cmd`.

No Windows build machine? Push the repository to GitHub and run the **Build installers** workflow (`.github/workflows/build-installers.yml`); the installers appear under the run's Artifacts.

You can also share the `.vsix` directly, or publish it to the Marketplace or a private gallery.

## Using it

1. Open a solution and run **Tools > .NET Code Quality > Check Solution**. The Output window (".NET Code Quality Pack" pane) shows one line per project:
   - `[OK]` - will get the analyzers
   - `[ACTION]` - needs a small fix first (the line says what)
   - `[SKIP]` - not a C# project
2. Run **Apply Code Quality Standards...** and confirm. When asked, let it reload the solution; NuGet restores the analyzers and warnings start showing in the editor and Error List.
3. Commit the new files so the whole team and CI get the same rules.
4. Run **Auto-Fix Warnings...** to clear everything that can be fixed automatically, then rebuild. What's left needs a human decision (for example `S1854` useless assignments or `CS8600` null warnings).

### Team Rules window (your own rules in Markdown)

**Tools > .NET Code Quality > Open Team Rules** docks a second window next to Solution Explorer.

1. Click **Create from template**. This writes `CodeQualityRules.md` at the solution root and opens it. Or click **Choose a .md file…** to use an existing one. The window also finds `CodeQualityRules.md`, `TeamRules.md`, `.codequality/rules.md` or `docs/CodeQualityRules.md` on its own, and reloads when you save the file.
2. The **Rules** tab lists everything the file defines. Mistakes (unknown severity, bad regex, …) appear in an amber box with the line number.
3. Use the buttons at the bottom:
   - **Apply to .editorconfig**: writes the severity overrides and settings into a `# >>> Team rules` block at the end of `.editorconfig`, so they override everything else. The chip shows whether `.editorconfig` is up to date.
   - **Check custom rules**: runs the pattern rules over the solution. Results appear in the **Findings** tab, grouped by file.
   - **AI review active file**: sends the C# file open in the editor and your guidelines to the selected model, and lists what it finds.
4. In **Findings**, click a row to preview the fix (red/green), then **Apply fix**, **Fix all for this rule…**, **Go to code** or **Dismiss**. **Undo** is at the top.

#### Markdown format

| Section heading | Contents |
|---|---|
| `## Severity overrides` | Table: `\| Rule \| Severity \| Why \|`; severity is none, silent, suggestion, warning or error |
| `## EditorConfig settings` | Table: `\| Key \| Value \| Why \|`; any .editorconfig key |
| `## Custom rules` | One `### ID: Title` per rule, then `- severity:`, `- files:`, `- exclude:`, `- pattern:` (regex) or `- text:` (plain), `- message:`, optional `- replace:` (auto-fix, `$1` for groups), `- ignore-comments:` |
| `## AI review context` | Facts (not rules) sent with every AI review, e.g. "Primary constructors are our DI style". The C# version and target framework are added automatically |
| `## AI review guidelines` | Bullet list; each bullet becomes G1, G2, … |

See the generated `CodeQualityRules.md` for a complete example.

#### GitHub Copilot instruction files

If the repository has a `.github/instructions` folder (searched from the solution folder up to the repository root), the Team Rules window reads every `*.instructions.md` file in it:

- Each file's bullets appear in the **Rules** tab, grouped by file, with IDs like `dotnet §3.12` (section 3, bullet 12). Bullets under "Prefer:" / "Avoid:" keep that lead-in.
- **AI review uses only the instruction files whose `applyTo` matches the reviewed file**, the same way Copilot attaches them. For example, a controller gets `dotnet` + `security` + `webapi`, and a stored procedure gets `sqlserver`.
- Process sections that can't be judged from one file (Scope, Project Inspection, Pipeline, Deployment, Versioning, Packaging, Dependencies and similar) are shown with a grey **process** tag and left out of AI review.
- Many rules are sent in batches of 40 per request (`guidelinesPerRequest` in settings.json).
- Under the gear icon: switch Copilot instructions off entirely, or untick individual files (e.g. review with `security` only).
- Files outside C# projects (`.sql`, `.cshtml`, `.razor`, `.xaml`, `.yml`, …) can be AI-reviewed too; their findings show the suggested change and are fixed by hand.
- The window reloads when the instruction files change.

#### AI providers: OpenAI, Anthropic, Ollama

AI review and Code Compass use the AI provider **you** choose, with **your own** API key, model names and API URL.
Nothing is preconfigured and no other server is involved: requests go straight from Visual Studio to that provider.

1. Click the gear icon (⚙) in the Team Rules window and click **AI providers…** (or in Code Compass: gear icon > **Configure…**).
2. Tick the providers you want and fill in:

| Provider | API URL (leave empty for the default) | API key | Models |
|---|---|---|---|
| OpenAI | `https://api.openai.com/v1`, or any OpenAI-compatible API (LM Studio, vLLM, OpenRouter, a proxy...) | required | e.g. `gpt-4.1, gpt-4.1-mini` |
| Anthropic | `https://api.anthropic.com/v1` | required | e.g. `claude-sonnet-5-5` |
| Ollama | `http://localhost:11434`, or your Ollama server | optional (only if your Ollama sits behind an authenticating proxy) | any model you pulled, e.g. `qwen2.5-coder:7b` |

3. **Test connection** checks the URL and key and shows the models the provider lists. **Save** stores everything.

- The model dropdowns show the model names you typed first, then the models the provider lists (`/v1/models`, or Ollama's `/api/tags`). New models need no extension update; for Ollama, `ollama pull <model>` and **Load models** is enough.
- Settings are stored for your Windows account in `%LOCALAPPDATA%\DotNetCodeQualityPack\ai-providers.json`. API keys are encrypted with Windows DPAPI (current user only), never shown again, and never written to logs or source files. **Remove key** deletes a key.
- When the selected model runs on another machine, the window shows that your code leaves this PC. With Ollama on `localhost` it stays on your PC.

| Provider | Request | Authentication |
|---|---|---|
| OpenAI | `{API URL}/chat/completions`, streamed; `response_format: json_object` for reviews | `Authorization: Bearer <key>` |
| Anthropic | `{API URL}/messages`, streamed | `x-api-key: <key>`, `anthropic-version: 2023-06-01` |
| Ollama | `{API URL}/api/chat`, streamed; `format: json` for reviews | none, or `Authorization: Bearer <key>` if you set one |

Providers live behind one interface (`IAiProvider` in `src/DotNetCodeQualityPack/Ai`), so chat, review and settings never contain provider-specific code. Adding a provider means adding one class there.

#### AI review

- Large files are reviewed in parts of 150 lines. Settings live in `%LOCALAPPDATA%\DotNetCodeQualityPack\settings.json` (`chunkLines`, `timeoutSeconds`, `contextSize`).
- To cut false findings, the prompt includes the project's C# version and your context lines, tells the model to skip code that already follows a rule, and the extension drops any suggestion that quotes code not in the file or removes a constructor's parameters.
- **Open rule** (or double-click a rule in the Rules tab) opens the .md file at the line where the rule is written.
- AI findings can be wrong. Every suggested change is shown as a preview and is only applied when you approve it.

#### Token usage

The **Token usage** section under the AI review button shows how many tokens AI review has used. Its one-line header (for example *Token usage · last review 4.2k · today 15.5k*) expands to a table:

| Row | What it counts |
|---|---|
| Last review | The most recent AI review: model, file, input, output and total tokens, number of requests |
| Today | All reviews today on this computer (resets at midnight) |
| This session | Since Visual Studio started |
| All time | Since the first review, or since you last clicked **Reset counters** |

- Counts come from each server's reply: Ollama `prompt_eval_count` / `eval_count`, OpenAI `usage.prompt_tokens` / `completion_tokens`, Anthropic `usage.input_tokens` / `output_tokens` (cached input included). If a server doesn't report them, the request is estimated at about 4 characters per token and marked with ≈.
- Each completed request is counted. A request that fails or that you stop part-way is not counted, because the server reports usage only at the end.
- The status line after each review also shows its tokens, the progress text shows a running total, and the section lists all-time usage per model.
- Only the counts are saved, in `settings.json` under `usage`. No code, prompts or keys are stored.

### Code Compass window (AI chat and code review)

**Tools > .NET Code Quality > Open Code Compass (AI Chat & Review)** (also under View > Other Windows) docks a window with two tabs. It uses the same AI providers as Team Rules (OpenAI, Anthropic, Ollama), set up under the gear icon > **Configure…**.

**Chat**
- Pick any model in the dropdown at the top. Answers stream in as they are written.
- Press Ctrl+V in the message box to attach a screenshot or copied files. You can also use the paperclip button, or drag files onto the window.
- The `</>` button attaches the code selected in the editor.
- Image review needs a model that can read images: an Ollama vision model (for example `llava` or `llama3.2-vision`), an OpenAI GPT-4o/GPT-5 model, or any Claude model. The window warns you when the selected model is text-only.
- Answers are formatted: headings, numbered and nested lists, **tables** (severity cells in colour), quotes and links.
- Code blocks are coloured like the Visual Studio 2022 editor, for C#, SQL, JSON, XML/XAML/HTML, JavaScript/TypeScript, PowerShell, Python, shell and diffs. The colours follow the window theme: VS Dark colours for Dark, VS Light colours for Light, a tuned set for Midnight Blue, and the matching set when the theme is Visual Studio. Long lines scroll sideways (Shift + mouse wheel).
- Every message and code block has **Copy**. Code blocks also have **Insert in editor**, which replaces the selection; Ctrl+Z undoes it.
- Chats are saved per solution (or per folder when no `.sln` is open) and reopen where you left off. The last 20 messages are sent back to the model so the discussion continues. Older chats are in the dropdown.

**Code Review**
- Choose what to review: the selection, the open file, or code you paste in. Then click **Review code**. You can also right-click in the editor and choose **Review Selection with Code Compass**.
- The review checks against your rules:
  - the AI review guidelines and custom rules in `CodeQualityRules.md`
  - the `.github/instructions/*.instructions.md` files that apply to the file
  - `docs/coding-best-practices.md` (CQ rule IDs)
- Error-level rules and rules that match what the code uses are sent first, so small models stay within their context window.
- Each finding shows its severity, rule ID, line (click it to jump there), the problem, how to fix it, and a corrected code snippet. The summary card has **Discuss in chat** and **Copy report**. At the end you get a fully improved version of the code with **Replace selection**.

**Window**
- The theme dropdown offers Visual Studio (follows the IDE theme), Dark, Light and Midnight Blue.
- Ctrl + mouse wheel zooms. Ctrl+0, or clicking the % in the header, resets the zoom.
- The gear icon holds **AI providers** (shared with Team Rules), how many earlier messages the chat sends, and a link to Team Rules for the rules file.
- Tokens used by chat and review are added to the usage counters in Team Rules.
- Chats are stored in `%LOCALAPPDATA%\DotNetCodeQualityPack\CodeCompass`.

### Code Quality Fixer window

**Tools > .NET Code Quality > Open Code Quality Fixer** docks a window next to Solution Explorer.

1. Click **Scan**. The header shows counts of warnings, messages, auto-fixable issues and files.
2. Work **page by page**: each page is one source file. Use the arrows to move between pages.
3. On a page, issues are grouped by rule. Click an issue to see a **before/after preview** of its fix (red = removed, green = added). Warnings with no automatic fix show the code with the line highlighted.
4. Choose what to do:
   - **Apply fix**: applies just this one, then moves to the next issue.
   - **Skip**: leaves it and moves on.
   - **Go to code** (or double-click): opens the file at that line.
   - **Fix ticked on page…**: previews every ticked rule on this page together; **Approve & apply** makes the changes.
   - **Fix rule in solution…**: previews one rule's fix across every file; approve to apply.
5. **Undo** (top-right arrow) reverts the last applied change. Files you edited since are left alone.

Nothing is changed until you approve a preview. Filters narrow the list by severity, auto-fixable only, or text.

### Auto-Fix and Undo

Auto-Fix saves all files, backs up every `.cs` file to `%LOCALAPPDATA%\DotNetCodeQualityPack\Backups` (outside your solution, so the copies are never compiled), then runs `dotnet format <solution> --severity info`. The Output window shows progress, the number of fixes per rule and the list of changed files.

- **Review:** View > Git Changes shows each changed file as a diff.
- **Undo:** Tools > .NET Code Quality > Undo Last Auto-Fix restores the changed files. Files you edited after the auto-fix are skipped, so later work is never lost.
- Needs the .NET SDK (`dotnet.exe`), which Visual Studio normally installs. Classic .NET Framework projects may not load in `dotnet format`; for those use **Ctrl+. > Fix all occurrences in: Solution** in the editor.

### What Apply writes at the solution root

| File | Purpose |
|---|---|
| `CodeQuality.props` | Package versions (one place to update) and defaults |
| `CodeQuality.targets` | Adds the analyzer packages to each C# project |
| `Directory.Build.props` / `.targets` | Created if missing; if they exist, **one import line is added** after a timestamped backup |
| `.editorconfig` | Gets a marked **".NET Code Quality Pack rules"** block with rule severities (noisy StyleCop rules off, key security rules as errors). Created with formatting basics if you don't have one. Rules you already set yourself are never overridden |

Nothing you already have is overwritten. Apply is safe to run again; it only adds what's missing.

> Upgrading from 1.0.0: run Apply again to add the rules block. The old `CodeQuality.globalconfig` is no longer used; Remove deletes it.

## How it handles .NET Core vs .NET Framework

MSBuild automatically imports `Directory.Build.props` and `Directory.Build.targets` from the solution folder into every project, both SDK-style and classic `.csproj`. Roslyn analyzers are compiler plugins, so they run the same way whatever the target framework.

| Project type | Result |
|---|---|
| SDK-style (`net8.0`, `net10.0`, `netcoreapp3.1`, `netstandard2.0`, SDK-style `net48`, multi-targeted) | Works as is |
| Classic .NET Framework `.csproj` with PackageReference or no packages | Works (`RestoreProjectStyle` is set to PackageReference) |
| Classic `.csproj` with **packages.config** | Skipped. Right-click `packages.config` > *Migrate packages.config to PackageReference...*, then apply again |
| Project outside the solution folder | Not reached by the solution's Directory.Build files; reported by Check |
| Project with its own nested `Directory.Build.props/.targets` | Reported by Check, with the exact import line to add |
| Solution using Central Package Management | Handled automatically with `VersionOverride` |

## Customizing

- **Opt one project out:** add `<EnableCodeQualityAnalyzers>false</EnableCodeQualityAnalyzers>` to its `.csproj`.
- **Update analyzer versions:** edit the four version properties in `CodeQuality.props`.
- **Change a rule's severity:** edit its line in the rules block of `.editorconfig`, or add one, e.g. `dotnet_diagnostic.SA1101.severity = none`. Or right-click the rule under *Dependencies > Analyzers* in Solution Explorer.
- **Already reference one of the analyzers in a project?** It isn't added twice; your project's own version wins.
- **Change the bundled extensions:** edit `DotNetCodeQualityPack.vsext`.

## Troubleshooting

- **Warning CS8032 / "analyzer references a newer compiler version":** that analyzer version needs a newer Visual Studio. Update VS 2022, or lower the version in `CodeQuality.props`.
- **Thousands of warnings on an old codebase:** expected. Start by setting whole categories to `suggestion` in `.editorconfig`, then promote rules to `warning`/`error` as the team cleans up.
- **Analyzers not showing after Apply:** reload the solution (or restart VS) and build once so NuGet restores.

## Bundled extension IDs (VS 2022)

| Extension | vsixId |
|---|---|
| Roslynator 2022 | `f326ee1b-81a6-4577-b251-1f29d391cef3` |
| SonarQube for Visual Studio | `SonarLint.0DE19254-1DCA-4479-8EAF-58E5D677FF4D` |
| CodeMaid VS2022 | `9079e73d-3fbb-4e07-8dab-f44fa5d8e8b5` |
| Fine Code Coverage 2022 | `FineCodeCoverage2022.ad53fb28-7a11-4465-a27a-3550499ea4a1` |

For Visual Studio 2026, make a copy of this project with the VS 2026 versions of these extensions and set the manifest range to `[18.0,19.0)`.

## Project layout

```
DotNetCodeQualityPack.sln
docs/coding-best-practices.md     team .NET rule set (CQ rule IDs) used by Code Compass reviews
.github/copilot-instructions.md   GitHub Copilot code review instructions (+ .github/instructions/*.instructions.md)
installer/                        Build-Installers (builds the .vsix + Setup.exe), Install/Uninstall scripts, Setup.iss
.github/workflows/                optional cloud build of the installers
src/DotNetCodeQualityPack/
  source.extension.vsixmanifest   VSIX identity, VS 2022 target
  DotNetCodeQualityPack.vsext     extension pack list
  VSCommandTable.vsct / .cs       menu and command IDs
  DotNetCodeQualityPackPackage.cs package entry point
  Commands/                       Check, Apply, Remove, Auto-Fix, Undo, Open Fixer / Team Rules / Code Compass + shared workflow
  Fixer/                          Roslyn engine: analysis, code fixes, previews, diff, undo
  Rules/                          Markdown rules parser, .editorconfig team block, custom pattern rules
  Ai/                             provider-neutral AI layer: IAiProvider, request/model types, user provider settings (DPAPI keys),
                                  model catalog, token usage, JSON helper, AI review client
  Ai/Providers/                   OpenAIProvider, AnthropicProvider, OllamaProvider
  ToolWindows/                    Code Quality Fixer and Team Rules windows (WPF views + view models)
  CodeCompass/                    Code Compass window (UI only): streaming chat, code review, themes, chat history,
                                  Markdown + syntax colouring, AI provider settings dialog
  Services/                       project scanning, file writing, backups, dotnet format runner, settings
  Templates/                      files written into solutions
```

Built on the [Community.VisualStudio.Toolkit](https://github.com/VsixCommunity/Community.VisualStudio.Toolkit).
