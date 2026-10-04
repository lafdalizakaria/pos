using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Auditing;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Domain.Organization;
using Newrest.Pos.Domain.Security;

namespace Newrest.Pos.Application.Organization;

/// <summary>Companies, sites, points of sale, registers (and their device keys), operators and user scopes.</summary>
public sealed class OrganizationService(IPosDbContext db, AccessControl access, AuditTrail audit, IPinHasher pinHasher, TimeProvider clock)
{
    // ----- Companies (global administrators only for writes) ---------------------------------------------------------

    public async Task<IReadOnlyList<CompanyDto>> ListCompaniesAsync(CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        var companies = await db.Companies.AsNoTracking().OrderBy(c => c.Code).ToListAsync(ct);
        return [.. companies.Where(c => scope.CanReadCompany(c.Id)).Select(c => c.ToDto())];
    }

    public async Task<CompanyDto> CreateCompanyAsync(CompanyUpsert request, CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        var code = request.Code?.Trim().ToUpperInvariant();
        if (await db.Companies.AnyAsync(c => c.Code == code, ct))
        {
            throw new ConflictException($"Company code '{code}' is already used.");
        }

        var company = new Company(Guid.CreateVersion7(), request.Code!, request.Name, request.LegalName);
        Apply(company, request);
        db.Companies.Add(company);
        audit.Record(AuditActions.Created, nameof(Company), company.Id, after: request, companyId: company.Id);
        await db.SaveChangesAsync(ct);
        return company.ToDto();
    }

    public async Task<CompanyDto> UpdateCompanyAsync(Guid id, CompanyUpsert request, CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Company", id);
        var before = company.ToDto();
        company.Update(request.Name, request.LegalName);
        Apply(company, request);
        audit.Record(AuditActions.Updated, nameof(Company), id, before, company.ToDto(), companyId: id);
        await db.SaveChangesAsync(ct);
        return company.ToDto();
    }

    private static void Apply(Company company, CompanyUpsert request)
    {
        company.Ice = request.Ice.Clean();
        company.TaxId = request.TaxId.Clean();
        company.TradeRegister = request.TradeRegister.Clean();
        company.Address = request.Address.Clean();
        company.IsActive = request.IsActive;
    }

