---
applyTo: "**/*.cs,**/*.config,**/appsettings*.json"
---

# Security rules (CQ-SEC, CQ-FWK-002)

Treat every item here as high priority. Comment format: `[Severity] RULE-ID: problem. Fix: ...`

- **CQ-SEC-001 [Error] No hard-coded secrets.** Flag passwords, API keys, tokens, and connection strings with `Password=` / `User ID=` in `.cs`, `appsettings*.json`, `web.config` or `app.config`. Fix: User Secrets locally; Key Vault or environment variables when deployed; read through `IConfiguration`.
- **CQ-SEC-002 [Error] Parameterize all SQL.** Flag SQL built with `+`, `string.Format`, `$"..."` or `StringBuilder` that includes any variable, and `FromSqlRaw` / `ExecuteSqlRaw` with interpolated input. Fix: `SqlParameter`, Dapper parameters, LINQ, or `FromSqlInterpolated` / `ExecuteSqlInterpolated`.

```csharp
// Bad
var sql = $"SELECT * FROM Users WHERE Email = '{email}'";
// Good
cmd.CommandText = "SELECT * FROM Users WHERE Email = @email";
cmd.Parameters.Add("@email", SqlDbType.NVarChar, 256).Value = email;
```

- **CQ-SEC-003 [Error] Validate and encode external input.** Flag user input passed directly to `File.*`, `Path.Combine`, `Process.Start`, `Redirect`, deserializers, or rendered with `Html.Raw` / `MarkupString`. Fix: allow-list validation, `Path.GetFileName`, `Url.IsLocalUrl`, HTML encoding.
- **CQ-SEC-004 [Error] Don't log sensitive data.** Flag logging of passwords, tokens, card numbers, national IDs, OTPs or whole request/response bodies. Fix: log an ID or a masked value.
- **CQ-SEC-005 [Error] Safe serializers only.** Flag `BinaryFormatter`, `NetDataContractSerializer`, `SoapFormatter`, `LosFormatter`, and Newtonsoft `TypeNameHandling` set to anything other than `None`. Fix: `System.Text.Json` or Newtonsoft with `TypeNameHandling.None`.
- **CQ-SEC-006 [Error] Strong cryptography.** Flag MD5 / SHA1 for passwords or signatures, custom encryption, `System.Random` for tokens/OTPs/keys, and hard-coded IVs or keys. Fix: `PasswordHasher<T>` or PBKDF2 (`Rfc2898DeriveBytes`), `RandomNumberGenerator`, AES-GCM.
- **CQ-SEC-007 [Error] Authorize every endpoint.** Flag new controllers, actions or minimal-API endpoints with neither `[Authorize]` (or `RequireAuthorization`) nor `[AllowAnonymous]`. Also flag data loaded by ID without checking that the current user owns it.
- **CQ-FWK-002 [Error] No secrets in web.config / app.config.** Flag credentials in `<connectionStrings>` or `<appSettings>`. Fix: config transforms with secrets injected at deploy time, or encrypted config sections.

## Also flag

- `[Warning]` CORS with `AllowAnyOrigin()` together with credentials, or `*` origins in production config.
- `[Warning]` `ServicePointManager.ServerCertificateValidationCallback` or `HttpClientHandler.ServerCertificateCustomValidationCallback` returning `true`.
- `[Warning]` Disabled anti-forgery checks (`[IgnoreAntiforgeryToken]`) on state-changing MVC actions.
- `[Warning]` Exception details or stack traces returned to the client.
