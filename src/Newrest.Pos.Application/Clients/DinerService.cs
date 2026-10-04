using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Auditing;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Clients;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Application.Clients;

/// <summary>Diners, their badges and accounts, and bulk import from CSV / Excel.</summary>
public sealed class DinerService(IPosDbContext db, AccessControl access, AuditTrail audit, TimeProvider clock)
{
    public const int MaxImportLines = 20_000;

    public async Task<PagedResult<DinerDto>> ListAsync(Guid clientCompanyId, string? search = null, int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        await access.EnsureCanAccessClientAsync(clientCompanyId, write: false, ct);
        (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, 200));
        var query = db.Diners.AsNoTracking().Where(d => d.ClientCompanyId == clientCompanyId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var badgeDiners = db.Badges.Where(b => b.Number.Contains(term)).Select(b => b.DinerId);
            query = query.Where(d => d.EmployeeNumber.Contains(term) || d.LastName.Contains(term) || d.FirstName.Contains(term)
                                     || badgeDiners.Contains(d.Id));
        }

        var total = await query.CountAsync(ct);
        var diners = await query.OrderBy(d => d.LastName).ThenBy(d => d.FirstName)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<DinerDto>(await ToDtosAsync(diners, ct), total, page, pageSize);
    }

    public async Task<DinerDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var diner = await db.Diners.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Diner", id);
        await access.EnsureCanAccessClientAsync(diner.ClientCompanyId, write: false, ct);
        return (await ToDtosAsync([diner], ct))[0];
    }

    /// <summary>Badge lookup (back-office search box, support).</summary>
    public async Task<DinerDto> FindByBadgeAsync(string badgeNumber, CancellationToken ct = default)
    {
        var number = Badge.NormalizeNumber(badgeNumber);
        var dinerId = await db.Badges.AsNoTracking().Where(b => b.Number == number).Select(b => (Guid?)b.DinerId).SingleOrDefaultAsync(ct)
                      ?? throw new NotFoundException("Badge", number);
        return await GetAsync(dinerId, ct);
    }

    public async Task<DinerDto> CreateAsync(DinerCreate request, CancellationToken ct = default)
    {
        var companyId = await access.EnsureCanAccessClientAsync(request.ClientCompanyId, write: true, ct);
        await EnsureContractAsync(request.ContractId, request.ClientCompanyId, ct);
        var number = Guard.NotBlank(request.EmployeeNumber, nameof(request.EmployeeNumber), 32);
        if (await db.Diners.AnyAsync(d => d.ClientCompanyId == request.ClientCompanyId && d.EmployeeNumber == number, ct))
        {
            throw new ConflictException($"Employee number '{number}' already exists for this client.");
        }

        var diner = new Diner(Guid.CreateVersion7(), request.ClientCompanyId, number, request.FirstName, request.LastName)
        { Category = request.Category.Clean()?.ToUpperInvariant() };
        var account = new Account(Guid.CreateVersion7(), diner.Id, request.ContractId,
            Mappings.Parse<AccountType>(request.AccountType, "account type"), request.OverdraftLimit);
        db.Diners.Add(diner);
        db.Accounts.Add(account);
        if (request.BadgeNumber.Clean() is { } badgeNumber)
        {
            await EnsureBadgeFreeAsync(badgeNumber, ct);
            db.Badges.Add(new Badge(Guid.CreateVersion7(), diner.Id, badgeNumber, clock.GetUtcNow()));
        }

        audit.Record(AuditActions.Created, nameof(Diner), diner.Id, after: request, companyId: companyId);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([diner], ct))[0];
    }

    public async Task<DinerDto> UpdateAsync(Guid id, DinerUpdate request, CancellationToken ct = default)
    {
        var diner = await db.Diners.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Diner", id);
        var companyId = await access.EnsureCanAccessClientAsync(diner.ClientCompanyId, write: true, ct);
        var before = new { diner.FirstName, diner.LastName, diner.Category, diner.IsActive };
        diner.FirstName = Guard.NotBlank(request.FirstName, nameof(request.FirstName), 100);
        diner.LastName = Guard.NotBlank(request.LastName, nameof(request.LastName), 100);
        diner.Category = request.Category.Clean()?.ToUpperInvariant();
        diner.IsActive = request.IsActive;
        audit.Record(AuditActions.Updated, nameof(Diner), id, before, request, companyId);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([diner], ct))[0];
    }

    // ----- Badges ----------------------------------------------------------------------------------------------------

    public async Task<BadgeDto> IssueBadgeAsync(Guid dinerId, BadgeIssue request, CancellationToken ct = default)
    {
        var diner = await db.Diners.AsNoTracking().SingleOrDefaultAsync(d => d.Id == dinerId, ct) ?? throw new NotFoundException("Diner", dinerId);
        var companyId = await access.EnsureCanAccessClientAsync(diner.ClientCompanyId, write: true, ct);
        if (await db.Badges.AnyAsync(b => b.DinerId == dinerId && b.Status == BadgeStatus.Active, ct))
        {
            throw new DomainException("badge_already_active", "The diner already has an active badge; declare it lost to replace it.");
        }

        await EnsureBadgeFreeAsync(request.Number, ct);
        var badge = new Badge(Guid.CreateVersion7(), dinerId, request.Number, clock.GetUtcNow());
        db.Badges.Add(badge);
        audit.Record(AuditActions.BadgeIssued, nameof(Badge), badge.Id, after: badge.ToDto(), companyId: companyId);
        await db.SaveChangesAsync(ct);
        return badge.ToDto();
    }

    /// <summary>Lost badge: blocked at once, a new badge is issued; the balance stays on the account.</summary>
    public async Task<BadgeDto> ReplaceLostBadgeAsync(Guid badgeId, BadgeReplace request, CancellationToken ct = default)
    {
        var (badge, companyId) = await LoadBadgeAsync(badgeId, ct);
        await EnsureBadgeFreeAsync(request.NewNumber, ct);
        var before = badge.ToDto();
        var replacement = badge.ReplaceAsLost(request.NewNumber, clock.GetUtcNow());
        db.Badges.Add(replacement);
        audit.Record(AuditActions.BadgeLost, nameof(Badge), badgeId, before, new { Lost = badge.ToDto(), Replacement = replacement.ToDto() }, companyId);
        await db.SaveChangesAsync(ct);
        return replacement.ToDto();
    }

    public async Task<BadgeDto> SetBadgeBlockedAsync(Guid badgeId, bool blocked, CancellationToken ct = default)
    {
        var (badge, companyId) = await LoadBadgeAsync(badgeId, ct);
        var before = badge.ToDto();
        if (blocked)
        {
            badge.Block(clock.GetUtcNow());
        }
        else
        {
            if (await db.Badges.AnyAsync(b => b.DinerId == badge.DinerId && b.Status == BadgeStatus.Active && b.Id != badgeId, ct))
            {
                throw new DomainException("badge_already_active", "The diner already has another active badge.");
            }

            badge.Unblock();
        }

        audit.Record(blocked ? AuditActions.BadgeBlocked : AuditActions.BadgeUnblocked, nameof(Badge), badgeId, before, badge.ToDto(), companyId);
        await db.SaveChangesAsync(ct);
        return badge.ToDto();
    }

    private async Task<(Badge Badge, Guid CompanyId)> LoadBadgeAsync(Guid badgeId, CancellationToken ct)
    {
        var badge = await db.Badges.SingleOrDefaultAsync(b => b.Id == badgeId, ct) ?? throw new NotFoundException("Badge", badgeId);
        var clientId = await db.Diners.Where(d => d.Id == badge.DinerId).Select(d => d.ClientCompanyId).SingleAsync(ct);
        var companyId = await access.EnsureCanAccessClientAsync(clientId, write: true, ct);
        return (badge, companyId);
    }

    private async Task EnsureBadgeFreeAsync(string number, CancellationToken ct)
    {
        var normalized = Badge.NormalizeNumber(number);
        if (await db.Badges.AnyAsync(b => b.Number == normalized, ct))
        {
            throw new ConflictException($"Badge '{normalized}' is already registered (badge numbers are never reused).");
        }
    }

    private async Task EnsureContractAsync(Guid contractId, Guid clientCompanyId, CancellationToken ct)
    {
        if (!await db.Contracts.AnyAsync(c => c.Id == contractId && c.ClientCompanyId == clientCompanyId, ct))
        {
            throw new DomainException("invalid_contract", "The contract does not exist or belongs to another client.");
        }
    }

    // ----- Import ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates or updates diners from a CSV (UTF-8, ';' or ',') or Excel (.xlsx, first sheet) file with a header row.
    /// Columns (French or English): Matricule, Nom, Prénom, Catégorie, Badge, TypeCompte, Découvert, Actif.
    /// Invalid lines are reported and skipped; with <paramref name="dryRun"/> nothing is saved.
    /// </summary>
    public async Task<DinerImportResult> ImportAsync(Guid clientCompanyId, Guid contractId, string fileName, Stream content, bool dryRun = false,
        CancellationToken ct = default)
    {
        var companyId = await access.EnsureCanAccessClientAsync(clientCompanyId, write: true, ct);
        await EnsureContractAsync(contractId, clientCompanyId, ct);
        var rows = DinerImportReader.Read(fileName, content);
        if (rows.Count > MaxImportLines)
        {
            throw new DomainException("import_too_large", $"At most {MaxImportLines} lines can be imported at once.");
        }

        var diners = await db.Diners.Where(d => d.ClientCompanyId == clientCompanyId).ToDictionaryAsync(d => d.EmployeeNumber, ct);
        var dinerIds = diners.Values.Select(d => d.Id).ToList();
        var accounts = await db.Accounts.Where(a => a.ContractId == contractId && dinerIds.Contains(a.DinerId)).ToDictionaryAsync(a => a.DinerId, ct);
        var activeBadges = await db.Badges.Where(b => dinerIds.Contains(b.DinerId) && b.Status == BadgeStatus.Active)
            .ToDictionaryAsync(b => b.DinerId, b => b.Number, ct);
        var importedBadges = rows.Select(r => r.BadgeNumber).Where(n => n is not null).Select(n => n!.ToUpperInvariant()).ToList();
        var takenBadges = (await db.Badges.Where(b => importedBadges.Contains(b.Number)).Select(b => new { b.Number, b.DinerId }).ToListAsync(ct))
            .ToDictionary(b => b.Number, b => b.DinerId);

        int created = 0, updated = 0, unchanged = 0;
        var errors = new List<DinerImportLineError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = clock.GetUtcNow();

        foreach (var row in rows)
        {
            try
            {
                if (row.Error is not null)
                {
                    throw new DomainException("invalid_line", row.Error);
                }

                var number = Guard.NotBlank(row.EmployeeNumber, "Matricule", 32);
                if (!seen.Add(number))
                {
                    throw new DomainException("duplicate_line", "Matricule présent plusieurs fois dans le fichier.");
                }

                var accountType = row.AccountType.Clean() is { } t ? Mappings.Parse<AccountType>(t, "TypeCompte") : (AccountType?)null;
                var badgeNumber = row.BadgeNumber.Clean() is { } bn ? Badge.NormalizeNumber(bn) : null;

                if (!diners.TryGetValue(number, out var diner))
                {
                    diner = new Diner(Guid.CreateVersion7(), clientCompanyId, number, row.FirstName!, row.LastName!)
                    { Category = row.Category.Clean()?.ToUpperInvariant(), IsActive = row.IsActive ?? true };
                    var account = new Account(Guid.CreateVersion7(), diner.Id, contractId, accountType ?? AccountType.Prepaid, row.OverdraftLimit ?? 0m);
                    if (badgeNumber is not null)
                    {
                        if (takenBadges.ContainsKey(badgeNumber))
                        {
                            throw new DomainException("badge_taken", $"Badge {badgeNumber} déjà attribué.");
                        }

                        db.Badges.Add(new Badge(Guid.CreateVersion7(), diner.Id, badgeNumber, now));
                        takenBadges[badgeNumber] = diner.Id;
                    }

                    db.Diners.Add(diner);
                    db.Accounts.Add(account);
                    diners[number] = diner;
                    created++;
                    continue;
                }

                // Validate the whole line before touching tracked entities: a rejected line leaves no partial change.
                var firstName = row.FirstName.Clean() is { } f ? Guard.NotBlank(f, "Prénom", 100) : null;
                var lastName = row.LastName.Clean() is { } l ? Guard.NotBlank(l, "Nom", 100) : null;
                if (row.OverdraftLimit is { } od)
                {
                    Money.EnsureValid(Guard.NotNegative(od, "Découvert"), "Découvert");
                }

                var issueBadge = false;
                if (badgeNumber is not null && activeBadges.GetValueOrDefault(diner.Id) != badgeNumber)
                {
                    if (activeBadges.ContainsKey(diner.Id))
                    {
                        throw new DomainException("badge_mismatch",
                            "Le convive a déjà un badge actif différent : utiliser le remplacement de badge (badge perdu).");
                    }

                    if (takenBadges.TryGetValue(badgeNumber, out var owner) && owner != diner.Id)
                    {
                        throw new DomainException("badge_taken", $"Badge {badgeNumber} déjà attribué.");
                    }

                    issueBadge = !takenBadges.ContainsKey(badgeNumber);
                }

                var changed = false;
                if (firstName is not null && firstName != diner.FirstName)
                {
                    diner.FirstName = firstName;
                    changed = true;
                }

                if (lastName is not null && lastName != diner.LastName)
                {
                    diner.LastName = lastName;
                    changed = true;
                }

                var category = row.Category.Clean()?.ToUpperInvariant();
                if (row.Category is not null && category != diner.Category)
                {
                    diner.Category = category;
                    changed = true;
                }

                if (row.IsActive is { } active && active != diner.IsActive)
                {
                    diner.IsActive = active;
                    changed = true;
                }

                if (!accounts.TryGetValue(diner.Id, out var existingAccount))
                {
                    existingAccount = new Account(Guid.CreateVersion7(), diner.Id, contractId, accountType ?? AccountType.Prepaid, row.OverdraftLimit ?? 0m);
                    db.Accounts.Add(existingAccount);
                    accounts[diner.Id] = existingAccount;
                    changed = true;
                }
                else
                {
                    if (accountType is { } type && type != existingAccount.Type)
                    {
                        existingAccount.Type = type;
                        changed = true;
                    }

                    if (row.OverdraftLimit is { } overdraft && overdraft != existingAccount.OverdraftLimit)
                    {
                        existingAccount.SetOverdraftLimit(overdraft);
                        changed = true;
                    }
                }

                if (issueBadge)
                {
                    db.Badges.Add(new Badge(Guid.CreateVersion7(), diner.Id, badgeNumber!, now));
                    takenBadges[badgeNumber!] = diner.Id;
                    activeBadges[diner.Id] = badgeNumber!;
                    changed = true;
                }

                if (changed)
                {
                    updated++;
                }
                else
                {
                    unchanged++;
                }
            }
            catch (DomainException ex)
            {
                errors.Add(new DinerImportLineError(row.Line, row.EmployeeNumber ?? string.Empty, ex.Message));
            }
        }

        var result = new DinerImportResult(created, updated, unchanged, errors);
        if (!dryRun)
        {
            audit.Record(AuditActions.DinersImported, nameof(ClientCompany), clientCompanyId,
                after: new { fileName, contractId, created, updated, unchanged, Errors = errors.Count }, companyId: companyId);
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    private async Task<List<DinerDto>> ToDtosAsync(IReadOnlyCollection<Diner> diners, CancellationToken ct)
    {
        var ids = diners.Select(d => d.Id).ToList();
        var badges = (await db.Badges.AsNoTracking().Where(b => ids.Contains(b.DinerId)).ToListAsync(ct)).ToLookup(b => b.DinerId);
        var accounts = (await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.DinerId)).ToListAsync(ct)).ToLookup(a => a.DinerId);
        return [.. diners.Select(d => new DinerDto(d.Id, d.ClientCompanyId, d.EmployeeNumber, d.FirstName, d.LastName, d.Category, d.IsActive,
            [.. badges[d.Id].OrderByDescending(b => b.IssuedAt).Select(b => b.ToDto())],
            [.. accounts[d.Id].Select(a => a.ToSummary())]))];
    }
}
