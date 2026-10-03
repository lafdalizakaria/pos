using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Audit;
using Newrest.Pos.Domain.Catalog;
using Newrest.Pos.Domain.Clients;
using Newrest.Pos.Domain.Organization;
using Newrest.Pos.Domain.Sales;
using Newrest.Pos.Domain.Vision;

namespace Newrest.Pos.Infrastructure.Persistence.Configurations;

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> b)
    {
        b.Property(x => x.Number).HasMaxLength(32);
        b.Property(x => x.BadgeNumber).HasMaxLength(64);
        b.Property(x => x.CreditReason).HasMaxLength(500);
        b.Property(x => x.PreviousHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        b.Property(x => x.Hash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        b.HasOne<Register>().WithMany().HasForeignKey(x => x.RegisterId);
        b.HasOne<CashSession>().WithMany().HasForeignKey(x => x.CashSessionId);
        b.HasOne<Operator>().WithMany().HasForeignKey(x => x.OperatorId);
        b.HasOne<Diner>().WithMany().HasForeignKey(x => x.DinerId);
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
        b.HasOne<SubsidyRule>().WithMany().HasForeignKey(x => x.SubsidyRuleId);
        b.HasOne<Ticket>().WithMany().HasForeignKey(x => x.CreditedTicketId);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.TicketId);
        b.HasMany(x => x.Payments).WithOne().HasForeignKey(x => x.TicketId);

        // Gapless numbering is produced by the register; the database refuses duplicates.
        b.HasIndex(x => new { x.RegisterId, x.Sequence }).IsUnique();
        b.HasIndex(x => x.Number).IsUnique();
        // Only full credit notes exist: one per original ticket.
        b.HasIndex(x => x.CreditedTicketId).IsUnique().HasFilter("[CreditedTicketId] IS NOT NULL");
        // Daily subsidy cap across all points of sale.
        b.HasIndex(x => new { x.DinerId, x.BusinessDate }).HasFilter("[DinerId] IS NOT NULL")
            .IncludeProperties(x => new { x.SubsidyAmount, x.Kind });
        b.HasIndex(x => new { x.RegisterId, x.BusinessDate });
        b.HasIndex(x => x.CashSessionId);
        b.ToTable(t => t.HasCheckConstraint("CK_Tickets_Shares", "[TotalAmount] = [SubsidyAmount] + [DinerShare]"));
    }
}

internal sealed class TicketLineConfiguration : IEntityTypeConfiguration<TicketLine>
{
    public void Configure(EntityTypeBuilder<TicketLine> b)
    {
        b.Property(x => x.ArticleCode).HasMaxLength(32);
        b.Property(x => x.Label).HasMaxLength(150);
        b.Property(x => x.VatRate).IsRate();
        b.HasOne<Article>().WithMany().HasForeignKey(x => x.ArticleId);
        b.HasIndex(x => new { x.TicketId, x.LineNumber }).IsUnique();
        b.HasIndex(x => x.ArticleId);
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.Property(x => x.AuthorizationCode).HasMaxLength(64);
        b.HasIndex(x => new { x.TicketId, x.Index }).IsUnique();
    }
}

internal sealed class ZReportConfiguration : IEntityTypeConfiguration<ZReport>
{
    public void Configure(EntityTypeBuilder<ZReport> b)
    {
        b.Property(x => x.LastTicketHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        b.HasOne<Register>().WithMany().HasForeignKey(x => x.RegisterId);
        b.HasOne<CashSession>().WithMany().HasForeignKey(x => x.CashSessionId);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ZReportId);
        b.HasIndex(x => new { x.RegisterId, x.ZNumber }).IsUnique();
        b.HasIndex(x => x.CashSessionId).IsUnique();
    }
}

internal sealed class ZReportLineConfiguration : IEntityTypeConfiguration<ZReportLine>
{
    public void Configure(EntityTypeBuilder<ZReportLine> b)
    {
        b.Property(x => x.Key).HasMaxLength(32);
        b.HasIndex(x => new { x.ZReportId, x.Section, x.Key }).IsUnique();
    }
}

internal sealed class RecognitionLogConfiguration : IEntityTypeConfiguration<RecognitionLog>
{
    public void Configure(EntityTypeBuilder<RecognitionLog> b)
    {
        b.Property(x => x.Provider).HasMaxLength(32);
        b.Property(x => x.ImageStorageKey).HasMaxLength(500);
        b.Property(x => x.RawPredictionsJson).HasMaxLength(-1);
        b.Property(x => x.ValidatedLinesJson).HasMaxLength(-1);
        b.HasOne<Register>().WithMany().HasForeignKey(x => x.RegisterId);
        b.HasIndex(x => new { x.RegisterId, x.CapturedAt });
        b.HasIndex(x => x.TicketId).HasFilter("[TicketId] IS NOT NULL");
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLog");
        b.Property(x => x.EntityId).HasMaxLength(64);
        b.Property(x => x.BeforeJson).HasMaxLength(-1);
        b.Property(x => x.AfterJson).HasMaxLength(-1);
        b.Property(x => x.IpAddress).HasMaxLength(45);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.HasIndex(x => x.OccurredAt);
        b.HasIndex(x => new { x.EntityType, x.EntityId });
    }
}
