---
applyTo: "**/*.cs"
---

# Async and exception rules (CQ-ASY, CQ-EXC, CQ-FWK-001)

Comment format: `[Severity] RULE-ID: problem. Fix: ...`

## Async and concurrency

- **CQ-ASY-001 [Error] Never block on async code.** Flag `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` and `Task.WaitAll` on tasks. Fix: `await`, and make the caller async.

```csharp
// Bad
var user = _users.GetUserAsync(id).Result;
// Good
var user = await _users.GetUserAsync(id, ct);
```

- **CQ-FWK-001 [Error]** On .NET Framework ASP.NET (MVC / Web API / WebForms) blocking on a task deadlocks. Treat CQ-ASY-001 as critical there.
- **CQ-ASY-002 [Error] No `async void`.** Allowed only for UI event handlers `(object sender, EventArgs e)`. Fix: return `Task`.
- **CQ-ASY-003 [Warning] Pass `CancellationToken`.** Public async methods doing I/O accept a `CancellationToken` (last parameter) and pass it to every async call they make.
- **CQ-ASY-004 [Info] `ConfigureAwait(false)` in shared libraries.** Not required in ASP.NET Core application code.
- **CQ-ASY-005 [Warning] No unhandled fire-and-forget.** Flag `_ = SomethingAsync();`, `Task.Run(...)` whose result is ignored, or an un-awaited task, unless errors are caught and logged inside. Fix: await it, or use a background queue / `IHostedService`.
- **CQ-ASY-006 [Error] Protect shared state.** Flag static or singleton mutable fields (`List<T>`, `Dictionary<,>`, counters) changed without a lock. Flag `lock(this)`, `lock(typeof(...))` and `lock("string")`. Use `SemaphoreSlim` instead of `lock` around `await`. Fix: `lock` on a private readonly object, `Interlocked`, or `Concurrent*` collections.
- **CQ-NAM-003 [Warning]** Methods returning `Task` / `ValueTask` end with `Async` (controller actions and event handlers are exempt).

## Exceptions

- **CQ-EXC-001 [Error] Never swallow exceptions.** Flag empty `catch { }`, and catch blocks that only `return null` / `return false` with no logging.

```csharp
// Bad
try { Save(order); } catch { }
// Good
try { Save(order); }
catch (SqlException ex)
{
    _logger.LogError(ex, "Failed to save order {OrderId}", order.Id);
    throw;
}
```

- **CQ-EXC-002 [Error] Rethrow with `throw;`.** Flag `throw ex;`. To add context use `throw new CustomException("...", ex);`.
- **CQ-EXC-003 [Warning] Catch specific exceptions.** Flag `catch (Exception)` except in middleware, global handlers, `Main`, or background job loops.
- **CQ-EXC-004 [Warning] Don't use exceptions for control flow.** Flag try/catch around `Parse`, dictionary indexers or `First()` for expected cases. Fix: `TryParse`, `TryGetValue`, `FirstOrDefault`.
- **CQ-EXC-005 [Warning] Validate public arguments.** Public methods check inputs and throw `ArgumentNullException` / `ArgumentException` / `ArgumentOutOfRangeException` with `nameof(param)`. On .NET 6+ prefer `ArgumentNullException.ThrowIfNull(x)`.
