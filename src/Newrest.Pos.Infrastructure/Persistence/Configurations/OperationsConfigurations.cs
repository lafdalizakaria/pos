using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Newrest.Pos.Domain.Operations;
using Newrest.Pos.Domain.Organization;

namespace Newrest.Pos.Infrastructure.Persistence.Configurations;

internal sealed class RegisterHeartbeatConfiguration : IEntityTypeConfiguration<RegisterHeartbeat>
{
    public void Configure(EntityTypeBuilder<RegisterHeartbeat> b)
    {
        b.Ignore(x => x.RegisterId);
        b.Property(x => x.AppVersion).HasMaxLength(64);
        b.Property(x => x.BlockingError).HasMaxLength(300);
        b.HasOne<Register>().WithOne().HasForeignKey<RegisterHeartbeat>(x => x.Id);
    }
}

internal sealed class IntegrityCheckConfiguration : IEntityTypeConfiguration<IntegrityCheck>
{
    public void Configure(EntityTypeBuilder<IntegrityCheck> b)
    {
        b.Property(x => x.IssuesSummary).HasMaxLength(2000);
        b.HasOne<Register>().WithMany().HasForeignKey(x => x.RegisterId);
        b.HasIndex(x => new { x.RegisterId, x.CheckedAt });
    }
}

internal sealed class ArchiveRecordConfiguration : IEntityTypeConfiguration<ArchiveRecord>
{
    public void Configure(EntityTypeBuilder<ArchiveRecord> b)
    {
        b.Property(x => x.StoragePath).HasMaxLength(500);
        b.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength();
        b.Property(x => x.KeyId).HasMaxLength(64);
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.Property(x => x.CheckpointsJson).HasMaxLength(-1);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        b.HasIndex(x => new { x.CompanyId, x.Year, x.Month }).IsUnique();
    }
}
