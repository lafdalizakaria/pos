using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Audit;
using Newrest.Pos.Domain.Catalog;
using Newrest.Pos.Domain.Clients;
using Newrest.Pos.Domain.Menus;
using Newrest.Pos.Domain.Organization;
using Newrest.Pos.Domain.Sales;
using Newrest.Pos.Domain.Security;

namespace Newrest.Pos.Application.Abstractions;

/// <summary>Unit of work over the central database, implemented by Infrastructure (SQL Server).</summary>
public interface IPosDbContext
{
    DbSet<Company> Companies { get; }
    DbSet<Site> Sites { get; }
    DbSet<PointOfSale> PointsOfSale { get; }
    DbSet<Register> Registers { get; }
    DbSet<Operator> Operators { get; }
    DbSet<UserAccessScope> UserAccessScopes { get; }
    DbSet<Category> Categories { get; }
    DbSet<Article> Articles { get; }
    DbSet<ArticlePhoto> ArticlePhotos { get; }
    DbSet<PriceList> PriceLists { get; }
    DbSet<DailyMenu> DailyMenus { get; }
    DbSet<ClientCompany> ClientCompanies { get; }
    DbSet<Contract> Contracts { get; }
    DbSet<SubsidyRule> SubsidyRules { get; }
    DbSet<Diner> Diners { get; }
    DbSet<Badge> Badges { get; }
    DbSet<Account> Accounts { get; }
    DbSet<AccountMovement> AccountMovements { get; }
    DbSet<Ticket> Tickets { get; }

    DbSet<Domain.Vision.RecognitionLog> RecognitionLogs { get; }

    DbSet<Domain.Vision.VisionModel> VisionModels { get; }

    DbSet<Domain.Vision.SiteVisionSettings> SiteVisionSettings { get; }

    DbSet<Domain.Vision.RegisterVisionStatus> RegisterVisionStatuses { get; }

    DbSet<Domain.Operations.RegisterHeartbeat> RegisterHeartbeats { get; }

    DbSet<Domain.Operations.IntegrityCheck> IntegrityChecks { get; }

    DbSet<Domain.Operations.ArchiveRecord> ArchiveRecords { get; }
    DbSet<CashSession> CashSessions { get; }
    DbSet<ZReport> ZReports { get; }
    DbSet<AuditLog> AuditLogs { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Upper bound for incremental sync: rows with a rowversion below it are committed (SQL Server MIN_ACTIVE_ROWVERSION).</summary>
public interface IRowVersionSource
{
    Task<ulong> GetMinActiveRowVersionAsync(CancellationToken cancellationToken = default);
}
