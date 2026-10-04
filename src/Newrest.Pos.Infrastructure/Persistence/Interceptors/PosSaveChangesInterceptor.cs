using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Catalog;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Sets technical timestamps and refuses any update or delete of an <see cref="IImmutableRecord"/>
/// (tickets, ledger movements, Z reports, audit). Corrections must go through credit notes / reversals.
/// </summary>
public sealed class PosSaveChangesInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        TouchArticlesOfChangedPhotos(context);
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is IImmutableRecord && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new ImmutableRecordException(entry.Entity.GetType().Name);
            }

            if (entry.State != EntityState.Added && entry.State != EntityState.Modified)
            {
                continue;
            }

            switch (entry.Entity)
            {
                case ReferenceEntity reference:
                    if (entry.State == EntityState.Added)
                    {
                        reference.CreatedAt = now;
                    }

                    reference.UpdatedAt = now;
                    break;
                case AccountMovement movement when entry.State == EntityState.Added:
                    movement.RecordedAt = now;
                    break;
                case Ticket ticket when entry.State == EntityState.Added:
                    ticket.ReceivedAt = now;
                    break;
            }
        }
    }

    /// <summary>A photo added or removed changes its (tracked) article, so the article's row version moves and registers re-sync it.</summary>
    private static void TouchArticlesOfChangedPhotos(DbContext context)
    {
        var articleIds = context.ChangeTracker.Entries<ArticlePhoto>()
            .Where(e => e.State is EntityState.Added or EntityState.Deleted).Select(e => e.Entity.ArticleId).ToHashSet();
        foreach (var article in context.ChangeTracker.Entries<Article>().Where(e => e.State == EntityState.Unchanged && articleIds.Contains(e.Entity.Id)))
        {
            article.State = EntityState.Modified;
        }
    }
}

public sealed class ImmutableRecordException(string entityType)
    : DomainException("immutable_record", $"{entityType} records are immutable and cannot be updated or deleted.");
