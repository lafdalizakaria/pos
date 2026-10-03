using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Clients;

/// <summary>B2B customer whose employees (diners) eat at Newrest points of sale.</summary>
public sealed class ClientCompany : ReferenceEntity
{
    private ClientCompany()
    {
    }

    public ClientCompany(Guid id, Guid companyId, string code, string name) : base(id)
    {
        CompanyId = Guard.NotEmpty(companyId, nameof(companyId));
        Code = Guard.NotBlank(code, nameof(code), 32).ToUpperInvariant();
        Name = Guard.NotBlank(name, nameof(name));
        IsActive = true;
    }

    /// <summary>Newrest company that holds the contract.</summary>
    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = null!;
    public string Name { get; set; } = null!;
    public string? Ice { get; set; }
    public string? BillingAddress { get; set; }
    public bool IsActive { get; set; }
}

public enum BillingMode
{
    /// <summary>Monthly invoice to the employer (subsidies + postpaid consumption).</summary>
    EmployerInvoice,

    /// <summary>Postpaid consumption withheld on salary; employer receives the export.</summary>
    PayrollDeduction,
}

public sealed class Contract : ReferenceEntity
{
    private readonly List<ContractPointOfSale> _pointsOfSale = [];
    private readonly List<SubsidyRule> _subsidyRules = [];

    private Contract()
    {
    }

    public Contract(Guid id, Guid clientCompanyId, string reference, DateOnly startDate, DateOnly? endDate = null) : base(id)
    {
        ClientCompanyId = Guard.NotEmpty(clientCompanyId, nameof(clientCompanyId));
        Reference = Guard.NotBlank(reference, nameof(reference), 64);
        if (endDate is { } end && end < startDate)
        {
            throw new DomainException("invalid_validity", "End date must be on or after start date.");
        }

        StartDate = startDate;
        EndDate = endDate;
        IsActive = true;
    }

    public Guid ClientCompanyId { get; private set; }
    public string Reference { get; private set; } = null!;
    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
    public BillingMode BillingMode { get; set; } = BillingMode.EmployerInvoice;
    public bool IsActive { get; set; }

    public IReadOnlyCollection<ContractPointOfSale> PointsOfSale => _pointsOfSale;
    public IReadOnlyCollection<SubsidyRule> SubsidyRules => _subsidyRules;

    public bool IsActiveOn(DateOnly date) => IsActive && date >= StartDate && (EndDate is null || date <= EndDate);

    public void SetPeriod(DateOnly startDate, DateOnly? endDate)
    {
        if (endDate is { } end && end < startDate)
        {
            throw new DomainException("invalid_validity", "End date must be on or after start date.");
        }

        StartDate = startDate;
        EndDate = endDate;
    }

    public bool AcceptsPointOfSale(Guid pointOfSaleId) => _pointsOfSale.Any(p => p.PointOfSaleId == pointOfSaleId);

    public void AcceptPointOfSale(Guid pointOfSaleId)
    {
        Guard.NotEmpty(pointOfSaleId, nameof(pointOfSaleId));
        if (!AcceptsPointOfSale(pointOfSaleId))
        {
            _pointsOfSale.Add(new ContractPointOfSale(Id, pointOfSaleId));
        }
    }

    public void RemovePointOfSale(Guid pointOfSaleId) => _pointsOfSale.RemoveAll(p => p.PointOfSaleId == pointOfSaleId);

    public SubsidyRule AddSubsidyRule(SubsidyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.ContractId != Id)
        {
            throw new DomainException("invalid_rule", "The subsidy rule belongs to another contract.");
        }

        _subsidyRules.Add(rule);
        return rule;
    }
}

public sealed class ContractPointOfSale
{
    private ContractPointOfSale()
    {
    }

    internal ContractPointOfSale(Guid contractId, Guid pointOfSaleId)
    {
        ContractId = contractId;
        PointOfSaleId = pointOfSaleId;
    }

    public Guid ContractId { get; private set; }
    public Guid PointOfSaleId { get; private set; }
}

/// <summary>Employee of a client company.</summary>
public sealed class Diner : ReferenceEntity
{
    private Diner()
    {
    }

    public Diner(Guid id, Guid clientCompanyId, string employeeNumber, string firstName, string lastName) : base(id)
    {
        ClientCompanyId = Guard.NotEmpty(clientCompanyId, nameof(clientCompanyId));
        EmployeeNumber = Guard.NotBlank(employeeNumber, nameof(employeeNumber), 32);
        FirstName = Guard.NotBlank(firstName, nameof(firstName), 100);
        LastName = Guard.NotBlank(lastName, nameof(lastName), 100);
        IsActive = true;
    }

    public Guid ClientCompanyId { get; private set; }

    /// <summary>Matricule. Unique within a client company.</summary>
    public string EmployeeNumber { get; private set; } = null!;

    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;

    /// <summary>Optional diner category used to select subsidy rules (e.g. "CADRE", "OPERATEUR").</summary>
    public string? Category { get; set; }

    public bool IsActive { get; set; }

    public string DisplayName => $"{FirstName} {LastName}";
}

public enum BadgeStatus
{
    Active,
    Lost,
    Blocked,
    Returned,
}

/// <summary>Physical badge. The balance belongs to the account, never to the badge.</summary>
public sealed class Badge : ReferenceEntity
{
    private Badge()
    {
    }

    public Badge(Guid id, Guid dinerId, string number, DateTimeOffset issuedAt) : base(id)
    {
        DinerId = Guard.NotEmpty(dinerId, nameof(dinerId));
        Number = NormalizeNumber(number);
        IssuedAt = issuedAt;
        Status = BadgeStatus.Active;
    }

    public Guid DinerId { get; private set; }
    public string Number { get; private set; } = null!;
    public BadgeStatus Status { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset? DeactivatedAt { get; private set; }
    public Guid? ReplacedByBadgeId { get; private set; }

    public bool IsUsable => Status == BadgeStatus.Active;

    public static string NormalizeNumber(string number) => Guard.NotBlank(number, nameof(number), 64).ToUpperInvariant();

    public void Block(DateTimeOffset now) => Deactivate(BadgeStatus.Blocked, now);

    public void Unblock()
    {
        if (Status != BadgeStatus.Blocked)
        {
            throw new DomainException("badge_not_blocked", "Only a blocked badge can be unblocked.");
        }

        Status = BadgeStatus.Active;
        DeactivatedAt = null;
    }

    /// <summary>Declares the badge lost and issues its replacement for the same diner (and therefore the same account).</summary>
    public Badge ReplaceAsLost(string newNumber, DateTimeOffset now)
    {
        var replacement = new Badge(Guid.CreateVersion7(), DinerId, newNumber, now);
        if (replacement.Number == Number)
        {
            throw new DomainException("same_badge_number", "The replacement badge must have a different number.");
        }

        Deactivate(BadgeStatus.Lost, now);
        ReplacedByBadgeId = replacement.Id;
        return replacement;
    }

    private void Deactivate(BadgeStatus status, DateTimeOffset now)
    {
        if (Status != BadgeStatus.Active)
        {
            throw new DomainException("badge_not_active", $"Badge is already {Status}.");
        }

        Status = status;
        DeactivatedAt = now;
    }
}
