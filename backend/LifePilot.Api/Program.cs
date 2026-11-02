using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authorization;
using Polly;
using Polly.Retry;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5080");
builder.Services.AddDbContext<LifeDb>(o => o.UseSqlite("Data Source=lifepilot-enterprise.db"));
builder.Services.AddCors(o => o.AddPolicy("local", p => p.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173").AllowAnyHeader().AllowAnyMethod()));
var jwtKey = Environment.GetEnvironmentVariable("LIFEPILOT_JWT_KEY");
if (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Set LIFEPILOT_JWT_KEY to a random secret of at least 32 UTF-8 bytes before starting.");
var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => {
    o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = "LifePilot", ValidateAudience = true, ValidAudience = "LifePilot.Local", ValidateIssuerSigningKey = true, IssuerSigningKey = key, ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(15) };
});
builder.Services.AddAuthorization(options => {
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
    options.AddPolicy("MemberOrAdmin", policy => policy.RequireRole("Member", "Admin"));
});
builder.Services.AddHostedService<ReminderWorker>();
// Recurring tasks: the scheduler is scoped (it owns a DbContext) and shared by the API endpoints and the background
// worker; TimeProvider is injected so tests can drive the clock deterministically.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<RecurringTaskScheduler>();
builder.Services.AddHostedService<RecurringTaskWorker>();
builder.Services.AddSingleton<TransientRetryService>();
builder.Services.AddProblemDetails();
// Malformed request bodies (unparseable JSON, wrong JSON shape, wrong content type) are surfaced by the minimal-API binder as
// BadHttpRequestException, which ApiExceptionHandler turns into a Problem Details response. Without this the framework only
// throws in Development and writes an empty 400 in Production, so clients would see different error bodies per environment.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(o => { o.RejectionStatusCode = 429; o.AddFixedWindowLimiter("auth", x => { x.PermitLimit = 10; x.Window = TimeSpan.FromMinutes(1); x.QueueLimit = 0; }); });
var app = builder.Build();
app.UseExceptionHandler(); app.UseCors("local"); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<LifeDb>();
    // Creates the schema, tolerates another instance creating it at the same moment, and adds tables a newer version needs.
    await SchemaBootstrap.InitializeAsync(db, app.Logger);
}
app.MapHealthChecks("/api/health");
app.MapPost("/api/auth/register", async (Credentials input, LifeDb db, CancellationToken ct) => {
    var email = input.Email.Trim().ToLowerInvariant();
    if (email.Length > 254 || !email.Contains('@') || input.Password.Length < 12 || input.Password.Length > 128) return Results.ValidationProblem(new Dictionary<string,string[]> { ["credentials"] = ["Use a valid email and a password of 12–128 characters."] });
    if (await db.Users.AnyAsync(u => u.Email == email, ct)) return Results.Conflict(new { message = "Account already exists." });
    var user = new AppUser { Email = email, PasswordHash = Passwords.Hash(input.Password), Role = "Member" };
    db.Users.Add(user); await db.SaveChangesAsync(ct); return Results.Ok(new { message = "Registered. Please sign in." });
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async (Credentials input, LifeDb db, CancellationToken ct) => {
    var email = input.Email.Trim().ToLowerInvariant(); var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
    if (user is null || !Passwords.Verify(input.Password, user.PasswordHash)) return Results.Unauthorized();
    var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new Claim(JwtRegisteredClaimNames.Email, user.Email), new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, user.Role) };
    var token = new JwtSecurityToken("LifePilot", "LifePilot.Local", claims, expires: DateTime.UtcNow.AddMinutes(60), signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
    return Results.Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), expiresIn = 3600 });
}).RequireRateLimiting("auth");
var secure = app.MapGroup("/api").RequireAuthorization("MemberOrAdmin");
secure.MapGet("/admin/stats", async (LifeDb db, CancellationToken ct) => Results.Ok(new {
    users = await db.Users.CountAsync(ct), items = await db.Items.CountAsync(ct),
    pendingReminders = await db.Reminders.CountAsync(x => !x.Delivered, ct),
    activeRecurringTasks = await db.RecurringTasks.CountAsync(x => x.IsActive, ct),
    openOccurrences = await db.TaskOccurrences.CountAsync(x => !x.Completed, ct)
})).RequireAuthorization("AdminOnly");
secure.MapRecurringTasks();
secure.MapGet("/reminders", async (LifeDb db, ClaimsPrincipal user, CancellationToken ct) => {
    var owner = UserId(user);
    return await db.Reminders.Where(x => x.UserId == owner).OrderBy(x => x.DueUtc).ToListAsync(ct);
});
secure.MapPost("/reminders", async (ReminderRequest input, LifeDb db, ClaimsPrincipal user, CancellationToken ct) => {
    if (string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > 200 || input.DueUtc <= DateTime.UtcNow.AddSeconds(-10))
        throw new ValidationException("Provide a message (1–200 characters) and a future UTC date.");
    if (input.RepeatMinutes is < 0 or > 525600) throw new ValidationException("Invalid recurrence interval.");
    var reminder = new Reminder { UserId = UserId(user), Message = input.Message.Trim(), DueUtc = input.DueUtc.ToUniversalTime(), RepeatMinutes = input.RepeatMinutes };
    db.Reminders.Add(reminder); await db.SaveChangesAsync(ct);
    return Results.Created($"/api/reminders/{reminder.Id}", reminder);
});
secure.MapDelete("/reminders/{id:int}", async (int id, LifeDb db, ClaimsPrincipal user, CancellationToken ct) => {
    var owner = UserId(user);
    var reminder = await db.Reminders.SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner, ct);
    if (reminder is null) return Results.NotFound();
    db.Reminders.Remove(reminder); await db.SaveChangesAsync(ct); return Results.NoContent();
});
secure.MapGet("/audit", async (LifeDb db, ClaimsPrincipal user, CancellationToken ct) => {
    var owner = UserId(user);
    return await db.AuditEvents.Where(x => x.UserId == owner).OrderByDescending(x => x.OccurredUtc).Take(100).ToListAsync(ct);
});
secure.MapGet("/resilience/demo", async (TransientRetryService retry, CancellationToken ct) => {
    // A safe, read-only operation demonstrating retry on a transient exception.
    var attempts = 0;
    return Results.Ok(new { value = await retry.ExecuteAsync(async token => {
        attempts++;
        if (attempts < 2) throw new HttpRequestException("Simulated transient failure");
        await Task.Yield(); return "Recovered";
    }, ct), attempts });
});
secure.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { userId = UserId(user) }));
secure.MapGet("/items", async (LifeDb db, ClaimsPrincipal user, string? kind, CancellationToken ct) => {
    var owner = UserId(user);
    return await db.Items.Where(x => x.UserId == owner && (kind == null || x.Kind == kind)).OrderByDescending(x => x.Id).ToListAsync(ct);
});
secure.MapPost("/items", async (HttpContext ctx, LifeDb db, ClaimsPrincipal user, NewItem input, CancellationToken ct) => {
    Validate(input);
    var owner = UserId(user); var idempotencyKey = ctx.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100) return Results.BadRequest(new { message = "Idempotency-Key header required (1–100 characters)." });
    var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));
    // SQLite serializes writers; BEGIN IMMEDIATE is obtained by EF Core's serializable transaction.
    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
    var previous = await db.IdempotencyRecords.SingleOrDefaultAsync(x => x.UserId == owner && x.Key == idempotencyKey, ct);
    if (previous != null) {
        if (previous.Fingerprint != fingerprint) return Results.Conflict(new { message = "Idempotency key reused with different request data." });
        var saved = await db.Items.SingleAsync(x => x.Id == previous.ItemId && x.UserId == owner, ct);
        return Results.Ok(saved);
    }
    var item = new LifeItem { UserId = owner, Kind = input.Kind, Title = input.Title.Trim(), Category = input.Category?.Trim() ?? "", Amount = input.Amount ?? 0, Date = input.Date, CreatedUtc = DateTime.UtcNow };
    db.Items.Add(item); await db.SaveChangesAsync(ct);
    db.AuditEvents.Add(new AuditEvent { UserId = owner, Action = "item.created", EntityId = item.Id, OccurredUtc = DateTime.UtcNow });
    db.IdempotencyRecords.Add(new IdempotencyRecord { UserId = owner, Key = idempotencyKey, Fingerprint = fingerprint, ItemId = item.Id });
    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    return Results.Created($"/api/items/{item.Id}", item);
});
secure.MapPut("/items/{id:int}", async (int id, UpdateItem input, LifeDb db, ClaimsPrincipal user, CancellationToken ct) => {
    var owner = UserId(user);
    var item = await db.Items.SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner, ct); if (item is null) return Results.NotFound();
    if (input.Title is not null) { if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 200) throw new ValidationException("Title must be 1–200 characters."); item.Title = input.Title.Trim(); }
    if (input.Category is not null) item.Category = input.Category.Trim();
    if (input.Amount is not null) { if (input.Amount < 0 || input.Amount > 100000000) throw new ValidationException("Invalid amount."); item.Amount = input.Amount.Value; }
    if (input.Date is not null) { if (input.Date != "" && !DateOnly.TryParse(input.Date, out _)) throw new ValidationException("Invalid date."); item.Date = input.Date == "" ? null : input.Date; }
    if (input.Done.HasValue) item.Done = input.Done.Value;
    db.AuditEvents.Add(new AuditEvent { UserId = UserId(user), Action = "item.updated", EntityId = item.Id, OccurredUtc = DateTime.UtcNow });
    await db.SaveChangesAsync(ct); return Results.Ok(item);
});
secure.MapDelete("/items/{id:int}", async (int id, LifeDb db, ClaimsPrincipal user, CancellationToken ct) => {
    var owner = UserId(user);
    var item = await db.Items.SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner, ct); if (item is null) return Results.NotFound();
    db.Items.Remove(item); db.AuditEvents.Add(new AuditEvent { UserId = UserId(user), Action = "item.deleted", EntityId = item.Id, OccurredUtc = DateTime.UtcNow }); await db.SaveChangesAsync(ct); return Results.NoContent();
});
secure.MapGet("/backup", async (LifeDb db, ClaimsPrincipal user, CancellationToken ct) => {
    var owner = UserId(user);
    return await db.Items.Where(x => x.UserId == owner).OrderBy(x => x.Id).Select(x => new NewItem(x.Kind,x.Title,x.Category,x.Amount,x.Date)).ToListAsync(ct);
});
secure.MapPost("/restore", async (LifeDb db, ClaimsPrincipal user, List<NewItem?>? items, CancellationToken ct) => {
    // Validate the whole backup first so a single bad entry rejects the import before anything is written.
    var incoming = ValidateBackup(items);
    var owner = UserId(user);
    // An empty backup is valid but has nothing to compare or write; skip the transaction and the item scan.
    if (incoming.Count == 0) return Results.Ok(new { imported = 0, skipped = 0, total = 0 });
    // Backups carry no database IDs, so an existing record is recognised by its ItemKey (kind, title, category, amount, date).
    // The duplicate check and the inserts share one transaction so the import is all-or-nothing. Serializable maps to
    // BEGIN IMMEDIATE in Microsoft.Data.Sqlite, which takes the writer lock *before* the read below; a second restore
    // (or POST /items) therefore waits at this line and then sees the first one's rows, instead of both reading an empty
    // set and both inserting. Do not downgrade this to a deferred transaction.
    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
    var existing = await db.Items.Where(x => x.UserId == owner)
        .Select(x => new { x.Kind, x.Title, x.Category, x.Amount, x.Date }).ToListAsync(ct);
    // Each stored record can satisfy one incoming record, so a backup holding two identical rows still restores both
    // into an account that has only one, while re-importing the same backup imports nothing.
    var unmatched = new Dictionary<ItemKey, int>();
    foreach (var e in existing) { var k = ItemKey.Create(e.Kind, e.Title, e.Category, e.Amount, e.Date); unmatched[k] = unmatched.GetValueOrDefault(k) + 1; }
    var imported = 0; var skipped = 0; var now = DateTime.UtcNow;
    foreach (var x in incoming) {
        var k = ItemKey.From(x);
        if (unmatched.TryGetValue(k, out var remaining) && remaining > 0) { unmatched[k] = remaining - 1; skipped++; continue; }
        db.Items.Add(new LifeItem { UserId = owner, Kind = k.Kind, Title = k.Title, Category = k.Category, Amount = k.Amount, Date = k.Date, CreatedUtc = now });
        imported++;
    }
    if (imported > 0) await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new { imported, skipped, total = incoming.Count });
});
secure.MapPost("/assistant", (Command input) => {
    if (input.Text is null || input.Text.Length > 500) throw new ValidationException("Command must be at most 500 characters.");
    var s = input.Text.Trim();
    var grocery = Regex.Match(s, @"^(?:add|buy)\s+(.+?)\s+(?:to\s+(?:my\s+)?(?:grocery|groceries|shopping)(?:\s+list)?)$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    if (grocery.Success) return Results.Ok(new { kind="grocery", title=grocery.Groups[1].Value, message="Ready to add to groceries." });
    var remind = Regex.Match(s, @"^remind me to\s+(.+)$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    if (remind.Success) return Results.Ok(new { kind="task", title=remind.Groups[1].Value, message="Ready to add a task." });
    return Results.Ok(new { kind="", title="", message="Try: add milk to groceries; remind me to call mom" });
});
app.Run();
static int UserId(ClaimsPrincipal principal) => int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException());
static bool IsInvalidItem(NewItem x) =>
    x.Kind is not ("task" or "grocery" or "event") || string.IsNullOrWhiteSpace(x.Title) || x.Title.Length > 200 || x.Amount is < 0 or > 100000000 || (x.Date is not null && !DateOnly.TryParse(x.Date, out _));
static void Validate(NewItem x) {
    if (IsInvalidItem(x)) throw new ValidationException("Invalid item kind, title, amount, or date.");
}
// Checks an entire backup payload and returns it as a non-null list. Throws on the first problem, naming the offending index,
// so the caller can reject the whole import before touching the database.
static List<NewItem> ValidateBackup(List<NewItem?>? items) {
    if (items is null) throw new ValidationException("Backup must be a JSON array of items.");
    if (items.Count > BackupLimits.MaxItems) throw new ValidationException($"Backup is too large (maximum {BackupLimits.MaxItems} items).");
    var result = new List<NewItem>(items.Count);
    for (var i = 0; i < items.Count; i++) {
        var item = items[i];
        if (item is null) throw new ValidationException($"Backup item at index {i} is missing.");
        if (IsInvalidItem(item)) throw new ValidationException($"Backup item at index {i} has an invalid kind, title, amount, or date.");
        result.Add(item);
    }
    return result;
}
public class LifeDb(DbContextOptions<LifeDb> options) : DbContext(options) {
    public DbSet<Reminder> Reminders => Set<Reminder>(); public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>(); public DbSet<LifeItem> Items => Set<LifeItem>(); public DbSet<AppUser> Users => Set<AppUser>(); public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<RecurringTask> RecurringTasks => Set<RecurringTask>(); public DbSet<TaskOccurrence> TaskOccurrences => Set<TaskOccurrence>(); public DbSet<TaskReminder> TaskReminders => Set<TaskReminder>(); public DbSet<TaskReminderOccurrence> TaskReminderOccurrences => Set<TaskReminderOccurrence>();
    protected override void OnModelCreating(ModelBuilder m) {
        m.Entity<AppUser>().HasIndex(x=>x.Email).IsUnique(); m.Entity<IdempotencyRecord>().HasIndex(x=>new{x.UserId,x.Key}).IsUnique(); m.Entity<LifeItem>().HasIndex(x=>x.UserId); m.Entity<Reminder>().HasIndex(x=>new{x.Delivered,x.DueUtc}); m.Entity<AuditEvent>().HasIndex(x=>new{x.UserId,x.OccurredUtc});
        // Recurring tasks. The unique indexes are the last line of defence against duplicate occurrences and reminders.
        m.Entity<RecurringTask>().HasIndex(x => new { x.UserId, x.IsActive });
        m.Entity<TaskOccurrence>().HasOne(x => x.RecurringTask).WithMany().HasForeignKey(x => x.RecurringTaskId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<TaskOccurrence>().HasIndex(x => new { x.RecurringTaskId, x.SequenceNumber }).IsUnique();
        m.Entity<TaskOccurrence>().HasIndex(x => new { x.UserId, x.Completed, x.DueUtc });
        m.Entity<TaskOccurrence>().HasIndex(x => new { x.Completed, x.ReminderSentUtc, x.ReminderAtUtc });
        m.Entity<TaskReminder>().HasOne<TaskOccurrence>().WithMany().HasForeignKey(x => x.OccurrenceId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<TaskReminder>().HasIndex(x => x.OccurrenceId).IsUnique();
        m.Entity<TaskReminder>().HasIndex(x => new { x.UserId, x.ReadUtc, x.CreatedUtc });
        m.Entity<TaskReminderOccurrence>().HasOne<TaskReminder>().WithMany().HasForeignKey(x => x.ReminderId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<TaskReminderOccurrence>().HasOne<TaskOccurrence>().WithMany().HasForeignKey(x => x.OccurrenceId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<TaskReminderOccurrence>().HasIndex(x => new { x.ReminderId, x.OccurrenceId }).IsUnique();
        m.Entity<TaskReminderOccurrence>().HasIndex(x => x.OccurrenceId);
    }
}
public class AppUser { public int Id {get;set;} public string Email {get;set;}=""; public string PasswordHash {get;set;}=""; public string Role {get;set;}="Member"; }
public class IdempotencyRecord { public int Id {get;set;} public int UserId {get;set;} public string Key {get;set;}=""; public string Fingerprint {get;set;}=""; public int ItemId {get;set;} }
public class LifeItem { public int Id {get;set;} public int UserId {get;set;} public string Kind {get;set;}=""; public string Title {get;set;}=""; public string Category {get;set;}=""; public decimal Amount {get;set;} public string? Date {get;set;} public bool Done {get;set;} public DateTime CreatedUtc {get;set;} }
public record Credentials(string Email,string Password);
public record NewItem(string Kind,string Title,string? Category,decimal? Amount,string? Date);
/// <summary>
/// Identity of an item in a backup. Backups carry no database IDs, so two records are the same when every exported field
/// matches after the same normalisation POST /api/items applies before storing (trimmed title, trimmed category with null
/// treated as empty, null amount treated as zero). Record-struct value equality makes this usable as a dictionary key.
/// </summary>
public readonly record struct ItemKey(string Kind, string Title, string Category, decimal Amount, string? Date) {
    // Single normalisation point used for both incoming backup rows and stored rows, so the comparison does not depend on
    // stored data having been written through the API (the README documents hand-editing the SQLite file).
    public static ItemKey Create(string kind, string title, string? category, decimal? amount, string? date) =>
        new(kind, title.Trim(), category?.Trim() ?? "", amount ?? 0, date);
    public static ItemKey From(NewItem x) => Create(x.Kind, x.Title, x.Category, x.Amount, x.Date);
}
public static class BackupLimits { public const int MaxItems = 5000; }
public record UpdateItem(string? Title,string? Category,decimal? Amount,string? Date,bool? Done);
public record Command(string Text);
public sealed class ValidationException(string message) : Exception(message);
public class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler {
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception error, CancellationToken ct) {
        // BadHttpRequestException is raised by the framework for malformed request bodies (e.g. JSON that is not the expected
        // shape, or a non-JSON content type) because RouteHandlerOptions.ThrowOnBadRequest is enabled at startup; keep its own
        // 4xx status (400/415/413) instead of reporting a server error.
        var status = error switch {
            ValidationException => 400,
            UnauthorizedAccessException => 401,
            BadHttpRequestException bad => bad.StatusCode,
            _ => 500
        };
        if (status == 500) logger.LogError(error, "Unhandled API error");
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status=status, Title=status==500?"Unexpected server error":error.Message, Type=$"https://httpstatuses.com/{status}" }, ct);
        return true;
    }
}
public static class Passwords {
    public static string Hash(string password) { var salt = RandomNumberGenerator.GetBytes(16); var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32); return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}"; }
    public static bool Verify(string password,string encoded) { var parts=encoded.Split(':'); if(parts.Length!=2) return false; var salt=Convert.FromBase64String(parts[0]); var actual=Convert.FromBase64String(parts[1]); var expected=Rfc2898DeriveBytes.Pbkdf2(password,salt,210000,HashAlgorithmName.SHA256,32); return CryptographicOperations.FixedTimeEquals(expected,actual); }
}

public record ReminderRequest(string Message, DateTime DueUtc, int? RepeatMinutes);
public class Reminder {
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Message { get; set; } = "";
    public DateTime DueUtc { get; set; }
    public int? RepeatMinutes { get; set; }
    public bool Delivered { get; set; }
    public DateTime? DeliveredUtc { get; set; }
}
public class AuditEvent {
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Action { get; set; } = "";
    public int EntityId { get; set; }
    public DateTime OccurredUtc { get; set; }
}
public sealed class ReminderWorker(IServiceScopeFactory scopeFactory, ILogger<ReminderWorker> logger) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested) {
            try {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LifeDb>();
                var now = DateTime.UtcNow;
                var due = await db.Reminders.Where(r => !r.Delivered && r.DueUtc <= now).Take(100).ToListAsync(stoppingToken);
                foreach (var reminder in due) {
                    logger.LogInformation("Reminder due: ID {ReminderId}, owner {UserId}", reminder.Id, reminder.UserId);
                    reminder.Delivered = true; reminder.DeliveredUtc = now;
                    if (reminder.RepeatMinutes is > 0) {
                        db.Reminders.Add(new Reminder { UserId = reminder.UserId, Message = reminder.Message,
                            DueUtc = now.AddMinutes(reminder.RepeatMinutes.Value), RepeatMinutes = reminder.RepeatMinutes });
                    }
                }
                await db.SaveChangesAsync(stoppingToken);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
              catch (Exception ex) { logger.LogError(ex, "Reminder worker iteration failed"); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
public sealed class TransientRetryService {
    private readonly ResiliencePipeline _pipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(150),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>().Handle<TimeoutException>()
        }).Build();
    public async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(token => new ValueTask<T>(operation(token)), ct);
}

public partial class Program { }
