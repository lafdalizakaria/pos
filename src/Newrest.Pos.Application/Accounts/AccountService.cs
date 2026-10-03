using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Auditing;
using Newrest.Pos.Application.Security;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Application.Accounts;

/// <summary>
/// Back-office account operations. Every money movement goes through <see cref="IAccountLedger"/> (locked, idempotent)
/// and is audited in the same transaction.
/// Top-up: managers and finance. Correction, reversal, refund (payout): finance only.
/// </summary>
public sealed class AccountService(IPosDbContext db, IAccountLedger ledger, AccessControl access, AuditTrail audit, TimeProvider clock)
{
    public async Task<PagedResult<AccountDto>> ListAsync(Guid clientCompanyId, string? search = null, int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        await access.EnsureCanAccessClientAsync(clientCompanyId, write: false, ct);
        (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, 200));
        var query = from a in db.Accounts.AsNoTracking()
                    join d in db.Diners.AsNoTracking() on a.DinerId equals d.Id
                    where d.ClientCompanyId == clientCompanyId
                    select new { Account = a, Diner = d };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Diner.EmployeeNumber.Contains(term) || x.Diner.LastName.Contains(term) || x.Diner.FirstName.Contains(term));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.Diner.LastName).ThenBy(x => x.Diner.FirstName)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<AccountDto>([.. rows.Select(r => ToDto(r.Account, r.Diner))], total, page, pageSize);
    }

    public async Task<AccountDto> GetAsync(Guid accountId, CancellationToken ct = default)
    {
        var (account, diner, _) = await LoadAsync(accountId, write: false, ct);
        return ToDto(account, diner);
    }

    public async Task<AccountDto> UpdateAsync(Guid accountId, AccountUpdate request, CancellationToken ct = default)
    {
        var (_, diner, companyId) = await LoadAsync(accountId, write: true, ct);
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct);
        var before = new { Type = account.Type.ToString(), account.OverdraftLimit, account.IsActive };
        account.Type = Mappings.Parse<AccountType>(request.Type, "account type");
        account.SetOverdraftLimit(request.OverdraftLimit);
        account.IsActive = request.IsActive;
        audit.Record(AuditActions.Updated, nameof(Account), accountId, before, request, companyId);
        await db.SaveChangesAsync(ct);
        return ToDto(account, diner);
    }

    public async Task<PagedResult<MovementDto>> ListMovementsAsync(Guid accountId, DateOnly? from = null, DateOnly? to = null, int page = 1,
        int pageSize = 50, CancellationToken ct = default)
    {
        await LoadAsync(accountId, write: false, ct);
        (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, 200));
        var query = db.AccountMovements.AsNoTracking().Where(m => m.AccountId == accountId);
        if (from is { } f)
        {
            var start = new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(m => m.OccurredAt >= start);
        }

        if (to is { } t)
        {
            var end = new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(m => m.OccurredAt < end);
        }

        var total = await query.CountAsync(ct);
        var movements = await query.OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = movements.Select(m => m.Id).ToList();
        var reversed = (await db.AccountMovements.AsNoTracking()
            .Where(m => m.ReversesMovementId != null && ids.Contains(m.ReversesMovementId.Value))
            .Select(m => m.ReversesMovementId!.Value).ToListAsync(ct)).ToHashSet();
        return new PagedResult<MovementDto>([.. movements.Select(m => m.ToDto(reversed.Contains(m.Id)))], total, page, pageSize);
    }

    public async Task<LedgerResultDto> TopUpAsync(Guid accountId, TopUpRequest request, CancellationToken ct = default)
    {
        access.RequireAnyRole(AccessControl.MoneyIn);
        var (_, _, companyId) = await LoadAsync(accountId, write: false, ct);
        var method = ParseCollectionMethod(request.PaymentMethod);
        var movement = new MovementRequest(MovementType.TopUp, request.Amount, request.IdempotencyKey, clock.GetUtcNow())
        {
            PaymentMethod = method,
            PerformedBy = access.User.Name,
            Comment = request.Comment.Clean(),
        };
        var result = await ledger.PostAsync(accountId, movement,
            audit.Create(AuditActions.AccountTopUp, nameof(Account), accountId, after: request, companyId: companyId), ct);
        return ToDto(result);
    }

    /// <summary>Manual correction (signed). It never pushes the balance below the overdraft limit.</summary>
    public async Task<LedgerResultDto> CorrectAsync(Guid accountId, CorrectionRequest request, CancellationToken ct = default)
    {
        access.RequireAnyRole(AccessControl.Finance);
        var (_, _, companyId) = await LoadAsync(accountId, write: false, ct);
        var reason = Guard.NotBlank(request.Reason, nameof(request.Reason), 500);
        var movement = new MovementRequest(MovementType.Correction, request.Amount, request.IdempotencyKey, clock.GetUtcNow())
        {
            PerformedBy = access.User.Name,
            Comment = reason,
        };
        var result = await ledger.PostAsync(accountId, movement,
            audit.Create(AuditActions.AccountCorrection, nameof(Account), accountId, after: request, companyId: companyId), ct);
        return ToDto(result);
    }

    public async Task<LedgerResultDto> ReverseAsync(Guid accountId, Guid movementId, ReversalRequest request, CancellationToken ct = default)
    {
        access.RequireAnyRole(AccessControl.Finance);
        var (_, _, companyId) = await LoadAsync(accountId, write: false, ct);
        var original = await db.AccountMovements.AsNoTracking().SingleOrDefaultAsync(m => m.Id == movementId && m.AccountId == accountId, ct)
                       ?? throw new NotFoundException("AccountMovement", movementId);
        if (original.Type == MovementType.Consumption)
        {
            throw new DomainException("use_credit_note", "A consumption is cancelled by a credit note on its ticket, not by a reversal.");
        }

        var reason = Guard.NotBlank(request.Reason, nameof(request.Reason), 500);
        var result = await ledger.ReverseAsync(movementId, request.IdempotencyKey, access.User.Name, reason,
            audit.Create(AuditActions.AccountReversal, nameof(Account), accountId,
                before: new { MovementId = movementId, original.Type, original.Amount }, after: request, companyId: companyId), ct);
        return ToDto(result);
    }

    /// <summary>Gives (part of) the remaining balance back to the diner.</summary>
    public async Task<LedgerResultDto> RefundAsync(Guid accountId, RefundRequest request, CancellationToken ct = default)
    {
        access.RequireAnyRole(AccessControl.Finance);
        var (_, _, companyId) = await LoadAsync(accountId, write: false, ct);
        if (request.Amount <= 0)
        {
            throw new DomainException("invalid_amount", "The refunded amount must be positive.");
        }

        var balance = await ledger.GetBalanceAsync(accountId, ct);
        if (request.Amount > balance.LedgerBalance)
        {
            throw new DomainException("refund_exceeds_balance", "Only a positive balance can be refunded.");
        }

        var movement = new MovementRequest(MovementType.Refund, -request.Amount, request.IdempotencyKey, clock.GetUtcNow())
        {
            PaymentMethod = ParseCollectionMethod(request.PaymentMethod),
            PerformedBy = access.User.Name,
            Comment = Guard.NotBlank(request.Reason, nameof(request.Reason), 500),
        };
        var result = await ledger.PostAsync(accountId, movement,
            audit.Create(AuditActions.AccountRefund, nameof(Account), accountId, after: request, companyId: companyId), ct);
        return ToDto(result);
    }

    private static PaymentMethod ParseCollectionMethod(string? value)
    {
        var method = Mappings.Parse<PaymentMethod>(value, "payment method");
        return method == PaymentMethod.Account
            ? throw new DomainException("invalid_value", "An account cannot be topped up or refunded from itself.")
            : method;
    }

    private async Task<(Account Account, Domain.Clients.Diner Diner, Guid CompanyId)> LoadAsync(Guid accountId, bool write, CancellationToken ct)
    {
        var row = await (from a in db.Accounts.AsNoTracking()
                         join d in db.Diners.AsNoTracking() on a.DinerId equals d.Id
                         where a.Id == accountId
                         select new { Account = a, Diner = d }).SingleOrDefaultAsync(ct)
                  ?? throw new NotFoundException("Account", accountId);
        var companyId = await access.EnsureCanAccessClientAsync(row.Diner.ClientCompanyId, write, ct);
        return (row.Account, row.Diner, companyId);
    }

    private static AccountDto ToDto(Account a, Domain.Clients.Diner d) => new(a.Id, d.Id, d.DisplayName, d.EmployeeNumber, d.ClientCompanyId,
        a.ContractId, a.Type.ToString(), a.CachedBalance, a.OverdraftLimit, a.AvailableToSpend, a.IsActive);

    private static LedgerResultDto ToDto(LedgerPostResult r) => new(r.MovementId, r.AccountId, r.Amount, r.BalanceAfter, r.WasDuplicate);
}
