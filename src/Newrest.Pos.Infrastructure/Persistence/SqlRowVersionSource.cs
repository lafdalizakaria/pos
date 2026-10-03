using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Application.Abstractions;

namespace Newrest.Pos.Infrastructure.Persistence;

/// <summary>
/// MIN_ACTIVE_ROWVERSION(): every row with a lower rowversion is committed, so an incremental sync bounded by it
/// never skips a row written by a transaction still in flight.
/// </summary>
public sealed class SqlRowVersionSource(PosDbContext db) : IRowVersionSource
{
    public async Task<ulong> GetMinActiveRowVersionAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await db.Database.SqlQuery<byte[]>($"SELECT MIN_ACTIVE_ROWVERSION() AS [Value]").SingleAsync(cancellationToken);
        var value = 0UL;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }
}
