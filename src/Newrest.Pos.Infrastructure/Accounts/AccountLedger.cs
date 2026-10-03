using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newrest.Pos.Application.Accounts;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Infrastructure.Persistence;

namespace Newrest.Pos.Infrastructure.Accounts;

/// <summary>
/// SQL Server ledger. Concurrency: the account row is locked with <c>UPDLOCK, ROWLOCK</c> for the duration of a
/// short transaction (balance check + insert + cache update), so concurrent debits on the same account are
/// serialised while other accounts are unaffected. The rowversion on the account is a second safety net.
/// Idempotency: unique index on <c>IdempotencyKey</c>; a replay returns the original movement.
/// </summary>
public sealed partial class AccountLedger(PosDbContext db, TimeProvider clock, ILogger<AccountLedger> logger) : IAccountLedger
{
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    public Task<LedgerPostResult> PostAsync(Guid accountId, MovementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteLockedAsync(accountId, request.IdempotencyKey, request.Amount,
            (account, _) => Task.FromResult(account.Post(request)), cancellationToken);
    }

    public async Task<LedgerPostResult> ReverseAsync(Guid movementId, Guid idempotencyKey, string performedBy, string reason,
        CancellationToken cancellationToken = default)
    {
        var original = await db.AccountMovements.AsNoTracking().SingleOrDefaultAsync(m => m.Id == movementId, cancellationToken)
                       ?? throw new Domain.Common.DomainException("movement_not_found", $"Movement {movementId} was not found.");

        return await ExecuteLockedAsync(original.AccountId, idempotencyKey, -original.Amount, async (account, ct) =>
        {
            if (await db.AccountMovements.AnyAsync(m => m.ReversesMovementId == movementId, ct))
            {
                throw new Domain.Common.DomainException("already_reversed", $"Movement {movementId} was already reversed.");
            }

            return account.Reverse(original, idempotencyKey, clock.GetUtcNow(), performedBy, reason);
        }, cancellationToken);
    }

    public async Task<AccountBalance> GetBalanceAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await db.Accounts.AsNoTracking()
                          .Where(a => a.Id == accountId)
                          .Select(a => new { a.CachedBalance, a.OverdraftLimit })
                          .SingleOrDefaultAsync(cancellationToken)
                      ?? throw new AccountNotFoundException(accountId);
        var ledger = await db.AccountMovements.Where(m => m.AccountId == accountId).SumAsync(m => (decimal?)m.Amount, cancellationToken) ?? 0m;
        return new AccountBalance(accountId, ledger, account.CachedBalance, account.OverdraftLimit);
    }

    public async Task<IReadOnlyList<AccountBalance>> FindCacheDriftAsync(CancellationToken cancellationToken = default)
    {
        var rows = await db.Accounts.AsNoTracking()
            .Select(a => new
            {
                a.Id,
                a.CachedBalance,
                a.OverdraftLimit,
                Ledger = db.AccountMovements.Where(m => m.AccountId == a.Id).Sum(m => (decimal?)m.Amount) ?? 0m,
            })
            .Where(x => x.Ledger != x.CachedBalance)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new AccountBalance(r.Id, r.Ledger, r.CachedBalance, r.OverdraftLimit))];
    }

    private async Task<LedgerPostResult> ExecuteLockedAsync(Guid accountId, Guid idempotencyKey, decimal expectedAmount,
        Func<Account, CancellationToken, Task<AccountMovement>> createMovement, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async ct =>
            {
                db.ChangeTracker.Clear();
                if (await FindExistingAsync(idempotencyKey, accountId, expectedAmount, ct) is { } replay)
                {
                    return replay;
                }

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var account = await db.Accounts
                                  .FromSql($"SELECT * FROM [pos].[Accounts] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {accountId}")
                                  .SingleOrDefaultAsync(ct)
                              ?? throw new AccountNotFoundException(accountId);

                // A concurrent request with the same key may have committed while we waited for the lock.
                if (await FindExistingAsync(idempotencyKey, accountId, expectedAmount, ct) is { } concurrentReplay)
                {
                    return concurrentReplay;
                }

                var movement = await createMovement(account, ct);
                db.AccountMovements.Add(movement);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                if (movement.ExceededOverdraft)
                {
                    LogOverdraftExceeded(logger, accountId, movement.Id, account.CachedBalance);
                }

                return ToResult(movement, wasDuplicate: false);
            }, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: UniqueConstraintViolation or UniqueIndexViolation } sql
                                           && sql.Message.Contains("IdempotencyKey", StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            return await FindExistingAsync(idempotencyKey, accountId, expectedAmount, cancellationToken)
                   ?? throw new IdempotencyConflictException(idempotencyKey);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: UniqueConstraintViolation or UniqueIndexViolation } sql
                                           && sql.Message.Contains("ReversesMovementId", StringComparison.Ordinal))
        {
            throw new Domain.Common.DomainException("already_reversed", "The movement was already reversed.");
        }
    }

    private async Task<LedgerPostResult?> FindExistingAsync(Guid idempotencyKey, Guid accountId, decimal expectedAmount, CancellationToken ct)
    {
        var existing = await db.AccountMovements.AsNoTracking().SingleOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey, ct);
        if (existing is null)
        {
            return null;
        }

        if (existing.AccountId != accountId || existing.Amount != expectedAmount)
        {
            throw new IdempotencyConflictException(idempotencyKey);
        }

        return ToResult(existing, wasDuplicate: true);
    }

    private static LedgerPostResult ToResult(AccountMovement m, bool wasDuplicate) =>
        new(m.Id, m.AccountId, m.Amount, m.BalanceAfter, wasDuplicate, m.ExceededOverdraft);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offline replay pushed account {AccountId} below its overdraft limit (movement {MovementId}, balance {Balance})")]
    private static partial void LogOverdraftExceeded(ILogger logger, Guid accountId, Guid movementId, decimal balance);
}
