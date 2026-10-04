using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Auditing;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Domain.Clients;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Application.Clients;

/// <summary>B2B clients, contracts (accepted points of sale) and subsidy rules. Scoped by Newrest company.</summary>
public sealed class ClientService(IPosDbContext db, AccessControl access, AuditTrail audit)
{
    public async Task<IReadOnlyList<ClientCompanyDto>> ListClientsAsync(Guid? companyId = null, CancellationToken ct = default)
    {
        access.RequireBackOfficeUser();
        var scope = await access.GetScopeAsync(ct);
        var clients = await db.ClientCompanies.AsNoTracking()
            .Where(c => companyId == null || c.CompanyId == companyId)
            .OrderBy(c => c.Name).ToListAsync(ct);
        return [.. clients.Where(c => scope.CanReadCompany(c.CompanyId)).Select(c => c.ToDto())];
    }

    public async Task<ClientCompanyDto> GetClientAsync(Guid id, CancellationToken ct = default)
    {
        await access.EnsureCanAccessClientAsync(id, write: false, ct);
        return (await db.ClientCompanies.AsNoTracking().SingleAsync(c => c.Id == id, ct)).ToDto();
    }

    public async Task<ClientCompanyDto> CreateClientAsync(ClientCompanyUpsert request, CancellationToken ct = default)
    {
        await access.EnsureCanManageCompanyAsync(request.CompanyId, ct);
        var code = request.Code?.Trim().ToUpperInvariant();
        if (await db.ClientCompanies.AnyAsync(c => c.CompanyId == request.CompanyId && c.Code == code, ct))
        {
            throw new ConflictException($"Client code '{code}' is already used in this company.");
        }

        var client = new ClientCompany(Guid.CreateVersion7(), request.CompanyId, request.Code!, request.Name)
        {
            Ice = request.Ice.Clean(),
            BillingAddress = request.BillingAddress.Clean(),
            IsActive = request.IsActive,
        };
        db.ClientCompanies.Add(client);
        audit.Record(AuditActions.Created, nameof(ClientCompany), client.Id, after: request, companyId: request.CompanyId);
        await db.SaveChangesAsync(ct);
        return client.ToDto();
    }

    public async Task<ClientCompanyDto> UpdateClientAsync(Guid id, ClientCompanyUpsert request, CancellationToken ct = default)
    {
        var client = await db.ClientCompanies.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("ClientCompany", id);
        await access.EnsureCanManageCompanyAsync(client.CompanyId, ct);
        var before = client.ToDto();
        client.Name = Guard.NotBlank(request.Name, nameof(request.Name));
        client.Ice = request.Ice.Clean();
        client.BillingAddress = request.BillingAddress.Clean();
        client.IsActive = request.IsActive;
        audit.Record(AuditActions.Updated, nameof(ClientCompany), id, before, client.ToDto(), client.CompanyId);
        await db.SaveChangesAsync(ct);
        return client.ToDto();
    }

    // ----- Contracts -------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ContractDto>> ListContractsAsync(Guid clientCompanyId, CancellationToken ct = default)
    {
        await access.EnsureCanAccessClientAsync(clientCompanyId, write: false, ct);
        var contracts = await db.Contracts.AsNoTracking().Include(c => c.PointsOfSale).Include(c => c.SubsidyRules)
            .Where(c => c.ClientCompanyId == clientCompanyId).OrderByDescending(c => c.StartDate).ToListAsync(ct);
        return [.. contracts.Select(c => c.ToDto())];
    }

    public async Task<ContractDto> GetContractAsync(Guid id, CancellationToken ct = default)
    {
        var contract = await LoadContractAsync(id, tracked: false, ct);
        await access.EnsureCanAccessClientAsync(contract.ClientCompanyId, write: false, ct);
        return contract.ToDto();
    }

    public async Task<ContractDto> CreateContractAsync(ContractUpsert request, CancellationToken ct = default)
    {
        var companyId = await access.EnsureCanAccessClientAsync(request.ClientCompanyId, write: false, ct);
        await access.EnsureCanManageCompanyAsync(companyId, ct);
        var reference = request.Reference?.Trim();
        if (await db.Contracts.AnyAsync(c => c.Reference == reference, ct))
        {
            throw new ConflictException($"Contract reference '{reference}' is already used.");
        }

        var contract = new Contract(Guid.CreateVersion7(), request.ClientCompanyId, request.Reference!, request.StartDate, request.EndDate)
        {
            BillingMode = Mappings.Parse<BillingMode>(request.BillingMode, "billing mode"),
            IsActive = request.IsActive,
        };
        await SetPointsOfSaleAsync(contract, companyId, request.PointOfSaleIds, ct);
        db.Contracts.Add(contract);
        audit.Record(AuditActions.Created, nameof(Contract), contract.Id, after: request, companyId: companyId);
        await db.SaveChangesAsync(ct);
        return contract.ToDto();
    }

