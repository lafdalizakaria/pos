namespace Newrest.Pos.Client.Data;

/// <summary>
/// Local SQLite store of the register (cache + outbox). The schema and the sync engine are delivered in phase 3;
/// this type fixes the outbox vocabulary shared with the server-side deduplication.
/// </summary>
public enum OutboxStatus
{
    Pending,
    Sent,
    Acknowledged,
    Failed,
}
