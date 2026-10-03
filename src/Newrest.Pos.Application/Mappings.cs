using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Audit;
using Newrest.Pos.Domain.Catalog;
using Newrest.Pos.Domain.Clients;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Organization;
using Newrest.Pos.Domain.Security;

namespace Newrest.Pos.Application;

internal static class Mappings
{
    public static CompanyDto ToDto(this Company c) =>
        new(c.Id, c.Code, c.Name, c.LegalName, c.Ice, c.TaxId, c.TradeRegister, c.Address, c.IsActive);

    public static SiteDto ToDto(this Site s) => new(s.Id, s.CompanyId, s.Code, s.Name, s.City, s.Address, s.TimeZone, s.IsActive);

    public static PointOfSaleDto ToDto(this PointOfSale p) => new(p.Id, p.SiteId, p.Code, p.Name, p.Type.ToString(), p.IsActive);

    public static RegisterDto ToDto(this Register r) => new(r.Id, r.PointOfSaleId, r.Code, r.Name, r.TicketPrefix, r.IsActive,
        r.DeviceKeyHash is not null, r.DeviceKeyIssuedAt, r.LastSeenAt, r.LastSyncedTicketSequence);

    public static OperatorDto ToDto(this Operator o, DateTimeOffset now) => new(o.Id, o.CompanyId, o.SiteId, o.Code, o.FirstName,
        o.LastName, RoleNames(o.Roles), o.IsActive, o.IsLockedOut(now));

    public static AccessScopeDto ToDto(this UserAccessScope s) => new(s.Id, s.UserName, s.CompanyId, s.SiteId);

    public static CategoryDto ToDto(this Category c) => new(c.Id, c.Code, c.Name, c.DisplayOrder, c.ColorHex, c.IsActive);

    public static ArticleDto ToDto(this Article a) => new(a.Id, a.Code, a.Name, a.ReceiptLabel, a.CategoryId, a.BasePrice, a.VatRate,
        a.VisualDescription, a.IsSubsidizable, a.IsActive,
        [.. a.Photos.OrderBy(p => p.DisplayOrder).Select(p => new ArticlePhotoDto(p.Id, p.StoragePath, p.Caption, p.DisplayOrder))]);

    public static ClientCompanyDto ToDto(this ClientCompany c) => new(c.Id, c.CompanyId, c.Code, c.Name, c.Ice, c.BillingAddress, c.IsActive);

    public static SubsidyRuleDto ToDto(this SubsidyRule r) => new(r.Id, r.ContractId, r.Name, r.Kind.ToString(), r.Value, r.MaxPerMeal,
        r.MaxPerDay, r.MaxMealsPerDay, r.DinerCategory, r.ValidFrom, r.ValidTo);

    public static ContractDto ToDto(this Contract c) => new(c.Id, c.ClientCompanyId, c.Reference, c.StartDate, c.EndDate,
        c.BillingMode.ToString(), c.IsActive, [.. c.PointsOfSale.Select(p => p.PointOfSaleId)],
        [.. c.SubsidyRules.OrderByDescending(r => r.ValidFrom).Select(r => r.ToDto())]);

    public static BadgeDto ToDto(this Badge b) => new(b.Id, b.DinerId, b.Number, b.Status.ToString(), b.IssuedAt, b.DeactivatedAt, b.ReplacedByBadgeId);

    public static AccountSummaryDto ToSummary(this Account a) =>
        new(a.Id, a.ContractId, a.Type.ToString(), a.CachedBalance, a.OverdraftLimit, a.AvailableToSpend, a.IsActive);

    public static MovementDto ToDto(this AccountMovement m, bool isReversed) => new(m.Id, m.AccountId, m.Type.ToString(), m.Amount,
        m.OccurredAt, m.BalanceAfter, m.PaymentMethod?.ToString(), m.Comment, m.PerformedBy, m.TicketId, m.RegisterId,
        m.ReversesMovementId, isReversed, m.IsOfflineReplay, m.ExceededOverdraft);

    public static AuditEntryDto ToDto(this AuditLog a) => new(a.Id, a.OccurredAt, a.Actor, a.Action, a.EntityType, a.EntityId,
        a.BeforeJson, a.AfterJson, a.CompanyId, a.SiteId, a.CorrelationId);

    public static IReadOnlyList<string> RoleNames(OperatorRoles roles) =>
        [.. Enum.GetValues<OperatorRoles>().Where(r => r != OperatorRoles.None && roles.HasFlag(r)).Select(r => r.ToString())];

    public static OperatorRoles ParseRoles(IReadOnlyList<string>? roles)
    {
        var result = OperatorRoles.None;
        foreach (var role in roles ?? [])
        {
            result |= Parse<OperatorRoles>(role, "role");
        }

        return result;
    }

    /// <summary>Case-insensitive enum parsing that rejects numeric values and unknown names with a domain error.</summary>
    public static T Parse<T>(string? value, string name)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value) || char.IsAsciiDigit(value.Trim()[0]) || value.Contains(',', StringComparison.Ordinal)
            || !Enum.TryParse<T>(value.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new DomainException("invalid_value",
                $"Invalid {name} '{value}'. Expected one of: {string.Join(", ", Enum.GetNames<T>())}.");
        }

        return parsed;
    }

    public static string? Clean(this string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