    public async Task<ContractDto> UpdateContractAsync(Guid id, ContractUpsert request, CancellationToken ct = default)
    {
        var contract = await LoadContractAsync(id, tracked: true, ct);
        var companyId = await access.EnsureCanAccessClientAsync(contract.ClientCompanyId, write: false, ct);
        await access.EnsureCanManageCompanyAsync(companyId, ct);
        if (request.ClientCompanyId != contract.ClientCompanyId
            || !string.Equals(request.Reference?.Trim(), contract.Reference, StringComparison.Ordinal))
        {
            throw new DomainException("immutable_field", "The client and the reference of a contract cannot change.");
        }

        var before = contract.ToDto();
        contract.SetPeriod(request.StartDate, request.EndDate);
        contract.BillingMode = Mappings.Parse<BillingMode>(request.BillingMode, "billing mode");
        contract.IsActive = request.IsActive;
        await SetPointsOfSaleAsync(contract, companyId, request.PointOfSaleIds, ct);
        audit.Record(AuditActions.Updated, nameof(Contract), id, before, contract.ToDto(), companyId);
        await db.SaveChangesAsync(ct);
        return contract.ToDto();
    }

    private async Task SetPointsOfSaleAsync(Contract contract, Guid companyId, IReadOnlyList<Guid>? pointOfSaleIds, CancellationToken ct)
    {
        var wanted = (pointOfSaleIds ?? []).Distinct().ToList();
        var valid = await (from p in db.PointsOfSale
                           join s in db.Sites on p.SiteId equals s.Id
                           where wanted.Contains(p.Id) && s.CompanyId == companyId
                           select p.Id).ToListAsync(ct);
        if (valid.Count != wanted.Count)
        {
            throw new DomainException("invalid_point_of_sale", "Every point of sale must exist and belong to the contract's company.");
        }

        foreach (var removed in contract.PointsOfSale.Select(p => p.PointOfSaleId).Except(wanted).ToList())
        {
            contract.RemovePointOfSale(removed);
        }

        foreach (var added in wanted)
        {
            contract.AcceptPointOfSale(added);
        }
    }

    // ----- Subsidy rules ---------------------------------------------------------------------------------------------

    public async Task<SubsidyRuleDto> AddSubsidyRuleAsync(Guid contractId, SubsidyRuleCreate request, CancellationToken ct = default)
    {
        var contract = await LoadContractAsync(contractId, tracked: true, ct);
        var companyId = await access.EnsureCanAccessClientAsync(contract.ClientCompanyId, write: false, ct);
        await access.EnsureCanManageCompanyAsync(companyId, ct);
        var rule = contract.AddSubsidyRule(new SubsidyRule(Guid.CreateVersion7(), contract.Id, request.Name,
            Mappings.Parse<SubsidyKind>(request.Kind, "subsidy kind"), request.Value, request.ValidFrom, request.MaxPerMeal,
            request.MaxPerDay, request.MaxMealsPerDay, request.DinerCategory, request.ValidTo));
        db.SubsidyRules.Add(rule);
        audit.Record(AuditActions.Created, nameof(SubsidyRule), rule.Id, after: rule.ToDto(), companyId: companyId);
        await db.SaveChangesAsync(ct);
        return rule.ToDto();
    }

    public async Task<SubsidyRuleDto> CloseSubsidyRuleAsync(Guid ruleId, SubsidyRuleClose request, CancellationToken ct = default)
    {
        var rule = await db.SubsidyRules.SingleOrDefaultAsync(r => r.Id == ruleId, ct) ?? throw new NotFoundException("SubsidyRule", ruleId);
        var clientId = await db.Contracts.Where(c => c.Id == rule.ContractId).Select(c => c.ClientCompanyId).SingleAsync(ct);
        var companyId = await access.EnsureCanAccessClientAsync(clientId, write: false, ct);
        await access.EnsureCanManageCompanyAsync(companyId, ct);
        var before = rule.ToDto();
        rule.Close(request.LastValidDate);
        audit.Record(AuditActions.SubsidyRuleClosed, nameof(SubsidyRule), ruleId, before, rule.ToDto(), companyId);
        await db.SaveChangesAsync(ct);
        return rule.ToDto();
    }

    private async Task<Contract> LoadContractAsync(Guid id, bool tracked, CancellationToken ct)
    {
        var query = db.Contracts.Include(c => c.PointsOfSale).Include(c => c.SubsidyRules).AsQueryable();
        if (!tracked)
        {
            query = query.AsNoTracking();
        }

        return await query.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Contract", id);
    }
}
