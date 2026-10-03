---
applyTo: "**/*.cs"
---

# Resource, performance and data access rules (CQ-RES, CQ-PRF, CQ-DAT)

Comment format: `[Severity] RULE-ID: problem. Fix: ...`

## Resources

- **CQ-RES-001 [Error] Dispose `IDisposable`.** Flag `SqlConnection`, `SqlCommand`, `DbDataReader`, `Stream`, `StreamReader/Writer`, `FileStream`, `Bitmap` and similar objects created without `using` / `await using`. Objects obtained from DI are disposed by the container; do not flag them.

```csharp
// Bad
var conn = new SqlConnection(cs); conn.Open();
// Good
await using var conn = new SqlConnection(cs);
await conn.OpenAsync(ct);
```

- **CQ-RES-002 [Error] No `HttpClient` per call.** Flag `new HttpClient()` inside methods, loops or `using` blocks. Fix: `IHttpClientFactory` / typed clients (.NET Core) or one static `HttpClient` (.NET Framework).
- **CQ-RES-003 [Warning] Unsubscribe from events.** Flag `+=` on events of longer-lived objects (singletons, static events) with no matching `-=` in `Dispose`.

## Performance

- **CQ-PRF-001 [Warning]** String `+=` inside a loop. Fix: `StringBuilder` or `string.Join`.
- **CQ-PRF-002 [Warning]** An `IEnumerable<T>` / `IQueryable<T>` enumerated more than once (`Any()` then `Count()` then `foreach`). Fix: materialize once with `ToList()`.
- **CQ-PRF-003 [Info]** `Count() > 0` → `Any()`; `Where(p).FirstOrDefault()` → `FirstOrDefault(p)`; `list.Count()` → `list.Count`; `array.Count()` → `array.Length`.
- **CQ-PRF-004 [Info]** `List<T>.Contains` or `.Find` inside a loop over another collection. Fix: `HashSet<T>` or `Dictionary<,>`.
- **CQ-PRF-005 [Warning]** A database or HTTP call inside a `for` / `foreach` loop. Fix: batch the call.

## Data access (EF Core / EF6 / ADO.NET / Dapper)

- **CQ-DAT-001 [Warning] Avoid N+1 queries.** Flag navigation properties used inside a loop over query results. Fix: `Include` or a `Select` projection.

```csharp
// Bad
foreach (var o in db.Orders.ToList()) Print(o.Customer.Name);
// Good
var rows = db.Orders.Select(o => new { o.Id, o.Customer.Name }).ToList();
```

- **CQ-DAT-002 [Info]** Read-only queries use `AsNoTracking()`.
- **CQ-DAT-003 [Warning] Filter and page in the database.** Flag `ToList()` / `AsEnumerable()` called before `Where`, `OrderBy`, `Skip` or `Take`. List endpoints must be paged.
- **CQ-DAT-004 [Warning] Short `DbContext` lifetime.** Flag `DbContext` registered as singleton, stored in a static field, or used across parallel tasks.
- **CQ-DAT-005 [Warning] Multi-step writes are atomic.** Flag related writes across several `SaveChanges` calls or commands with no transaction. Fix: one `SaveChanges`, or `BeginTransaction` / `TransactionScope`.
