# Code Compass — .NET Coding Best Practices Rule Set

> **Version:** 1.0 · **Applies to:** C# on .NET (Core / 5+) and .NET Framework 4.x
> **Owner:** Technical Architecture · **Last updated:** 2026-10-02

## How to read this file

Every rule follows the same structure so it can be read by people and parsed by tools:

```
### <RULE-ID>: <Short title>
- **Severity:** Error | Warning | Info
- **Applies to:** All | .NET Core | .NET Framework
- **Rule:** what must (or must not) be done
- **Why:** the reason, in one or two lines
Bad / Good code examples (optional)
```

**Severity meaning**

| Severity | Meaning | Review action |
|---|---|---|
| Error | Bug, security hole, or data-loss risk | Must fix before merge |
| Warning | Likely defect, maintainability or performance problem | Fix unless justified in the PR |
| Info | Style or readability improvement | Fix when touching the code |

**Rule ID prefixes:** `NAM` Naming · `STR` Structure & design · `ASY` Async · `EXC` Exceptions · `RES` Resources · `SEC` Security · `PRF` Performance · `DAT` Data access · `API` Web APIs · `LOG` Logging · `CFG` Configuration · `TST` Testing · `RDB` Readability · `FWK` .NET Framework specific

**Suppressing a rule:** add a comment on the line above with the rule ID and a reason, e.g.
`// cq-ignore CQ-PRF-002: list is always < 10 items`. Suppressions without a reason are not accepted.

---

## 1. Naming

### CQ-NAM-001: Use standard .NET casing
- **Severity:** Warning
- **Applies to:** All
- **Rule:** PascalCase for types, methods, properties, events, constants and namespaces. camelCase for parameters and locals. `_camelCase` for private fields. Interfaces start with `I`.
- **Why:** Consistent casing lets anyone on the team read any file without friction.

```csharp
// Bad
public class order_service { private int Count; public void getorder(int OrderId) { } }

// Good
public class OrderService { private int _count; public void GetOrder(int orderId) { } }
```

### CQ-NAM-002: Names describe intent, not type
- **Severity:** Info
- **Applies to:** All
- **Rule:** No single-letter names (except loop counters and lambdas), no Hungarian notation (`strName`, `iCount`), no vague names (`data`, `temp`, `obj`, `manager2`).
- **Why:** The name should tell the reader what the value means.

```csharp
// Bad
var d = DateTime.Now - s; string strCust = GetData();

// Good
var elapsed = DateTime.Now - startTime; string customerName = GetCustomerName();
```

### CQ-NAM-003: Async methods end with `Async`
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Any method returning `Task`, `Task<T>`, `ValueTask` or `ValueTask<T>` has the `Async` suffix. Controller actions and event handlers are exempt.
- **Why:** Callers can see at a glance that the method must be awaited.

### CQ-NAM-004: Booleans read as questions
- **Severity:** Info
- **Applies to:** All
- **Rule:** Prefix booleans with `is`, `has`, `can`, `should`. Avoid negative names (`isNotValid`).
- **Why:** `if (!isNotValid)` is a double negative and a common source of bugs.

---

## 2. Structure & design

### CQ-STR-001: One public type per file
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Each public class, interface, record or enum lives in its own file, named after the type.
- **Why:** Makes types easy to find and keeps merge conflicts small.

### CQ-STR-002: Keep methods small and focused
- **Severity:** Warning
- **Applies to:** All
- **Rule:** A method does one thing. Aim for under 40 lines and cyclomatic complexity under 10. Extract helper methods when it grows.
- **Why:** Small methods are easier to test, review and name.

### CQ-STR-003: Limit method parameters
- **Severity:** Info
- **Applies to:** All
- **Rule:** More than 4 parameters → group them into a request/options object or record.
- **Why:** Long parameter lists are easy to call in the wrong order.

```csharp
// Bad
CreateUser(string first, string last, string email, string phone, int roleId, bool active);

// Good
CreateUser(CreateUserRequest request);
```

