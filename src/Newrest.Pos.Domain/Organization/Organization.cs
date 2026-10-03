using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Organization;

/// <summary>Newrest legal entity (e.g. NFMS, NMS, NMI).</summary>
public sealed class Company : ReferenceEntity
{
    private Company()
    {
    }

    public Company(Guid id, string code, string name, string legalName) : base(id)
    {
        Code = Guard.NotBlank(code, nameof(code), 16).ToUpperInvariant();
        Name = Guard.NotBlank(name, nameof(name));
        LegalName = Guard.NotBlank(legalName, nameof(legalName));
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string LegalName { get; private set; } = null!;

    /// <summary>Identifiant Commun de l'Entreprise (15 digits).</summary>
    public string? Ice { get; set; }

    /// <summary>Identifiant Fiscal.</summary>
    public string? TaxId { get; set; }

    /// <summary>Registre de Commerce.</summary>
    public string? TradeRegister { get; set; }

    public string? Address { get; set; }
    public bool IsActive { get; set; }
}

public sealed class Site : ReferenceEntity
{
    private Site()
    {
    }

    public Site(Guid id, Guid companyId, string code, string name, string city) : base(id)
    {
        CompanyId = Guard.NotEmpty(companyId, nameof(companyId));
        Code = Guard.NotBlank(code, nameof(code), 32).ToUpperInvariant();
        Name = Guard.NotBlank(name, nameof(name));
        City = Guard.NotBlank(city, nameof(city), 100);
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string City { get; private set; } = null!;
    public string? Address { get; set; }

    /// <summary>IANA time zone used to compute business dates (default Africa/Casablanca).</summary>
    public string TimeZone { get; set; } = "Africa/Casablanca";

    public bool IsActive { get; set; }
}

public enum PointOfSaleType
{
    Self,
    Cafeteria,
    Snack,
    Kiosk,
}

public sealed class PointOfSale : ReferenceEntity
{
    private PointOfSale()
    {
    }

    public PointOfSale(Guid id, Guid siteId, string code, string name, PointOfSaleType type) : base(id)
    {
        SiteId = Guard.NotEmpty(siteId, nameof(siteId));
        Code = Guard.NotBlank(code, nameof(code), 32).ToUpperInvariant();
        Name = Guard.NotBlank(name, nameof(name));
        Type = type;
        IsActive = true;
    }

    public Guid SiteId { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public PointOfSaleType Type { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>Physical register (PC + camera). Owns its gapless ticket sequence.</summary>
public sealed class Register : ReferenceEntity
{
    private Register()
    {
    }

    public Register(Guid id, Guid pointOfSaleId, string code, string name, string ticketPrefix) : base(id)
    {
        PointOfSaleId = Guard.NotEmpty(pointOfSaleId, nameof(pointOfSaleId));
        Code = Guard.NotBlank(code, nameof(code), 32).ToUpperInvariant();
        Name = Guard.NotBlank(name, nameof(name));
        TicketPrefix = Sales.TicketNumber.ValidatePrefix(ticketPrefix);
        IsActive = true;
    }

    public Guid PointOfSaleId { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;

    /// <summary>Unique, immutable prefix of every ticket number issued by this register.</summary>
    public string TicketPrefix { get; private set; } = null!;

    /// <summary>Highest ticket sequence received by the server (sync watermark, not the source of truth).</summary>
    public long LastSyncedTicketSequence { get; set; }

    /// <summary>Hash of the last ticket received by the server, used to check the chain on ingestion.</summary>
    public string? LastSyncedTicketHash { get; set; }

    /// <summary>Last Z report number issued by this register.</summary>
    public int LastZNumber { get; set; }

    /// <summary>Hash of the device API key (registration happens in phase 2).</summary>
    public string? DeviceKeyHash { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
    public bool IsActive { get; set; }
}
