using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Client.Data;

namespace Newrest.Pos.Client.Core.Local;

/// <summary>Creates short-lived contexts on the register database (one per operation, thread-safe usage).</summary>
public sealed class LocalStore(string databasePath)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly DbContextOptions<ClientDbContext> _options = ClientDbContext.CreateOptions(databasePath);

    public string DatabasePath => databasePath;

    public ClientDbContext Open() => new(_options);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        await using var db = Open();
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        if (!await db.RegisterStates.AnyAsync(ct))
        {
            db.RegisterStates.Add(new RegisterState());
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        await using var db = Open();
        return (await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, ct))?.Value;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = Open();
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.Settings.Add(new LocalSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<T?> GetSettingAsync<T>(string key, CancellationToken ct = default) =>
        await GetSettingAsync(key, ct) is { } json ? JsonSerializer.Deserialize<T>(json, Json) : default;

    public Task SetSettingAsync<T>(string key, T value, CancellationToken ct = default) =>
        SetSettingAsync(key, JsonSerializer.Serialize(value, Json), ct);

    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        await using var db = Open();
        return await db.Outbox.CountAsync(o => o.Status != OutboxStatus.Sent, ct);
    }
}

public static class SettingKeys
{
    public const string Profile = "register.profile";
    public const string RegisterId = "register.id";
    public const string ServerUrl = "register.serverUrl";
    public const string ReferenceCursor = "sync.referenceCursor";
    public const string LastReferenceSync = "sync.lastReferenceSync";
}