### CQ-STR-004: Depend on abstractions, inject dependencies
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Services receive their dependencies through the constructor (interfaces where a seam is useful). Do not `new` up services, repositories or `HttpClient` inside business logic. No service locator (`IServiceProvider.GetService` in business code).
- **Why:** Enables unit testing and keeps classes loosely coupled.

```csharp
// Bad
public class OrderService { private readonly OrderRepository _repo = new OrderRepository(); }

// Good
public class OrderService
{
    private readonly IOrderRepository _repo;
    public OrderService(IOrderRepository repo) => _repo = repo;
}
```

### CQ-STR-005: No magic numbers or strings
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Replace literal values that carry meaning with named constants, enums or configuration.
- **Why:** `if (status == 3)` hides business meaning and is easy to break.

```csharp
// Bad
if (order.Status == 3) { ... }

// Good
if (order.Status == OrderStatus.Shipped) { ... }
```

### CQ-STR-006: Prefer immutability
- **Severity:** Info
- **Applies to:** All
- **Rule:** Mark fields `readonly` when not reassigned. Use `record` / `init` for DTOs and value objects. Expose collections as `IReadOnlyList<T>` / `IReadOnlyCollection<T>`.
- **Why:** Fewer moving parts means fewer state bugs and safer concurrency.

### CQ-STR-007: No dead or commented-out code
- **Severity:** Info
- **Applies to:** All
- **Rule:** Delete unused methods, variables, `using` directives and commented-out blocks. Source control keeps the history.
- **Why:** Dead code misleads readers and rots.

### CQ-STR-008: Enable nullable reference types in new projects
- **Severity:** Warning
- **Applies to:** .NET Core
- **Rule:** New projects set `<Nullable>enable</Nullable>`. Do not silence warnings with `!` unless the value is provably non-null; add a comment when you do.
- **Why:** Catches `NullReferenceException` at compile time.

---

## 3. Async & concurrency

### CQ-ASY-001: Never block on async code
- **Severity:** Error
- **Applies to:** All
- **Rule:** Do not call `.Result`, `.Wait()` or `.GetAwaiter().GetResult()` on a Task. Use `await` all the way up.
- **Why:** Causes deadlocks (especially in ASP.NET Framework and UI apps) and thread-pool starvation.

```csharp
// Bad
var user = _userService.GetUserAsync(id).Result;

// Good
var user = await _userService.GetUserAsync(id);
```

### CQ-ASY-002: No `async void`
- **Severity:** Error
- **Applies to:** All
- **Rule:** Async methods return `Task` / `Task<T>`. `async void` is allowed only for UI event handlers.
- **Why:** Exceptions in `async void` cannot be caught and crash the process.

### CQ-ASY-003: Pass and honor `CancellationToken`
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Public async methods that do I/O accept a `CancellationToken` (last parameter, default allowed) and pass it to every downstream async call.
- **Why:** Lets abandoned requests stop work and free resources.

### CQ-ASY-004: Use `ConfigureAwait(false)` in library code
- **Severity:** Info
- **Applies to:** All
- **Rule:** Shared class libraries use `ConfigureAwait(false)` on awaits. Not required in ASP.NET Core app code.
- **Why:** Avoids capturing a sync context and prevents deadlocks for .NET Framework / UI consumers.

### CQ-ASY-005: Don't fire-and-forget without handling
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Do not start a Task and discard it (`_ = DoWorkAsync();`) unless errors are logged inside it. Prefer a background queue / `IHostedService` for background work.
- **Why:** Unobserved exceptions are silently lost.

### CQ-ASY-006: Protect shared state
- **Severity:** Error
- **Applies to:** All
- **Rule:** Shared mutable state accessed from multiple threads uses `lock`, `SemaphoreSlim` (for async) or concurrent collections. Never `lock` on `this`, a `Type` or a string.
- **Why:** Prevents race conditions and data corruption.

---

## 4. Exceptions & error handling

