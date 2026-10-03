namespace Newrest.Pos.Contracts;

/// <summary>Shared API constants (server, register and vision). DTOs are added from phase 2 on.</summary>
public static class ApiVersion
{
    public const string V1 = "v1";
    public const string BasePath = "/api/v1";

    /// <summary>Header carrying the client-generated idempotency key on every write from a register.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";
}
