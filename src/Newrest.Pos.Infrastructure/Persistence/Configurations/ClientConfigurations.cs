using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Clients;
using Newrest.Pos.Domain.Organization;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Infrastructure.Persistence.Configurations;

internal sealed class ClientCompanyConfiguration : IEntityTypeConfiguration<ClientCompany>
{
    public void Configure(EntityTypeBuilder<ClientCompany> b)
    {
        b.ToTable("ClientCompanies");
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Ice).HasMaxLength(15);
        b.Property(x => x.BillingAddress).HasMaxLength(500);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
    }
}

internal sealed class ContractConfiguration : IEntityTypeConfiguration<Contract>
{
    public void Configure(EntityTypeBuilder<Contract> b)
    {
        b.Property(x => x.Reference).HasMaxLength(64);
        b.HasOne<ClientCompany>().WithMany().HasForeignKey(x => x.ClientCompanyId);
        b.HasMany(x => x.PointsOfSale).WithOne().HasForeignKey(x => x.ContractId).CascadeChildren();
        b.HasMany(x => x.SubsidyRules).WithOne().HasForeignKey(x => x.ContractId);
        b.HasIndex(x => x.Reference).IsUnique();
    }
}

internal sealed class ContractPointOfSaleConfiguration : IEntityTypeConfiguration<ContractPointOfSale>
{
    public void Configure(EntityTypeBuilder<ContractPointOfSale> b)
    {
        b.ToTable("ContractPointsOfSale");
        b.HasKey(x => new { x.ContractId, x.PointOfSaleId });
        b.HasOne<PointOfSale>().WithMany().HasForeignKey(x => x.PointOfSaleId);
    }
}

internal sealed class SubsidyRuleConfiguration : IEntityTypeConfiguration<SubsidyRule>
{
    public void Configure(EntityTypeBuilder<SubsidyRule> b)
    {
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Value).IsRate();
        b.Property(x => x.DinerCategory).HasMaxLength(32);
        b.HasIndex(x => new { x.ContractId, x.ValidFrom });
    }
}

internal sealed class DinerConfiguration : IEntityTypeConfiguration<Diner>
{
    public void Configure(EntityTypeBuilder<Diner> b)
    {
        b.Property(x => x.EmployeeNumber).HasMaxLength(32);
        b.Property(x => x.FirstName).HasMaxLength(100);
        b.Property(x => x.LastName).HasMaxLength(100);
        b.Property(x => x.Category).HasMaxLength(32);
        b.Ignore(x => x.DisplayName);
        b.HasOne<ClientCompany>().WithMany().HasForeignKey(x => x.ClientCompanyId);
        b.HasIndex(x => new { x.ClientCompanyId, x.EmployeeNumber }).IsUnique();
    }
}

internal sealed class BadgeConfiguration : IEntityTypeConfiguration<Badge>
{
    public void Configure(EntityTypeBuilder<Badge> b)
    {
        b.Property(x => x.Number).HasMaxLength(64);
        b.Ignore(x => x.IsUsable);
        b.HasOne<Diner>().WithMany().HasForeignKey(x => x.DinerId);
        b.HasOne<Badge>().WithMany().HasForeignKey(x => x.ReplacedByBadgeId);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => x.DinerId);
    }
}

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.Ignore(x => x.AvailableToSpend);
        b.HasOne<Diner>().WithMany().HasForeignKey(x => x.DinerId);
        b.HasOne<Contract>().WithMany().HasForeignKey(x => x.ContractId);
        b.HasIndex(x => new { x.DinerId, x.ContractId }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_Accounts_OverdraftLimit", "[OverdraftLimit] >= 0"));
    }
}

internal sealed class AccountMovementConfiguration : IEntityTypeConfiguration<AccountMovement>
{
    public void Configure(EntityTypeBuilder<AccountMovement> b)
    {
        b.Property(x => x.PerformedBy).HasMaxLength(200);
        b.Property(x => x.Comment).HasMaxLength(500);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<AccountMovement>().WithMany().HasForeignKey(x => x.ReversesMovementId);
        b.HasOne<Register>().WithMany().HasForeignKey(x => x.RegisterId);
        b.HasOne<Operator>().WithMany().HasForeignKey(x => x.OperatorId);
        // No FK to Ticket: a movement may reach the server before its ticket (independent outbox items).
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => x.ReversesMovementId).IsUnique().HasFilter("[ReversesMovementId] IS NOT NULL");
        b.HasIndex(x => new { x.AccountId, x.OccurredAt });
        b.HasIndex(x => x.TicketId).HasFilter("[TicketId] IS NOT NULL");
        b.HasIndex(x => new { x.RegisterId, x.OccurredAt }).HasFilter("[RegisterId] IS NOT NULL");
        b.ToTable(t => t.HasCheckConstraint("CK_AccountMovements_Amount", "[Amount] <> 0"));
    }
}

internal sealed class CashSessionConfiguration : IEntityTypeConfiguration<CashSession>
{
    public void Configure(EntityTypeBuilder<CashSession> b)
    {
        b.HasOne<Register>().WithMany().HasForeignKey(x => x.RegisterId);
        b.HasOne<Operator>().WithMany().HasForeignKey(x => x.OpenedByOperatorId);
        b.HasOne<Operator>().WithMany().HasForeignKey(x => x.ClosedByOperatorId);
        b.HasOne<ZReport>().WithMany().HasForeignKey(x => x.ZReportId);
        b.HasIndex(x => x.RegisterId).IsUnique().HasFilter("[Status] = 'Open'").HasDatabaseName("UX_CashSessions_OneOpenPerRegister");
        b.HasIndex(x => new { x.RegisterId, x.BusinessDate });
    }
}