### CQ-EXC-001: Never swallow exceptions
- **Severity:** Error
- **Applies to:** All
- **Rule:** No empty `catch` blocks. A catch must log, handle meaningfully, or rethrow.
- **Why:** Silent failures turn into hours of debugging.

```csharp
// Bad
try { SaveOrder(order); } catch { }

// Good
try { SaveOrder(order); }
catch (SqlException ex)
{
    _logger.LogError(ex, "Failed to save order {OrderId}", order.Id);
    throw;
}
```

### CQ-EXC-002: Rethrow with `throw;`, not `throw ex;`
- **Severity:** Error
- **Applies to:** All
- **Rule:** To rethrow the caught exception use `throw;`. To add context, wrap it: `throw new XyzException("...", ex);`.
- **Why:** `throw ex;` resets the stack trace.

### CQ-EXC-003: Catch specific exceptions
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Catch the most specific exception type you can handle. Catching `Exception` is allowed only at top-level boundaries (middleware, global handlers, background job loops).
- **Why:** Broad catches hide bugs you did not expect.

### CQ-EXC-004: Don't use exceptions for control flow
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Use `TryParse`, `TryGetValue`, null checks or result objects for expected outcomes.
- **Why:** Exceptions are expensive and obscure normal logic.

```csharp
// Bad
try { id = int.Parse(input); } catch (FormatException) { id = 0; }

// Good
if (!int.TryParse(input, out var id)) { id = 0; }
```

### CQ-EXC-005: Validate arguments at public boundaries
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Public methods validate inputs and throw `ArgumentNullException` / `ArgumentException` / `ArgumentOutOfRangeException` with the parameter name. On .NET 6+ use `ArgumentNullException.ThrowIfNull(x)`.
- **Why:** Fails fast with a clear message instead of a deep `NullReferenceException`.

---

## 5. Resource management

### CQ-RES-001: Dispose `IDisposable` objects
- **Severity:** Error
- **Applies to:** All
- **Rule:** Wrap `IDisposable` objects you create (connections, streams, readers, commands) in `using` / `await using`.
- **Why:** Prevents connection-pool exhaustion, file locks and memory leaks.

```csharp
// Bad
var conn = new SqlConnection(cs); conn.Open(); /* ... */

// Good
using var conn = new SqlConnection(cs);
await conn.OpenAsync(ct);
```

### CQ-RES-002: Don't create `HttpClient` per request
- **Severity:** Error
- **Applies to:** All
- **Rule:** Use `IHttpClientFactory` (.NET Core) or a single static/long-lived `HttpClient` (.NET Framework). Never `new HttpClient()` inside a method that runs per request.
- **Why:** Causes socket exhaustion under load.

### CQ-RES-003: Unsubscribe from events
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Objects that subscribe to events on longer-lived objects unsubscribe in `Dispose` (or use weak events).
- **Why:** Event subscriptions keep subscribers alive — a classic memory leak.

---

## 6. Security

### CQ-SEC-001: No hard-coded secrets
- **Severity:** Error
- **Applies to:** All
- **Rule:** Passwords, API keys, connection strings with credentials and tokens never appear in source code or committed config files. Use User Secrets locally and a vault / environment variables in deployed environments.
- **Why:** Secrets in source control are effectively public.

```csharp
// Bad
var apiKey = "sk-live-XXXXXXXXXXXXXXXX"; // placeholder: never commit a real key

// Good
var apiKey = _configuration["PaymentGateway:ApiKey"];
```

### CQ-SEC-002: Parameterize all SQL
- **Severity:** Error
- **Applies to:** All
- **Rule:** Never build SQL by concatenating or interpolating user input. Use parameters, an ORM, or `FromSqlInterpolated` / `SqlQuery` with interpolation in EF Core.
- **Why:** Prevents SQL injection.

```csharp
// Bad
var sql = "SELECT * FROM Users WHERE Email = '" + email + "'";

// Good
var cmd = new SqlCommand("SELECT * FROM Users WHERE Email = @email", conn);
cmd.Parameters.Add("@email", SqlDbType.NVarChar, 256).Value = email;
```

