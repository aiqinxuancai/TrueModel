using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace TrueModel;

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Administrator> Administrators => Set<Administrator>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<SiteKey> Keys => Set<SiteKey>();
    public DbSet<MonitoredModel> Models => Set<MonitoredModel>();
    public DbSet<DetectionRun> Runs => Set<DetectionRun>();
    public DbSet<DetectionResult> Results => Set<DetectionResult>();
    public DbSet<JuiceMethodStatistic> JuiceMethodStatistics => Set<JuiceMethodStatistic>();
    public DbSet<FingerprintBank> Banks => Set<FingerprintBank>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<NotificationSettings> Notifications => Set<NotificationSettings>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<JuiceMethodStatistic>().HasKey(s => new { s.Endpoint, s.Model, s.Method });
        model.Entity<SiteKey>().HasOne<Site>().WithMany(s => s.Keys).HasForeignKey(k => k.SiteId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<MonitoredModel>().HasOne<SiteKey>().WithMany(k => k.Models).HasForeignKey(m => m.SiteKeyId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<MonitoredModel>().HasIndex(m => new { m.SiteKeyId, m.Name }).IsUnique();
        // SQLite has no timezone metadata; restore UTC on reads for browser timers.
        var utc = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?)) property.SetValueConverter(utc);
    }
}
public sealed class DetectionRun
{
    public int Id { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "Queued";
    public string Source { get; set; } = "Manual";
    public int Total { get; set; }
    public int Completed { get; set; }
    public int BankId { get; set; }
    public bool CancelRequested { get; set; }
    public string TargetsJson { get; set; } = "[]";
}
public sealed class DetectionResult
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public int ModelId { get; set; }
    public string SiteName { get; set; } = "";
    public string KeyName { get; set; } = "";
    public string ModelName { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Pending";
    public int StatusCode { get; set; }
    public long LatencyMs { get; set; }
    public string? Error { get; set; }
    public string ResponsesJson { get; set; } = "[]";
    public string? AttributionJson { get; set; }
    public int? JuiceValue { get; set; }
    public string? JuiceStatus { get; set; }
    public string? JuicePrompt { get; set; }
    public string? CandyStatus { get; set; }
    public string? CandyPrompt { get; set; }
    public string? CandyResponse { get; set; }
    public string? CandyError { get; set; }
    public string? CandyReasoningEffort { get; set; }
    public long? CandyReasoningTokens { get; set; }
}
public sealed class FingerprintBank
{
    public bool IsDefault { get; set; }
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Json { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public bool Active { get; set; }
}
public sealed class AppSettings
{
    public int Id { get; set; } = 1;
    public int IntervalMinutes { get; set; }
    public int MaxConcurrency { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 240;
    public int ChallengeCount { get; set; } = 3;
    public string CandyReasoningEffort { get; set; } = "default";
    public DateTime? NextRunAt { get; set; }
}
public sealed class Administrator
{
    public int Id { get; set; }
    public string Username { get; set; } = "admin";
    public string PasswordHash { get; set; } = "";
}
public sealed class Site
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<SiteKey> Keys { get; set; } = [];
}
public sealed class SiteKey
{
    public int Id { get; set; }
    public int SiteId { get; set; }
    public string Name { get; set; } = "";
    public string ProtectedValue { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<MonitoredModel> Models { get; set; } = [];
}
public sealed class MonitoredModel
{
    public int? JuiceValue { get; set; }
    public string? JuiceStatus { get; set; }
    public string? JuicePrompt { get; set; }
    public string? CandyStatus { get; set; }
    public string? CandyPrompt { get; set; }
    public string? CandyResponse { get; set; }
    public string? CandyError { get; set; }
    public string? CandyReasoningEffort { get; set; }
    public long? CandyReasoningTokens { get; set; }
    public DateTime? JuiceCheckedAt { get; set; }
    public DateTime? CandyCheckedAt { get; set; }
    public int Id { get; set; }
    public int SiteKeyId { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
public static class Passwords
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }
    public static bool Verify(string password, string encoded)
    {
        var parts = encoded.Split(':');
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[0]), 210000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(parts[1]));
    }
}
