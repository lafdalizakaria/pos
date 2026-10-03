namespace Newrest.Pos.Domain.Common;

/// <summary>
/// Base class for every persisted entity. Identifiers are time-ordered GUIDs (v7) so that
/// they can be generated offline by registers and merged on the server without collision.
/// </summary>
public abstract class Entity
{
    protected Entity() => Id = Guid.CreateVersion7();

    protected Entity(Guid id) => Id = id == Guid.Empty ? Guid.CreateVersion7() : id;

    public Guid Id { get; protected set; }
}

/// <summary>
/// Master/reference data synchronised down to registers. <see cref="RowVersion"/> is a SQL Server
/// rowversion used both for optimistic concurrency and as the incremental sync cursor.
/// </summary>
public abstract class ReferenceEntity : Entity
{
    protected ReferenceEntity()
    {
    }

    protected ReferenceEntity(Guid id) : base(id)
    {
    }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Marker for records that must never be updated nor deleted once written
/// (fiscal tickets, ledger movements, Z reports, audit). Enforced by an EF Core interceptor.
/// </summary>
public interface IImmutableRecord;
