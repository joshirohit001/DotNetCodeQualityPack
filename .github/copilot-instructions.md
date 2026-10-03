# Code Compass — Code Review Instructions

This repository contains C# code on .NET (Core / 5+) and .NET Framework 4.x.
Review pull requests against the team rule set in `.github/instructions/`.
Rule IDs look like `CQ-SEC-002`.

## How to review

- Review only the changed lines and the code they directly affect.
- Start every comment with the severity and rule ID, e.g. `[Error] CQ-ASY-001: ...`.
- In one or two sentences, explain the problem and why it matters.
- Show the fix as a short code suggestion.
- Severity:
  - **Error**: bug, security hole or data-loss risk. Must be fixed before merge.
  - **Warning**: likely defect, maintainability or performance problem.
  - **Info**: readability or style improvement.
- If an issue is not covered by a rule, label it `[Warning] General` or `[Info] General`.
- Report each issue once. If the same issue repeats in a file, comment on the first occurrence and mention the other line numbers.

## Do not flag

- Formatting already enforced by `.editorconfig` / `dotnet format` (spacing, indentation, line endings).
- Generated code: `*.Designer.cs`, `*.g.cs`, `Migrations/`, `obj/`, `bin/`, `Reference.cs`.
- Code with a `// cq-ignore <RULE-ID>: <reason>` comment above it. If the comment has no reason, flag the missing reason only.
- Personal preferences that no rule covers.

## Top priorities (always check)

1. **CQ-SEC-001** No hard-coded passwords, keys, tokens or connection strings with credentials.
2. **CQ-SEC-002** SQL is never built by concatenating or interpolating input. Use parameters.
3. **CQ-ASY-001** No `.Result`, `.Wait()` or `.GetAwaiter().GetResult()` on tasks.
4. **CQ-ASY-002** No `async void` except UI event handlers.
5. **CQ-EXC-001** No empty or silent `catch` blocks.
6. **CQ-EXC-002** Rethrow with `throw;`, never `throw ex;`.
7. **CQ-RES-001** `IDisposable` objects are wrapped in `using` / `await using`.
8. **CQ-RES-002** No `new HttpClient()` per call. Use `IHttpClientFactory` or a shared instance.
9. **CQ-SEC-004** Do not log passwords, tokens or personal data.
10. **CQ-SEC-007** Every API endpoint has explicit `[Authorize]` or `[AllowAnonymous]`.

## Pull request summary

End the review with a short summary:
- the count of Errors, Warnings and Info items
- whether the PR is safe to merge once the Errors are fixed
- missing unit tests for new business logic (CQ-TST-001)
