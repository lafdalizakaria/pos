using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Security;

/// <summary>
/// Grants a back-office user (identified by UPN) access to a whole company (<see cref="SiteId"/> null) or to one site.
/// Roles come from Entra ID app roles; scopes are managed by administrators in the back-office.
/// Global administrators need no scope.
/// </summary>
public sealed class UserAccessScope : Entity
{
    private UserAccessScope()
    {
    }

    public UserAccessScope(Guid id, string userName, Guid companyId, Guid? siteId) : base(id)
    {
        UserName = NormalizeUserName(userName);
        CompanyId = Guard.NotEmpty(companyId, nameof(companyId));
        SiteId = siteId == Guid.Empty ? null : siteId;
    }

    public string UserName { get; private set; } = null!;
    public Guid CompanyId { get; private set; }
    public Guid? SiteId { get; private set; }

    public static string NormalizeUserName(string userName) => Guard.NotBlank(userName, nameof(userName), 200).ToLowerInvariant();
}