### CQ-SEC-003: Validate and encode all external input
- **Severity:** Error
- **Applies to:** All
- **Rule:** Validate input from users, files, queues and third-party APIs (type, length, range, allow-list). Encode output rendered into HTML/JS. Never pass user input directly to file paths, `Process.Start`, or deserializers.
- **Why:** Prevents injection, XSS and path traversal.

### CQ-SEC-004: Don't log sensitive data
- **Severity:** Error
- **Applies to:** All
- **Rule:** Never log passwords, tokens, full card numbers, national IDs or full request bodies containing personal data. Mask when an identifier is needed.
- **Why:** Logs are widely accessible and often retained for years.

### CQ-SEC-005: Use safe serializers
- **Severity:** Error
- **Applies to:** All
- **Rule:** Do not use `BinaryFormatter`, `NetDataContractSerializer` or `TypeNameHandling` other than `None` in Newtonsoft.Json for untrusted data.
- **Why:** These allow remote code execution through crafted payloads.

### CQ-SEC-006: Use strong cryptography from the framework
- **Severity:** Error
- **Applies to:** All
- **Rule:** No MD5/SHA1 for security purposes, no custom crypto. Hash passwords with PBKDF2 / ASP.NET Identity's `PasswordHasher`. Use `RandomNumberGenerator` (not `Random`) for tokens.
- **Why:** Weak or home-made crypto is easily broken.

