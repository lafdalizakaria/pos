using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Domain.Security;

namespace Newrest.Pos.Application.Security;

/// <summary>Company / site scope of the current back-office user.</summary>
public sealed record UserScope(bool IsGlobal, IReadOnlyList<(Guid CompanyId, Guid? SiteId)> Entries)
{
    public static readonly UserScope None = new(false, []);

    public IReadOnlySet<Guid> CompanyIds { get; } = Entries.Select(e => e.CompanyId).ToHashSet();

    /// <summary>Read access to company-level data (clients, diners, accounts): any scope inside the company.</summary>
    public bool CanReadCompany(Guid companyId) => IsGlobal || CompanyIds.Contains(companyId);

    /// <summary>Company-wide management (sites, company price lists, clients): requires a whole-company scope.</summary>
    public bool CanManageCompany(Guid companyId) => IsGlobal || Entries.Any(e => e.CompanyId == companyId && e.SiteId is null);

    public bool CanAccessSite(Guid companyId, Guid siteId) =>
        IsGlobal || Entries.Any(e => e.CompanyId == companyId && (e.SiteId is null || e.SiteId == siteId));
}

/// <summary>
/// Single place where roles and scopes are enforced, shared by the API and the back-office.
/// Admin = global. Other roles are limited to the companies/sites granted in <see cref="UserAccessScope"/>.
/// </summary>
public sealed class AccessControl(IPosDbContext db, ICurrentUser user)
{
    private UserScope? _scope;

    public ICurrentUser User => user;

    public static readonly string[] Writers = [PosRoles.Admin, PosRoles.Manager];
    public static readonly string[] Finance = [PosRoles.Admin, PosRoles.Accountant];
    public static readonly string[] MoneyIn = [PosRoles.Admin, PosRoles.Manager, PosRoles.Accountant];

    public void RequireAnyRole(params string[] roles)
    {
        if (!user.IsAuthenticated || !roles.Any(user.IsInRole))
        {
            throw new ForbiddenException($"This action requires one of the roles: {string.Join(", ", roles)}.");
        }
    }

    public void RequireBackOfficeUser() => RequireAnyRole(PosRoles.BackOffice);

    public async Task<UserScope> GetScopeAsync(CancellationToken ct = default)
    {
        if (_scope is not null)
        {
            return _scope;
        }

        if (!user.IsAuthenticated || !PosRoles.BackOffice.Any(user.IsInRole))
        {
            return _scope = UserScope.None;
        }

        if (user.IsInRole(PosRoles.Admin))
        {
            return _scope = new UserScope(true, []);
        }

        var name = UserAccessScope.NormalizeUserName(user.Name);
        var entries = await db.UserAccessScopes.AsNoTracking()
            .Where(s => s.UserName == name)
            .Select(s => new { s.CompanyId, s.SiteId })
            .ToListAsync(ct);
        return _scope = new UserScope(false, [.. entries.Select(e => (e.CompanyId, e.SiteId))]);
    }

    public async Task EnsureCanReadCompanyAsync(Guid companyId, CancellationToken ct = default)
    {
        RequireBackOfficeUser();
        if (!(await GetScopeAsync(ct)).CanReadCompany(companyId))
        {
            throw new ForbiddenException("You do not have access to this company.");
        }
    }

    public async Task EnsureCanManageCompanyAsync(Guid companyId, CancellationToken ct = default, params string[] roles)
    {
        RequireAnyRole(roles.Length > 0 ? roles : Writers);
        if (!(await GetScopeAsync(ct)).CanManageCompany(companyId))
        {
            throw new ForbiddenException("This action requires a company-wide scope.");
        }
    }

    public async Task<Guid> EnsureCanAccessSiteAsync(Guid siteId, bool write, CancellationToken ct = default)
    {
        if (write)
        {
            RequireAnyRole(Writers);
        }
        else
        {
            RequireBackOfficeUser();
        }

        var companyId = await db.Sites.AsNoTracking().Where(s => s.Id == siteId).Select(s => (Guid?)s.CompanyId).SingleOrDefaultAsync(ct)
                        ?? throw new NotFoundException("Site", siteId);
        if (!(await GetScopeAsync(ct)).CanAccessSite(companyId, siteId))
        {
            throw new ForbiddenException("You do not have access to this site.");
        }

        return companyId;
    }

    public async Task<(Guid CompanyId, Guid SiteId)> EnsureCanAccessPointOfSaleAsync(Guid pointOfSaleId, bool write, CancellationToken ct = default)
    {
        var siteId = await db.PointsOfSale.AsNoTracking().Where(p => p.Id == pointOfSaleId).Select(p => (Guid?)p.SiteId).SingleOrDefaultAsync(ct)
                     ?? throw new NotFoundException("PointOfSale", pointOfSaleId);
        var companyId = await EnsureCanAccessSiteAsync(siteId, write, ct);
        return (companyId, siteId);
    }

    /// <summary>Company owning a B2B client; the caller must be allowed to read (or manage) it.</summary>
    public async Task<Guid> EnsureCanAccessClientAsync(Guid clientCompanyId, bool write, CancellationToken ct = default, params string[] writeRoles)
    {
        var companyId = await db.ClientCompanies.AsNoTracking().Where(c => c.Id == clientCompanyId)
                            .Select(c => (Guid?)c.CompanyId).SingleOrDefaultAsync(ct)
                        ?? throw new NotFoundException("ClientCompany", clientCompanyId);
        if (write)
        {
            RequireAnyRole(writeRoles.Length > 0 ? writeRoles : Writers);
        }

        await EnsureCanReadCompanyAsync(companyId, ct);
        return companyId;
    }
}
