---
applyTo: "**/*.cs,**/appsettings*.json,**/*.config"
---

# API, logging, configuration and framework rules (CQ-API, CQ-LOG, CQ-CFG, CQ-FWK)

Comment format: `[Severity] RULE-ID: problem. Fix: ...`

## Web APIs (controllers, minimal APIs, Web API 2)

- **CQ-API-001 [Warning] Thin controllers.** Flag business rules, direct `DbContext` queries or multi-step workflows inside controller actions. Fix: move logic into a service.
- **CQ-API-002 [Warning] No entities over the wire.** Flag actions that accept or return EF entity types. Fix: request/response DTOs.
- **CQ-API-003 [Warning] Correct status codes and error shape.** Flag `200 OK` for failures, `500` for validation errors, and `ex.Message` / `ex.ToString()` returned to the client. Fix: `400/401/403/404/409` as appropriate; `ProblemDetails` on .NET Core.
- **CQ-API-004 [Info]** Identical try/catch blocks repeated in many actions. Fix: exception-handling middleware / `IExceptionHandler` (.NET Core) or an `ExceptionFilterAttribute` (.NET Framework).

## Logging

- **CQ-LOG-001 [Warning] Structured logging.** Flag `$"..."`, `+` or `string.Format` inside `ILogger` calls. Fix: message template with named placeholders.

```csharp
// Bad
_logger.LogInformation($"Order {order.Id} placed by {userId}");
// Good
_logger.LogInformation("Order {OrderId} placed by {UserId}", order.Id, userId);
```

- **CQ-LOG-002 [Warning]** `LogError(ex.Message)` or `LogError("..." + ex)`. Fix: pass the exception first: `LogError(ex, "...")`.
- **CQ-LOG-003 [Info]** `Console.WriteLine` / `Debug.WriteLine` in application code, and wrong levels (errors logged as `Information`, routine events as `Error`).

## Configuration

- **CQ-CFG-001 [Info]** (.NET Core) Raw `_config["Section:Key"]` reads repeated across classes. Fix: bind to an options class with `IOptions<T>` and `ValidateOnStart()`.
- **CQ-CFG-002 [Warning]** Environment-specific URLs, paths, timeouts or machine names hard-coded in code, or behavior switched with `#if DEBUG`. Fix: per-environment configuration.

## .NET Framework specific

- **CQ-FWK-003 [Info]** New shared libraries targeting only `net4x` when they could target `netstandard2.0`; new code depending on `System.Web`, WCF server, Remoting or AppDomains.

## Tests

- **CQ-TST-001 [Warning]** New or changed business logic (services, domain classes, validators) without a matching unit test in the PR. Bug fixes should include a test that reproduces the bug.
- **CQ-TST-002 [Info]** In test projects: tests not following Arrange–Act–Assert, names not in `Method_Scenario_ExpectedResult` form, `Thread.Sleep`, real network or database calls in unit tests, or shared mutable static state between tests.
