using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TrueModel;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
if (args.Contains("--probe-stdin"))
{
    Console.InputEncoding = Encoding.UTF8;
    Console.OutputEncoding = new UTF8Encoding(false);
    using var input = System.Text.Json.JsonDocument.Parse(await Console.In.ReadToEndAsync());
    var request = input.RootElement;
    var engine = new ModelTraceClient(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
    var bank = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "unified_bank.json"));
    var result = await engine.Test(request.GetProperty("base_url").GetString()!, request.GetProperty("api_key").GetString()!, request.GetProperty("model").GetString()!, bank, request.TryGetProperty("challenge_count", out var count) ? count.GetInt32() : 3, CancellationToken.None);
    Console.WriteLine(result.GetRawText());
    return;
}
var desktop = builder.Configuration.GetValue<bool>("Desktop:Enabled");
if (desktop) builder.WebHost.UseUrls("http://127.0.0.1:0");
var data = Path.GetFullPath(builder.Configuration["DataDirectory"] ?? (desktop ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrueModel") : "data"));
Directory.CreateDirectory(data);
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? $"Data Source={Path.Combine(data, "truemodel.db")}"));
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys"))).SetApplicationName("TrueModel");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "TrueModel.Session";
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddHttpClient("probe", c => { c.Timeout = Timeout.InfiniteTimeSpan; c.MaxResponseContentBufferSize = 1_000_000; }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<DetectionWorker>();
builder.Services.AddSingleton<DetectionControl>();
builder.Services.AddSingleton<JuiceHistory>();
builder.Services.AddHttpClient<ModelTraceClient>(c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient<ModelDiscovery>(c => { c.Timeout = TimeSpan.FromSeconds(20); c.MaxResponseContentBufferSize = 2_000_000; })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).RemoveAllLoggers();
builder.Services.AddHttpClient<NotificationService>(c => { c.Timeout = TimeSpan.FromSeconds(15); c.MaxResponseContentBufferSize = 1_000_000; })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).RemoveAllLoggers();
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.EnsureCreatedAsync();
    // Upgrade databases created before configurable challenge counts existed.
    await db.Database.OpenConnectionAsync();
    using (var command = db.Database.GetDbConnection().CreateCommand())
    {
        command.CommandText = "PRAGMA table_info(Settings)";
        var hasCount = false;
        using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) hasCount |= reader.GetString(1) == "ChallengeCount";
        if (!hasCount) await db.Database.ExecuteSqlRawAsync("ALTER TABLE Settings ADD COLUMN ChallengeCount INTEGER NOT NULL DEFAULT 3");
    }
    using (var command = db.Database.GetDbConnection().CreateCommand())
    {
        command.CommandText = "PRAGMA table_info(Results)";
        var columns = new HashSet<string>();
        using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        if (!columns.Contains("JuiceValue")) await db.Database.ExecuteSqlRawAsync("ALTER TABLE Results ADD COLUMN JuiceValue INTEGER NULL");
        if (!columns.Contains("JuiceStatus")) await db.Database.ExecuteSqlRawAsync("ALTER TABLE Results ADD COLUMN JuiceStatus TEXT NULL");
        if (!columns.Contains("JuicePrompt")) await db.Database.ExecuteSqlRawAsync("ALTER TABLE Results ADD COLUMN JuicePrompt TEXT NULL");
    }
    await db.Database.CloseConnectionAsync();
    await db.Database.OpenConnectionAsync();
    using (var command = db.Database.GetDbConnection().CreateCommand())
    {
        command.CommandText = "PRAGMA table_info(Models)";
        var columns = new HashSet<string>();
        using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        if (!columns.Contains("JuiceValue")) await db.Database.ExecuteSqlRawAsync("ALTER TABLE Models ADD COLUMN JuiceValue INTEGER NULL");
        if (!columns.Contains("JuiceStatus")) await db.Database.ExecuteSqlRawAsync("ALTER TABLE Models ADD COLUMN JuiceStatus TEXT NULL");
        if (!columns.Contains("JuicePrompt")) await db.Database.ExecuteSqlRawAsync("ALTER TABLE Models ADD COLUMN JuicePrompt TEXT NULL");
        if (!columns.Contains("JuiceCheckedAt")) await db.Database.ExecuteSqlRawAsync("ALTER TABLE Models ADD COLUMN JuiceCheckedAt TEXT NULL");
    }
    await db.Database.CloseConnectionAsync();
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS JuiceMethodStatistics (
          Endpoint TEXT NOT NULL, Model TEXT NOT NULL, Method TEXT NOT NULL,
          Attempts INTEGER NOT NULL, Successes INTEGER NOT NULL, PRIMARY KEY (Endpoint, Model, Method));
        CREATE TABLE IF NOT EXISTS Notifications (
          Id INTEGER NOT NULL PRIMARY KEY, WebhookEnabled INTEGER NOT NULL, WebhookProtected TEXT NOT NULL,
          PushDeerEnabled INTEGER NOT NULL, PushDeerEndpoint TEXT NOT NULL, PushKeyProtected TEXT NOT NULL, OnlyFailures INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS NotificationDeliveries (
          Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, RunId INTEGER NULL, Channel TEXT NOT NULL,
          CreatedAt TEXT NOT NULL, Success INTEGER NOT NULL, Message TEXT NOT NULL);
        """);
    if (!await db.Notifications.AnyAsync()) db.Notifications.Add(new NotificationSettings());
    if (!await db.Banks.AnyAsync())
    {
        var json = await File.ReadAllTextAsync(Path.Combine(app.Environment.ContentRootPath, "Assets", "unified_bank.json"));
        Attribution.Validate(json);
        db.Banks.Add(new FingerprintBank { Name = "ModelTrace 60949ef", Json = json, Active = true, Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))) });
    }
    if (!await db.Settings.AnyAsync()) db.Settings.Add(new AppSettings());
    if (!await db.Administrators.AnyAsync())
    {
        var password = builder.Configuration["Admin:Password"];
        if (!desktop && (string.IsNullOrEmpty(password) || password.Length < 12))
            throw new InvalidOperationException("Set Admin__Password to at least 12 characters before first startup.");
        if (!string.IsNullOrEmpty(password) && password.Length >= 12)
            db.Administrators.Add(new Administrator { Username = builder.Configuration["Admin:Username"] ?? "admin", PasswordHash = Passwords.Hash(password) });
        await db.SaveChangesAsync();
    }
    await db.SaveChangesAsync();
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method))
    {
        try { await context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>().ValidateRequestAsync(context); }
        catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException) { context.Response.StatusCode = 400; return; }
    }
    await next(context);
});
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/session", (HttpContext c, Microsoft.AspNetCore.Antiforgery.IAntiforgery csrf) => Results.Ok(new { authenticated = c.User.Identity?.IsAuthenticated ?? false, token = csrf.GetAndStoreTokens(c).RequestToken }));
app.MapGet("/api/setup", async (AppDb db) => Results.Ok(new { required = desktop && !await db.Administrators.AnyAsync() }));
var setupLock = new SemaphoreSlim(1, 1);
app.MapPost("/api/setup", async (LoginRequest input, AppDb db) =>
{
    await setupLock.WaitAsync();
    try
    {
        if (!desktop || await db.Administrators.AnyAsync()) return Results.Conflict();
        if (string.IsNullOrWhiteSpace(input.Username) || input.Password.Length < 12) return Results.BadRequest(new { error = "密码至少需要 12 个字符" });
        db.Administrators.Add(new Administrator { Username = input.Username, PasswordHash = Passwords.Hash(input.Password) });
        await db.SaveChangesAsync(); return Results.NoContent();
    }
    finally { setupLock.Release(); }
});
app.MapPost("/api/login", async (LoginRequest input, AppDb db, HttpContext c) =>
{
    var admin = await db.Administrators.SingleOrDefaultAsync();
    if (admin is null || input.Username != admin.Username || !Passwords.Verify(input.Password, admin.PasswordHash))
    { await Task.Delay(800); return Results.Unauthorized(); }
    await c.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, admin.Username)], CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.NoContent();
});
app.MapPost("/api/logout", async (HttpContext c) => { await c.SignOutAsync(); return Results.NoContent(); }).RequireAuthorization();
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/notifications", async (AppDb db) => await db.Notifications.Select(n => new { n.WebhookEnabled, WebhookConfigured = n.WebhookProtected != "", n.PushDeerEnabled, n.PushDeerEndpoint, PushKeyConfigured = n.PushKeyProtected != "", n.OnlyFailures }).SingleAsync());
api.MapGet("/notifications/deliveries", async (AppDb db) => await db.NotificationDeliveries.OrderByDescending(n => n.Id).Take(30).ToArrayAsync());
api.MapPut("/notifications", async (NotificationInput input, AppDb db, IDataProtectionProvider provider) =>
{
    var settings = await db.Notifications.SingleAsync();
    var protector = provider.CreateProtector("Notifications.v1");
    if ((!string.IsNullOrEmpty(input.WebhookUrl) && !NotificationService.ValidEndpoint(input.WebhookUrl)) || !NotificationService.ValidEndpoint(input.PushDeerEndpoint)) return Results.BadRequest(new { error = "请输入有效的 HTTP/HTTPS 通知地址" });
    var webhook = input.ClearWebhook ? "" : !string.IsNullOrEmpty(input.WebhookUrl) ? protector.Protect(input.WebhookUrl) : settings.WebhookProtected;
    var pushkey = input.ClearPushKey ? "" : !string.IsNullOrEmpty(input.PushKey) ? protector.Protect(input.PushKey) : settings.PushKeyProtected;
    if ((input.WebhookEnabled && webhook == "") || (input.PushDeerEnabled && pushkey == "")) return Results.BadRequest(new { error = "启用通道前请填写地址或 PushKey" });
    settings.WebhookEnabled = input.WebhookEnabled; settings.WebhookProtected = webhook; settings.PushDeerEnabled = input.PushDeerEnabled;
    settings.PushDeerEndpoint = input.PushDeerEndpoint; settings.PushKeyProtected = pushkey; settings.OnlyFailures = input.OnlyFailures;
    await db.SaveChangesAsync(); return Results.NoContent();
});
api.MapPost("/notifications/test", async (AppDb db, NotificationService sender, CancellationToken token) =>
{
    var settings = await db.Notifications.SingleAsync(token);
    if (!settings.WebhookEnabled && !settings.PushDeerEnabled) return Results.BadRequest(new { error = "请先启用并保存通知通道" });
    await sender.Deliver(db, settings, new("test", null, "Completed", 1000, 1, 1, [new("测试站点", "测试 Key", "测试模型", "Success", 1000, "示例归因", .9)]), token);
    return Results.Ok(await db.NotificationDeliveries.OrderByDescending(n => n.Id).Take(2).ToArrayAsync(token));
});
api.MapGet("/sites", async (AppDb db) => await db.Sites.AsNoTracking().Select(s => new
{
    s.Id, s.Name, s.BaseUrl, s.Enabled,
    Keys = s.Keys.Select(k => new { k.Id, k.Name, k.Enabled, Mask = "********", Models = k.Models.Select(m => new { m.Id, m.Name, m.Enabled, m.JuiceValue, m.JuiceStatus, m.JuicePrompt, m.JuiceCheckedAt }) })
}).ToListAsync());
api.MapPost("/sites", async (SiteInput input, AppDb db) =>
{
    if (string.IsNullOrWhiteSpace(input.Name) || !Uri.TryCreate(input.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)) return Results.BadRequest(new { error = "Invalid site name or HTTP URL" });
    var site = new Site { Name = input.Name.Trim(), BaseUrl = input.BaseUrl.TrimEnd('/'), Enabled = input.Enabled };
    db.Sites.Add(site); await db.SaveChangesAsync(); return Results.Ok(new { site.Id });
});
api.MapDelete("/sites/{id:int}", async (int id, AppDb db, DetectionControl control) => { await control.Delete(db, siteId: id); return Results.NoContent(); });
api.MapPost("/sites/{id:int}/keys", async (int id, KeyInput input, AppDb db, IDataProtectionProvider protection) =>
{
    if (!await db.Sites.AnyAsync(s => s.Id == id)) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Value)) return Results.BadRequest();
    var key = new SiteKey { SiteId = id, Name = input.Name.Trim(), ProtectedValue = protection.CreateProtector("ApiKeys.v1").Protect(input.Value.Trim()) };
    db.Keys.Add(key); await db.SaveChangesAsync(); return Results.Ok(new { key.Id });
});
api.MapPost("/keys/{id:int}/models", async (int id, ModelInput input, AppDb db) =>
{
    if (!await db.Keys.AnyAsync(k => k.Id == id)) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest();
    if (await db.Models.AnyAsync(m => m.SiteKeyId == id && m.Name == input.Name.Trim())) return Results.Conflict();
    var model = new MonitoredModel { SiteKeyId = id, Name = input.Name.Trim() }; db.Models.Add(model); await db.SaveChangesAsync(); return Results.Ok(new { model.Id });
});
api.MapDelete("/keys/{id:int}", async (int id, AppDb db, DetectionControl control) => { await control.Delete(db, keyId: id); return Results.NoContent(); });
api.MapPost("/keys/{id:int}/discover-models", async (int id, AppDb db, IDataProtectionProvider protection, ModelDiscovery discovery, HttpContext context, CancellationToken token) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var key = await db.Keys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, token);
    if (key is null) return Results.NotFound(new { error = "Key 不存在" });
    var site = await db.Sites.AsNoTracking().SingleAsync(s => s.Id == key.SiteId, token);
    try
    {
        var value = protection.CreateProtector("ApiKeys.v1").Unprotect(key.ProtectedValue);
        return Results.Ok(new { models = await discovery.Fetch(site.BaseUrl, value, token) });
    }
    catch (ModelDiscoveryException e) { return Results.Json(new { error = e.Message }, statusCode: 502); }
    catch (CryptographicException) { return Results.BadRequest(new { error = "无法解密 Key，请重新配置" }); }
});
api.MapPost("/keys/{id:int}/models/batch", async (int id, ModelBatchInput input, AppDb db, CancellationToken token) =>
{
    if (input.Names is null || input.Names.Length is < 1 or > 500 || input.Names.Any(string.IsNullOrWhiteSpace))
        return Results.BadRequest(new { error = "请选择 1–500 个模型" });
    await using var transaction = await db.Database.BeginTransactionAsync(token);
    if (!await db.Keys.AnyAsync(k => k.Id == id, token)) return Results.NotFound(new { error = "Key 不存在" });
    var names = input.Names.Select(n => n.Trim()).Distinct(StringComparer.Ordinal).ToArray();
    var existing = await db.Models.Where(m => m.SiteKeyId == id).Select(m => m.Name).ToArrayAsync(token);
    var additions = names.Except(existing, StringComparer.Ordinal).Select(name => new MonitoredModel { SiteKeyId = id, Name = name }).ToArray();
    db.Models.AddRange(additions);
    await db.SaveChangesAsync(token);
    await transaction.CommitAsync(token);
    return Results.Ok(new { added = additions.Length, skipped = names.Length - additions.Length });
});
api.MapDelete("/models/{id:int}", async (int id, AppDb db, DetectionControl control) => { await control.Delete(db, modelId: id); return Results.NoContent(); });
api.MapGet("/juice-methods", () => JuiceProbe.Methods);
api.MapPost("/models/{id:int}/juice", async (int id, string? methodId, AppDb db, ModelTraceClient client, IDataProtectionProvider protection, CancellationToken token) =>
{
    if (methodId is not null && !JuiceProbe.Methods.Any(m => m.Id == methodId))
        return Results.BadRequest(new { error = "未知的 Juice 检测方法" });
    var target = await (from m in db.Models join k in db.Keys on m.SiteKeyId equals k.Id join s in db.Sites on k.SiteId equals s.Id
        where m.Id == id select new { Model = m, k.ProtectedValue, s.BaseUrl }).SingleOrDefaultAsync(token);
    if (target is null) return Results.NotFound();
    if (!JuiceProbe.Supports(target.Model.Name)) return Results.BadRequest(new { error = "仅 GPT 模型支持 Juice 检测" });
    string key;
    try { key = protection.CreateProtector("ApiKeys.v1").Unprotect(target.ProtectedValue); }
    catch (CryptographicException) { return Results.BadRequest(new { error = "无法解密 Key，请重新配置" }); }
    var result = await client.ProbeJuice(target.BaseUrl, key, target.Model.Name, token, methodId);
    var value = result.GetValueOrDefault("juice_value") as int?;
    var status = result.GetValueOrDefault("juice_status") as string;
    var prompt = result.GetValueOrDefault("juice_prompt") as string;
    var checkedAt = DateTime.UtcNow;
    var updated = await db.Models.Where(m => m.Id == id).ExecuteUpdateAsync(s => s
        .SetProperty(m => m.JuiceValue, value).SetProperty(m => m.JuicePrompt, prompt).SetProperty(m => m.JuiceStatus, status).SetProperty(m => m.JuiceCheckedAt, checkedAt), token);
    return updated == 0 ? Results.NotFound() : Results.Ok(new { juiceValue = value, juiceStatus = status, juicePrompt = prompt, juiceCheckedAt = checkedAt });
});
api.MapPost("/detect", async (DetectionScope input, AppDb db, DetectionControl control) =>
{
    await control.Gate.WaitAsync();
    try { var run = await DetectionJobs.Enqueue(db, input, "Manual"); return Results.Accepted($"/api/runs/{run.Id}", new { run.Id }); }
    catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
    finally { control.Gate.Release(); }
});
api.MapGet("/runs", async (AppDb db, DetectionControl control, CancellationToken token) =>
{
    (int RunId, int ModelId)[] active;
    await control.Gate.WaitAsync(token);
    try { active = control.Active.Keys.ToArray(); }
    finally { control.Gate.Release(); }
    var runs = await db.Runs.AsNoTracking().OrderByDescending(r => r.Id).Take(100).Select(r => new { r.Id, r.StartedAt, r.CompletedAt, r.Status, r.Source, r.Total, r.Completed, r.BankId, r.TargetsJson }).ToArrayAsync(token);
    var ids = runs.Select(r => r.Id).ToArray();
    var completedTargets = await db.Results.AsNoTracking().Where(r => ids.Contains(r.RunId))
        .Select(r => new { r.RunId, r.ModelId }).ToArrayAsync(token);
    var completedLookup = completedTargets.ToLookup(r => r.RunId, r => r.ModelId);
    var failures = await db.Results.AsNoTracking().Where(r => ids.Contains(r.RunId) && (r.Status == "Failed" || r.Status == "Timeout" || r.Status == "Cancelled" || r.Status == "Interrupted"))
        .OrderByDescending(r => r.Id).Select(r => new { r.Id, r.RunId, r.SiteName, r.KeyName, r.ModelName, r.Status, r.StatusCode, r.Error, r.ResponsesJson }).ToArrayAsync(token);
    var lookup = failures.ToLookup(r => r.RunId);
    return Results.Ok(runs.Select(run => new
    {
        run.Id, run.StartedAt, run.CompletedAt, run.Status, run.Source, run.Total, run.Completed, run.BankId,
        CompletedModelIds = completedLookup[run.Id].Distinct(),
        ActiveModelIds = active.Where(t => t.RunId == run.Id).Select(t => t.ModelId),
        Targets = System.Text.Json.JsonSerializer.Deserialize<Target[]>(run.TargetsJson)?.Select(t => new { t.ModelId, t.ModelName, t.KeyName, t.SiteName }),
        Failures = lookup[run.Id].Select(r => new { r.Id, r.SiteName, r.KeyName, r.ModelName, Reason = FailureReasons.Describe(r.Status, r.StatusCode, r.Error, r.ResponsesJson) })
    }));
});
api.MapPost("/runs/{id:int}/cancel", async (int id, AppDb db, DetectionControl control) =>
{
    await control.Gate.WaitAsync();
    try
    {
        await db.Runs.Where(r => r.Id == id && (r.Status == "Running" || r.Status == "Queued")).ExecuteUpdateAsync(s => s.SetProperty(r => r.CancelRequested, true));
        foreach (var entry in control.Active.Where(e => e.Key.RunId == id)) await entry.Value.CancelAsync();
    }
    finally { control.Gate.Release(); }
    return Results.NoContent();
});
api.MapGet("/results", async (int? runId, int? modelId, bool? perModel, AppDb db) =>
{
    var query = db.Results.AsNoTracking().Where(r => (runId == null || r.RunId == runId) && (modelId == null || r.ModelId == modelId));
    if (perModel == true)
        return await query.Where(r => db.Models.Any(m => m.Id == r.ModelId) && db.Results.Count(newer => newer.ModelId == r.ModelId && newer.Id > r.Id) < 5).OrderByDescending(r => r.Id).ToArrayAsync();
    return await query.OrderByDescending(r => r.Id).Take(200).ToArrayAsync();
});
api.MapGet("/banks", async (AppDb db) => await db.Banks.Select(b => new { b.Id, b.Name, b.Sha256, b.ImportedAt, b.Active }).ToArrayAsync());
api.MapGet("/banks/{id:int}/export", async (int id, AppDb db) => { var bank = await db.Banks.FindAsync(id); return bank is null ? Results.NotFound() : Results.File(Encoding.UTF8.GetBytes(bank.Json), "application/json", $"bank-{id}.json"); });
api.MapPost("/banks", async (BankInput input, AppDb db) =>
{
    if (string.IsNullOrWhiteSpace(input.Name) || input.Json.Length > 10_000_000) return Results.BadRequest();
    try { Attribution.Validate(input.Json); }
    catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { return Results.BadRequest(new { error = "Invalid ModelTrace bank" }); }
    var bank = new FingerprintBank { Name = input.Name, Json = input.Json, Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Json))) };
    db.Banks.Add(bank); await db.SaveChangesAsync(); return Results.Ok(new { bank.Id });
});
api.MapPost("/banks/{id:int}/activate", async (int id, AppDb db) =>
{
    if (!await db.Banks.AnyAsync(b => b.Id == id)) return Results.NotFound();
    await using var transaction = await db.Database.BeginTransactionAsync();
    await db.Banks.ExecuteUpdateAsync(s => s.SetProperty(b => b.Active, false));
    await db.Banks.Where(b => b.Id == id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Active, true));
    await transaction.CommitAsync(); return Results.NoContent();
});
api.MapGet("/settings", async (AppDb db) => await db.Settings.SingleAsync());
api.MapPut("/settings", async (AppSettings input, AppDb db) =>
{
    if (input.IntervalMinutes < 0 || input.IntervalMinutes > 525600 || input.MaxConcurrency is < 1 or > 16 || input.TimeoutSeconds is < 5 or > 600 || input.ChallengeCount is < 3 or > 6) return Results.BadRequest();
    var settings = await db.Settings.SingleAsync();
    settings.IntervalMinutes = input.IntervalMinutes; settings.MaxConcurrency = input.MaxConcurrency; settings.TimeoutSeconds = 240; settings.ChallengeCount = input.ChallengeCount;
    settings.NextRunAt = input.IntervalMinutes > 0 ? DateTime.UtcNow.AddMinutes(input.IntervalMinutes) : null;
    await db.SaveChangesAsync(); return Results.NoContent();
});
if (desktop) app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = app.Urls.First();
    app.Logger.LogInformation("TrueModel: {Url}; local data: {Directory}", url, data);
    if (!builder.Configuration.GetValue("Desktop:OpenBrowser", true)) return;
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
    catch { app.Logger.LogWarning("Open {Url} in a browser to continue.", url); }
});
app.Run();
record BankInput(string Name, string Json);
record LoginRequest(string Username, string Password);
record SiteInput(string Name, string BaseUrl, bool Enabled = true);
record KeyInput(string Name, string Value);
record ModelInput(string Name);
record ModelBatchInput(string[]? Names);
public partial class Program { }

