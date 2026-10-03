---
applyTo: "**/*.cs"
---

# Design, naming and readability rules (CQ-NAM, CQ-STR, CQ-RDB)

Comment format: `[Severity] RULE-ID: problem. Fix: ...`
These are lower priority than security, async and exception issues. Do not leave more than 5 Info comments per file.

## Naming

- **CQ-NAM-001 [Warning]** PascalCase for types, methods, properties, events, constants and namespaces; camelCase for parameters and locals; `_camelCase` for private fields; interfaces start with `I`.
- **CQ-NAM-002 [Info]** No single-letter names (except loop counters and lambda parameters), no Hungarian notation (`strName`, `iCount`), no vague names (`data`, `temp`, `obj`, `helper2`).
- **CQ-NAM-004 [Info]** Booleans start with `is`, `has`, `can` or `should`; avoid negative names like `isNotValid`.

## Structure and design

- **CQ-STR-001 [Warning]** One public type per file; the file is named after the type.
- **CQ-STR-002 [Warning]** Methods over ~40 lines or with deeply branching logic. Suggest extracting focused helper methods.
- **CQ-STR-003 [Info]** More than 4 parameters. Suggest a request/options class or record.
- **CQ-STR-004 [Warning] Inject dependencies.** Flag `new` of services, repositories, `DbContext` or `HttpClient` inside business classes, and `IServiceProvider.GetService` / `GetRequiredService` outside composition root or factories. Fix: constructor injection.

```csharp
// Bad
private readonly OrderRepository _repo = new OrderRepository();
// Good
public OrderService(IOrderRepository repo) => _repo = repo;
```

- **CQ-STR-005 [Warning] No magic numbers or strings.** Flag literals with business meaning (status codes, role names, limits, URLs). `0`, `1`, `-1` and empty strings are fine. Fix: named constant, enum or configuration.
- **CQ-STR-006 [Info]** Fields never reassigned are `readonly`; DTOs use `record` / `init`; public collections are exposed as `IReadOnlyList<T>` / `IReadOnlyCollection<T>`.
- **CQ-STR-007 [Info]** Commented-out code, unused private methods, unused variables or unused `using` directives.
- **CQ-STR-008 [Warning]** In nullable-enabled projects, flag the `!` operator without a comment explaining why the value cannot be null.

## Readability

- **CQ-RDB-001 [Info]** Nesting deeper than 3 levels. Suggest guard clauses / early returns.
- **CQ-RDB-002 [Info]** Comments that only restate the code. Public APIs in shared libraries need XML doc comments.
- **CQ-RDB-004 [Info]** `if`, `else`, `for`, `foreach`, `while` without braces.
- **CQ-RDB-005 [Warning] Dates.** Flag `DateTime.Now` for stored, compared or logged times. Fix: `DateTime.UtcNow`, `DateTimeOffset`, or `TimeProvider` (.NET 8+).
- **CQ-RDB-006 [Warning] String comparison.** Flag `a.ToLower() == b.ToLower()` and `ToUpper()` comparisons. Fix: `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)`. Parsing and formatting of machine data uses `CultureInfo.InvariantCulture`.