    // ----- Sites ---------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SiteDto>> ListSitesAsync(Guid? companyId = null, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        var sites = await db.Sites.AsNoTracking()
            .Where(s => companyId == null || s.CompanyId == companyId)
            .OrderBy(s => s.Code).ToListAsync(ct);
        return [.. sites.Where(s => scope.CanAccessSite(s.CompanyId, s.Id)).Select(s => s.ToDto())];
    }

    public async Task<SiteDto> CreateSiteAsync(SiteUpsert request, CancellationToken ct = default)
    {
        await access.EnsureCanManageCompanyAsync(request.CompanyId, ct);
        if (!await db.Companies.AnyAsync(c => c.Id == request.CompanyId, ct))
        {
            throw new NotFoundException("Company", request.CompanyId);
        }

        var code = request.Code?.Trim().ToUpperInvariant();
        if (await db.Sites.AnyAsync(s => s.CompanyId == request.CompanyId && s.Code == code, ct))
        {
            throw new ConflictException($"Site code '{code}' is already used in this company.");
        }

        var site = new Site(Guid.CreateVersion7(), request.CompanyId, request.Code!, request.Name, request.City);
        Apply(site, request);
        db.Sites.Add(site);
        audit.Record(AuditActions.Created, nameof(Site), site.Id, after: request, companyId: site.CompanyId, siteId: site.Id);
        await db.SaveChangesAsync(ct);
        return site.ToDto();
    }

    public async Task<SiteDto> UpdateSiteAsync(Guid id, SiteUpsert request, CancellationToken ct = default)
    {
        var site = await db.Sites.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Site", id);
        await access.EnsureCanManageCompanyAsync(site.CompanyId, ct);
        var before = site.ToDto();
        site.Update(request.Name, request.City);
        Apply(site, request);
        audit.Record(AuditActions.Updated, nameof(Site), id, before, site.ToDto(), site.CompanyId, site.Id);
        await db.SaveChangesAsync(ct);
        return site.ToDto();
    }

    private static void Apply(Site site, SiteUpsert request)
    {
        site.Address = request.Address.Clean();
        if (request.TimeZone.Clean() is { } tz)
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))
            {
                throw new Domain.Common.DomainException("invalid_value", $"Unknown time zone '{tz}'.");
            }

            site.TimeZone = tz;
        }

        site.IsActive = request.IsActive;
    }

    // ----- Points of sale ------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PointOfSaleDto>> ListPointsOfSaleAsync(Guid? siteId = null, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        var rows = await (from p in db.PointsOfSale.AsNoTracking()
                          join s in db.Sites.AsNoTracking() on p.SiteId equals s.Id
                          where siteId == null || p.SiteId == siteId
                          orderby p.Code
                          select new { Pos = p, s.CompanyId }).ToListAsync(ct);
        return [.. rows.Where(r => scope.CanAccessSite(r.CompanyId, r.Pos.SiteId)).Select(r => r.Pos.ToDto())];
    }

    public async Task<PointOfSaleDto> CreatePointOfSaleAsync(PointOfSaleUpsert request, CancellationToken ct = default)
    {
        var companyId = await access.EnsureCanAccessSiteAsync(request.SiteId, write: true, ct);
        var code = request.Code?.Trim().ToUpperInvariant();
        if (await db.PointsOfSale.AnyAsync(p => p.SiteId == request.SiteId && p.Code == code, ct))
        {
            throw new ConflictException($"Point of sale code '{code}' is already used on this site.");
        }

        var pos = new PointOfSale(Guid.CreateVersion7(), request.SiteId, request.Code!, request.Name,
            Mappings.Parse<PointOfSaleType>(request.Type, "point of sale type"))
        { IsActive = request.IsActive };
        db.PointsOfSale.Add(pos);
        audit.Record(AuditActions.Created, nameof(PointOfSale), pos.Id, after: request, companyId: companyId, siteId: request.SiteId);
        await db.SaveChangesAsync(ct);
        return pos.ToDto();
    }

    public async Task<PointOfSaleDto> UpdatePointOfSaleAsync(Guid id, PointOfSaleUpsert request, CancellationToken ct = default)
    {
        var pos = await db.PointsOfSale.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("PointOfSale", id);
        var companyId = await access.EnsureCanAccessSiteAsync(pos.SiteId, write: true, ct);
        var before = pos.ToDto();
        pos.Rename(request.Name);
        pos.Type = Mappings.Parse<PointOfSaleType>(request.Type, "point of sale type");
        pos.IsActive = request.IsActive;
        audit.Record(AuditActions.Updated, nameof(PointOfSale), id, before, pos.ToDto(), companyId, pos.SiteId);
        await db.SaveChangesAsync(ct);
        return pos.ToDto();
    }

    // ----- Registers -----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<RegisterDto>> ListRegistersAsync(Guid? pointOfSaleId = null, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        var rows = await (from r in db.Registers.AsNoTracking()
                          join p in db.PointsOfSale.AsNoTracking() on r.PointOfSaleId equals p.Id
                          join s in db.Sites.AsNoTracking() on p.SiteId equals s.Id
                          where pointOfSaleId == null || r.PointOfSaleId == pointOfSaleId
                          orderby r.TicketPrefix
                          select new { Register = r, s.CompanyId, SiteId = s.Id }).ToListAsync(ct);
        return [.. rows.Where(r => scope.CanAccessSite(r.CompanyId, r.SiteId)).Select(r => r.Register.ToDto())];
    }

    public async Task<RegisterDto> CreateRegisterAsync(RegisterUpsert request, CancellationToken ct = default)
    {
        var (companyId, siteId) = await access.EnsureCanAccessPointOfSaleAsync(request.PointOfSaleId, write: true, ct);
        var prefix = request.TicketPrefix?.Trim().ToUpperInvariant();
        var code = request.Code?.Trim().ToUpperInvariant();
        if (await db.Registers.AnyAsync(r => r.TicketPrefix == prefix, ct))
        {
            throw new ConflictException($"Ticket prefix '{prefix}' is already used by another register.");
        }

        if (await db.Registers.AnyAsync(r => r.PointOfSaleId == request.PointOfSaleId && r.Code == code, ct))
        {
            throw new ConflictException($"Register code '{code}' is already used in this point of sale.");
        }

        var register = new Register(Guid.CreateVersion7(), request.PointOfSaleId, request.Code!, request.Name, request.TicketPrefix!)
        { IsActive = request.IsActive };
        db.Registers.Add(register);
        audit.Record(AuditActions.Created, nameof(Register), register.Id, after: request, companyId: companyId, siteId: siteId);
        await db.SaveChangesAsync(ct);
        return register.ToDto();
    }

    /// <summary>Only the name and the active flag can change: the ticket prefix is part of the fiscal numbering.</summary>
    public async Task<RegisterDto> UpdateRegisterAsync(Guid id, RegisterUpsert request, CancellationToken ct = default)
    {
        var register = await db.Registers.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Register", id);
        var (companyId, siteId) = await access.EnsureCanAccessPointOfSaleAsync(register.PointOfSaleId, write: true, ct);
        if (!string.Equals(request.TicketPrefix?.Trim(), register.TicketPrefix, StringComparison.OrdinalIgnoreCase)
            || request.PointOfSaleId != register.PointOfSaleId)
        {
            throw new Domain.Common.DomainException("immutable_field", "The ticket prefix and the point of sale of a register cannot change.");
        }

        var before = register.ToDto();
        register.Rename(request.Name);
        register.IsActive = request.IsActive;
        if (!register.IsActive)
        {
            register.RevokeDeviceKey();
        }

        audit.Record(AuditActions.Updated, nameof(Register), id, before, register.ToDto(), companyId, siteId);
        await db.SaveChangesAsync(ct);
        return register.ToDto();
    }

    /// <summary>Issues a new device key (shown once). The previous key, if any, stops working immediately.</summary>
    public async Task<DeviceKeyIssued> IssueDeviceKeyAsync(Guid registerId, CancellationToken ct = default)
    {
        var register = await db.Registers.SingleOrDefaultAsync(r => r.Id == registerId, ct) ?? throw new NotFoundException("Register", registerId);
        var (companyId, siteId) = await access.EnsureCanAccessPointOfSaleAsync(register.PointOfSaleId, write: true, ct);
        if (!register.IsActive)
        {
            throw new Domain.Common.DomainException("register_inactive", "Activate the register before issuing a key.");
        }

        var key = DeviceKeys.Generate();
        var now = clock.GetUtcNow();
        register.SetDeviceKeyHash(DeviceKeys.Hash(key), now);
        audit.Record(AuditActions.DeviceKeyIssued, nameof(Register), registerId, companyId: companyId, siteId: siteId);
        await db.SaveChangesAsync(ct);
        return new DeviceKeyIssued(registerId, key, now);
    }

    public async Task RevokeDeviceKeyAsync(Guid registerId, CancellationToken ct = default)
    {
        var register = await db.Registers.SingleOrDefaultAsync(r => r.Id == registerId, ct) ?? throw new NotFoundException("Register", registerId);
        var (companyId, siteId) = await access.EnsureCanAccessPointOfSaleAsync(register.PointOfSaleId, write: true, ct);
        register.RevokeDeviceKey();
        audit.Record(AuditActions.DeviceKeyRevoked, nameof(Register), registerId, companyId: companyId, siteId: siteId);
        await db.SaveChangesAsync(ct);
    }

    // ----- Operators -----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<OperatorDto>> ListOperatorsAsync(Guid? companyId = null, CancellationToken ct = default)
    {
        var scope = await ReadScopeAsync(ct);
        var now = clock.GetUtcNow();
        var operators = await db.Operators.AsNoTracking()
            .Where(o => companyId == null || o.CompanyId == companyId)
            .OrderBy(o => o.Code).ToListAsync(ct);
        return [.. operators
            .Where(o => o.SiteId is { } s ? scope.CanAccessSite(o.CompanyId, s) : scope.CanReadCompany(o.CompanyId))
            .Select(o => o.ToDto(now))];
    }

    public async Task<OperatorDto> CreateOperatorAsync(OperatorCreate request, CancellationToken ct = default)
    {
        await EnsureOperatorScopeAsync(request.CompanyId, request.SiteId, ct);
        var code = request.Code?.Trim().ToUpperInvariant();
        if (await db.Operators.AnyAsync(o => o.CompanyId == request.CompanyId && o.Code == code, ct))
        {
            throw new ConflictException($"Operator code '{code}' is already used in this company.");
        }

        var roles = Mappings.ParseRoles(request.Roles);
        var op = new Operator(Guid.CreateVersion7(), request.CompanyId, request.Code!, request.FirstName, request.LastName,
            roles, pinHasher.Hash(request.Pin))
        { SiteId = request.SiteId };
        db.Operators.Add(op);
        audit.Record(AuditActions.Created, nameof(Operator), op.Id, after: op.ToDto(clock.GetUtcNow()), companyId: op.CompanyId, siteId: op.SiteId);
        await db.SaveChangesAsync(ct);
        return op.ToDto(clock.GetUtcNow());
    }

    public async Task<OperatorDto> UpdateOperatorAsync(Guid id, OperatorUpdate request, CancellationToken ct = default)
    {
        var op = await db.Operators.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Operator", id);
        await EnsureOperatorScopeAsync(op.CompanyId, op.SiteId, ct);
        await EnsureOperatorScopeAsync(op.CompanyId, request.SiteId, ct);
        var now = clock.GetUtcNow();
        var before = op.ToDto(now);
        op.Rename(request.FirstName, request.LastName);
        op.SetRoles(Mappings.ParseRoles(request.Roles));
        op.SiteId = request.SiteId;
        op.IsActive = request.IsActive;
        audit.Record(AuditActions.Updated, nameof(Operator), id, before, op.ToDto(now), op.CompanyId, op.SiteId);
        await db.SaveChangesAsync(ct);
        return op.ToDto(now);
    }

    public async Task SetOperatorPinAsync(Guid id, string pin, CancellationToken ct = default)
    {
        var op = await db.Operators.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Operator", id);
        await EnsureOperatorScopeAsync(op.CompanyId, op.SiteId, ct);
        op.ChangePin(pin, pinHasher);
        audit.Record(AuditActions.PinChanged, nameof(Operator), id, companyId: op.CompanyId, siteId: op.SiteId);
        await db.SaveChangesAsync(ct);
    }

    public async Task UnlockOperatorAsync(Guid id, CancellationToken ct = default)
    {
        var op = await db.Operators.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Operator", id);
        await EnsureOperatorScopeAsync(op.CompanyId, op.SiteId, ct);
        op.Unlock();
        audit.Record(AuditActions.OperatorUnlocked, nameof(Operator), id, companyId: op.CompanyId, siteId: op.SiteId);
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureOperatorScopeAsync(Guid companyId, Guid? siteId, CancellationToken ct)
    {
        if (siteId is { } s)
        {
            var siteCompany = await access.EnsureCanAccessSiteAsync(s, write: true, ct);
            if (siteCompany != companyId)
            {
                throw new Domain.Common.DomainException("invalid_site", "The site belongs to another company.");
            }
        }
        else
        {
            await access.EnsureCanManageCompanyAsync(companyId, ct);
        }
    }

    // ----- User scopes (administrators only) -------------------------------------------------------------------------

    public async Task<CurrentUserDto> GetCurrentUserAsync(CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        var scope = await access.GetScopeAsync(ct);
        var name = UserAccessScope.NormalizeUserName(access.User.Name);
        var scopes = await db.UserAccessScopes.AsNoTracking().Where(s => s.UserName == name).ToListAsync(ct);
        return new CurrentUserDto(access.User.Name, [.. access.User.Roles.Order()], scope.IsGlobal, [.. scopes.Select(s => s.ToDto())]);
    }

    public async Task<IReadOnlyList<AccessScopeDto>> ListScopesAsync(CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        var scopes = await db.UserAccessScopes.AsNoTracking().OrderBy(s => s.UserName).ToListAsync(ct);
        return [.. scopes.Select(s => s.ToDto())];
    }

    public async Task<AccessScopeDto> GrantScopeAsync(AccessScopeCreate request, CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        if (request.SiteId is { } siteId
            && !await db.Sites.AnyAsync(s => s.Id == siteId && s.CompanyId == request.CompanyId, ct))
        {
            throw new Domain.Common.DomainException("invalid_site", "The site does not belong to the company.");
        }

        if (!await db.Companies.AnyAsync(c => c.Id == request.CompanyId, ct))
        {
            throw new NotFoundException("Company", request.CompanyId);
        }

        var scope = new UserAccessScope(Guid.CreateVersion7(), request.UserName, request.CompanyId, request.SiteId);
        if (await db.UserAccessScopes.AnyAsync(s => s.UserName == scope.UserName && s.CompanyId == scope.CompanyId && s.SiteId == scope.SiteId, ct))
        {
            throw new ConflictException("This scope is already granted.");
        }

        db.UserAccessScopes.Add(scope);
        audit.Record(AuditActions.ScopeGranted, nameof(UserAccessScope), scope.Id, after: scope.ToDto(), companyId: scope.CompanyId, siteId: scope.SiteId);
        await db.SaveChangesAsync(ct);
        return scope.ToDto();
    }

    public async Task RevokeScopeAsync(Guid id, CancellationToken ct = default)
    {
        access.RequireAnyRole(PosRoles.Admin);
        var scope = await db.UserAccessScopes.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("UserAccessScope", id);
        db.UserAccessScopes.Remove(scope);
        audit.Record(AuditActions.ScopeRevoked, nameof(UserAccessScope), id, before: scope.ToDto(), companyId: scope.CompanyId, siteId: scope.SiteId);
        await db.SaveChangesAsync(ct);
    }

    private async Task<UserScope> ReadScopeAsync(CancellationToken ct)
    {
        access.RequireBackOfficeUser();
        return await access.GetScopeAsync(ct);
    }
}

/// <summary>Device API keys: 256 random bits, only their SHA-256 is stored.</summary>
public static class DeviceKeys
{
    public const string Prefix = "nrpos_";

    public static string Generate() => Prefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static bool Matches(string? key, string? storedHash)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(storedHash) || !key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(key)), Encoding.ASCII.GetBytes(storedHash));
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