### CQ-SEC-007: Authorize every endpoint
- **Severity:** Error
- **Applies to:** All
- **Rule:** Every controller/endpoint has an explicit `[Authorize]` (or policy) or an explicit `[AllowAnonymous]`. Check resource ownership, not just authentication.
- **Why:** Prevents broken access control (OWASP #1).

---

## 7. Performance

### CQ-PRF-001: Use `StringBuilder` for string building in loops
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Concatenating strings inside a loop uses `StringBuilder` or `string.Join`.
- **Why:** Repeated `+=` allocates a new string each time.

### CQ-PRF-002: Avoid multiple enumeration
- **Severity:** Warning
- **Applies to:** All
- **Rule:** If an `IEnumerable<T>` is enumerated more than once, materialize it once with `.ToList()` / `.ToArray()`.
- **Why:** Each enumeration may re-run a query or expensive iterator.

```csharp
// Bad
var active = users.Where(u => u.IsActive);
if (active.Any()) Send(active.Count(), active);

// Good
var active = users.Where(u => u.IsActive).ToList();
if (active.Count > 0) Send(active.Count, active);
```

### CQ-PRF-003: Use the right LINQ method
- **Severity:** Info
- **Applies to:** All
- **Rule:** Use `Any()` instead of `Count() > 0`; use `FirstOrDefault(predicate)` instead of `Where(...).FirstOrDefault()`; use `Count` / `Length` properties on collections and arrays.
- **Why:** Avoids unnecessary iteration.

### CQ-PRF-004: Pick the right collection
- **Severity:** Info
- **Applies to:** All
- **Rule:** Use `HashSet<T>` / `Dictionary<TKey,TValue>` for lookups instead of `List<T>.Contains` / `.Find` in loops. Pre-size collections when the size is known.
- **Why:** O(1) lookup instead of O(n) inside an O(n) loop.

### CQ-PRF-005: Don't do I/O in loops when a batch exists
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Avoid calling the database or an HTTP API once per item. Fetch or save in a batch.
- **Why:** N round-trips are the most common cause of slow endpoints.

---

## 8. Data access (EF / ADO.NET)

### CQ-DAT-001: Avoid N+1 queries
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Load related data with `Include` / projection in a single query instead of lazy-loading inside loops.
- **Why:** One query per row kills database performance.

```csharp
// Bad
foreach (var order in db.Orders.ToList()) Console.WriteLine(order.Customer.Name); // lazy load each time

// Good
var rows = db.Orders.Select(o => new { o.Id, CustomerName = o.Customer.Name }).ToList();
```

### CQ-DAT-002: Use `AsNoTracking` for read-only queries
- **Severity:** Info
- **Applies to:** All
- **Rule:** Queries whose results are not updated use `AsNoTracking()` (EF Core / EF6).
- **Why:** Reduces memory and CPU spent on change tracking.

### CQ-DAT-003: Filter and page in the database
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Apply `Where`, `OrderBy`, `Skip`, `Take` before `ToList()`. Never load a full table into memory to filter it. List endpoints must be paged.
- **Why:** Pulling whole tables does not scale.

### CQ-DAT-004: Keep DbContext lifetime short
- **Severity:** Warning
- **Applies to:** All
- **Rule:** `DbContext` is scoped per request / unit of work. Never register it as a singleton or share it across threads.
- **Why:** `DbContext` is not thread-safe and grows unbounded.

### CQ-DAT-005: Use transactions for multi-step writes
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Writes that must succeed or fail together run in a single `SaveChanges` call or an explicit transaction.
- **Why:** Prevents partial updates and inconsistent data.

---

## 9. Web APIs

### CQ-API-001: Keep controllers thin
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Controllers handle HTTP concerns only (binding, validation, status codes). Business logic lives in services.
- **Why:** Logic in controllers can't be reused or unit-tested easily.

### CQ-API-002: Don't expose entities directly
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Accept and return DTOs / view models, not EF entities.
- **Why:** Prevents over-posting, leaking internal fields and accidental lazy-load serialization.

### CQ-API-003: Return correct status codes and consistent errors
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Use proper codes (200/201/204/400/401/403/404/409/500). Return errors in one consistent shape (`ProblemDetails` on .NET Core). Never return stack traces to clients.
- **Why:** Clients can handle errors predictably; internals stay private.

### CQ-API-004: Handle errors globally
- **Severity:** Info
- **Applies to:** All
- **Rule:** Use exception-handling middleware / `IExceptionHandler` (.NET Core) or an `ExceptionFilter` / `Application_Error` (.NET Framework) rather than try/catch in every action.
- **Why:** One place to log and shape errors.

---

## 10. Logging

### CQ-LOG-001: Use structured logging
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Use `ILogger` message templates with named placeholders. Do not use string interpolation or concatenation in log messages.
- **Why:** Keeps properties searchable and avoids formatting cost when the level is disabled.

```csharp
// Bad
_logger.LogInformation($"Order {order.Id} placed by {userId}");

// Good
_logger.LogInformation("Order {OrderId} placed by {UserId}", order.Id, userId);
```

### CQ-LOG-002: Log the exception object
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Pass the exception as the first argument (`LogError(ex, "...")`), not `ex.Message`.
- **Why:** Keeps the stack trace and inner exceptions.

### CQ-LOG-003: Use the right log level
- **Severity:** Info
- **Applies to:** All
- **Rule:** `Trace`/`Debug` for diagnostics, `Information` for business events, `Warning` for recoverable issues, `Error` for failed operations, `Critical` for outages. No `Console.WriteLine` in application code.
- **Why:** Correct levels keep production logs useful and affordable.

---

## 11. Configuration

### CQ-CFG-001: Use strongly typed options
- **Severity:** Info
- **Applies to:** .NET Core
- **Rule:** Bind configuration sections to classes with `IOptions<T>` and validate them at startup (`ValidateDataAnnotations().ValidateOnStart()`).
- **Why:** Typos and missing settings fail at startup, not at 2 a.m.

### CQ-CFG-002: No environment-specific values in code
- **Severity:** Warning
- **Applies to:** All
- **Rule:** URLs, paths, timeouts and feature flags come from configuration per environment, not `#if DEBUG` or hard-coded checks on machine names.
- **Why:** The same build must run in every environment.

---

## 12. Testing

### CQ-TST-001: Test business logic
- **Severity:** Warning
- **Applies to:** All
- **Rule:** New or changed business logic ships with unit tests. Bug fixes include a test that reproduces the bug.
- **Why:** Prevents regressions.

### CQ-TST-002: Tests are independent and readable
- **Severity:** Info
- **Applies to:** All
- **Rule:** Follow Arrange–Act–Assert. Name tests `Method_Scenario_ExpectedResult`. No shared mutable state between tests, no real network/database calls in unit tests, no `Thread.Sleep`.
- **Why:** Tests should be fast, deterministic and self-explanatory.

---

## 13. Readability & style

### CQ-RDB-001: Avoid deep nesting
- **Severity:** Info
- **Applies to:** All
- **Rule:** Use guard clauses / early returns. Keep nesting to 3 levels or fewer.
- **Why:** Flat code is easier to follow.

```csharp
// Bad
if (order != null) { if (order.IsPaid) { if (order.Items.Any()) { Ship(order); } } }

// Good
if (order is null || !order.IsPaid || !order.Items.Any()) return;
Ship(order);
```

### CQ-RDB-002: Comments explain why, not what
- **Severity:** Info
- **Applies to:** All
- **Rule:** Write comments for intent, business rules and non-obvious decisions. Public APIs in shared libraries have XML doc comments. Remove comments that just restate the code.
- **Why:** Good names explain what; comments should add what code cannot.

### CQ-RDB-003: Use `var` when the type is obvious
- **Severity:** Info
- **Applies to:** All
- **Rule:** Use `var` when the type is clear from the right-hand side (`new`, cast, well-named factory). Use explicit types otherwise.
- **Why:** Balances brevity with clarity.

### CQ-RDB-004: Always use braces
- **Severity:** Info
- **Applies to:** All
- **Rule:** `if`, `else`, `for`, `foreach`, `while` always use braces, even for single statements.
- **Why:** Prevents bugs when a second line is added later.

### CQ-RDB-005: Use `DateTime` correctly
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Store and compare times in UTC (`DateTime.UtcNow` or `DateTimeOffset`). Convert to local time only for display. Inject a clock (`TimeProvider` on .NET 8+) where time matters for tests.
- **Why:** Avoids time-zone and daylight-saving bugs.

### CQ-RDB-006: Compare strings explicitly
- **Severity:** Warning
- **Applies to:** All
- **Rule:** Use `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)` (or the appropriate comparison) instead of `a.ToLower() == b.ToLower()`. Use `CultureInfo.InvariantCulture` for machine-readable formatting and parsing.
- **Why:** Avoids culture bugs and extra allocations.

---

## 14. .NET Framework specific

### CQ-FWK-001: Don't block in ASP.NET (System.Web)
- **Severity:** Error
- **Applies to:** .NET Framework
- **Rule:** In ASP.NET MVC / Web API on .NET Framework, never use `.Result` / `.Wait()`; use async actions end-to-end (see CQ-ASY-001).
- **Why:** The `AspNetSynchronizationContext` makes blocking calls deadlock.

### CQ-FWK-002: Keep secrets out of web.config / app.config
- **Severity:** Error
- **Applies to:** .NET Framework
- **Rule:** Credentials in `web.config` / `app.config` are moved to environment-specific config transforms with secrets injected at deploy time, or encrypted config sections.
- **Why:** Config files are committed and copied to many servers.

### CQ-FWK-003: Plan for migration
- **Severity:** Info
- **Applies to:** .NET Framework
- **Rule:** New shared libraries target `netstandard2.0` (or multi-target) when possible. Avoid new dependencies on `System.Web`, WCF server, Remoting or AppDomains in new code.
- **Why:** Keeps the path to modern .NET open.

---

## Pull request checklist

Before raising a PR, confirm:

- [ ] No `Error`-severity rule violations
- [ ] Every `Warning` is fixed or justified in the PR description
- [ ] No secrets, credentials or personal data in code, config or logs
- [ ] New logic has unit tests; all tests pass
- [ ] No compiler warnings introduced
- [ ] Async code is awaited end-to-end, with cancellation tokens passed through
- [ ] Code formatted with the team `.editorconfig`
