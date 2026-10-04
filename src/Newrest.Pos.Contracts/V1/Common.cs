namespace Newrest.Pos.Contracts.V1;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

/// <summary>Roles carried by Entra ID app roles (back-office users) or issued to registers.</summary>
public static class PosRoles
{
    /// <summary>Global administrator: every company and site, master catalogue, user scopes.</summary>
    public const string Admin = "Pos.Admin";

    /// <summary>Site / company manager: organisation, prices, menus, clients, diners, badges, top-ups within scope.</summary>
    public const string Manager = "Pos.Manager";

    /// <summary>Finance: balance corrections, reversals, refunds, audit, within scope.</summary>
    public const string Accountant = "Pos.Accountant";

    /// <summary>Read-only access within scope.</summary>
    public const string Viewer = "Pos.Viewer";

    /// <summary>A registered register (device credential), never a person.</summary>
    public const string Register = "Pos.Register";

    public static readonly string[] BackOffice = [Admin, Manager, Accountant, Viewer];
}

public sealed record CurrentUserDto(string Name, IReadOnlyList<string> Roles, bool IsGlobal, IReadOnlyList<AccessScopeDto> Scopes);
