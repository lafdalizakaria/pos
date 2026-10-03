using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Data;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;

namespace Newrest.Pos.Client.Core.Sync;

/// <summary>Offline view of the reference data (what the register knows without network).</summary>
public sealed class ReferenceCache(LocalStore store)
{
    public async Task<SyncBadgeDto?> FindBadgeAsync(string number, CancellationToken ct = default) =>
        await GetByKeyAsync<SyncBadgeDto>(CacheKind.Badge, number.Trim().ToUpperInvariant(), ct);

    public Task<SyncDinerDto?> GetDinerAsync(Guid id, CancellationToken ct = default) => GetAsync<SyncDinerDto>(CacheKind.Diner, id, ct);

    public async Task<IReadOnlyList<SyncAccountDto>> GetAccountsOfDinerAsync(Guid dinerId, CancellationToken ct = default) =>
        await ListByKeyAsync<SyncAccountDto>(CacheKind.Account, dinerId.ToString(), ct);

    public Task<SyncContractDto?> GetContractAsync(Guid id, CancellationToken ct = default) => GetAsync<SyncContractDto>(CacheKind.Contract, id, ct);

    public async Task<IReadOnlyList<SubsidyRuleDto>> GetRulesAsync(Guid contractId, CancellationToken ct = default) =>
        await ListByKeyAsync<SubsidyRuleDto>(CacheKind.SubsidyRule, contractId.ToString(), ct);

    public async Task<SyncOperatorDto?> FindOperatorAsync(string code, CancellationToken ct = default) =>
        await GetByKeyAsync<SyncOperatorDto>(CacheKind.Operator, code.Trim().ToUpperInvariant(), ct);

    public Task<SyncOperatorDto?> GetOperatorAsync(Guid id, CancellationToken ct = default) => GetAsync<SyncOperatorDto>(CacheKind.Operator, id, ct);

    public async Task<IReadOnlyList<DailyMenuDto>> GetMenusAsync(DateOnly date, CancellationToken ct = default) =>
        await ListByKeyAsync<DailyMenuDto>(CacheKind.Menu, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);

    public async Task<IReadOnlyList<SyncCategoryDto>> GetCategoriesAsync(CancellationToken ct = default) => await ListAsync<SyncCategoryDto>(CacheKind.Category, ct);

    public async Task<IReadOnlyDictionary<Guid, SyncArticleDto>> GetArticlesAsync(CancellationToken ct = default) =>
        (await ListAsync<SyncArticleDto>(CacheKind.Article, ct)).ToDictionary(a => a.Id);

    public async Task<int> CountAsync(CacheKind kind, CancellationToken ct = default)
    {
        await using var db = store.Open();
        return await db.Cache.CountAsync(c => c.Kind == kind, ct);
    }

    /// <summary>Applies a server snapshot. A full snapshot replaces everything of each kind (removals included).</summary>
    public async Task ApplyAsync(ReferenceSyncResponse r, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        await using var db = store.Open();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (r.IsFull)
        {
            await db.Cache.Where(c => c.Kind != CacheKind.Menu).ExecuteDeleteAsync(ct);
        }

        // Menus are always a full window.
        await db.Cache.Where(c => c.Kind == CacheKind.Menu).ExecuteDeleteAsync(ct);

        Upsert(db, CacheKind.Category, r.Categories, x => x.Id, x => x.Code, now);
        Upsert(db, CacheKind.Article, r.Articles, x => x.Id, x => x.Code, now);
        Upsert(db, CacheKind.Operator, r.Operators, x => x.Id, x => x.Code.ToUpperInvariant(), now);
        Upsert(db, CacheKind.Contract, r.Contracts, x => x.Id, x => x.ClientCompanyId.ToString(), now);
        Upsert(db, CacheKind.SubsidyRule, r.SubsidyRules, x => x.Id, x => x.ContractId.ToString(), now);
        Upsert(db, CacheKind.Diner, r.Diners, x => x.Id, x => x.EmployeeNumber, now);
        Upsert(db, CacheKind.Badge, r.Badges, x => x.Id, x => x.Number, now);
        Upsert(db, CacheKind.Account, r.Accounts, x => x.Id, x => x.DinerId.ToString(), now);
        Upsert(db, CacheKind.Menu, r.Menus, x => x.Id, x => x.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static void Upsert<T>(ClientDbContext db, CacheKind kind, IEnumerable<T> items, Func<T, Guid> id, Func<T, string> key, DateTimeOffset now)
    {
        foreach (var item in items)
        {
            var entryId = id(item);
            var json = JsonSerializer.Serialize(item, LocalStore.Json);
            var existing = db.Cache.Local.FirstOrDefault(c => c.Kind == kind && c.Id == entryId) ?? db.Cache.Find(kind, entryId);
            if (existing is null)
            {
                db.Cache.Add(new CacheEntry { Kind = kind, Id = entryId, LookupKey = key(item), Json = json, UpdatedAt = now });
            }
            else
            {
                existing.LookupKey = key(item);
                existing.Json = json;
                existing.UpdatedAt = now;
            }
        }
    }

    private async Task<T?> GetAsync<T>(CacheKind kind, Guid id, CancellationToken ct)
    {
        await using var db = store.Open();
        var entry = await db.Cache.AsNoTracking().SingleOrDefaultAsync(c => c.Kind == kind && c.Id == id, ct);
        return entry is null ? default : JsonSerializer.Deserialize<T>(entry.Json, LocalStore.Json);
    }

    private async Task<T?> GetByKeyAsync<T>(CacheKind kind, string key, CancellationToken ct) =>
        (await ListByKeyAsync<T>(kind, key, ct)).FirstOrDefault();

    private async Task<List<T>> ListByKeyAsync<T>(CacheKind kind, string key, CancellationToken ct)
    {
        await using var db = store.Open();
        var entries = await db.Cache.AsNoTracking().Where(c => c.Kind == kind && c.LookupKey == key).ToListAsync(ct);
        return [.. entries.Select(e => JsonSerializer.Deserialize<T>(e.Json, LocalStore.Json)!)];
    }

    private async Task<List<T>> ListAsync<T>(CacheKind kind, CancellationToken ct)
    {
        await using var db = store.Open();
        var entries = await db.Cache.AsNoTracking().Where(c => c.Kind == kind).ToListAsync(ct);
        return [.. entries.Select(e => JsonSerializer.Deserialize<T>(e.Json, LocalStore.Json)!)];
    }
}
