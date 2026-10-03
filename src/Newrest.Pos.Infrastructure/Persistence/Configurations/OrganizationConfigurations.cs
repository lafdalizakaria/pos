using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Newrest.Pos.Domain.Organization;
using Newrest.Pos.Domain.Security;

namespace Newrest.Pos.Infrastructure.Persistence.Configurations;

internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Ice).HasMaxLength(15);
        b.Property(x => x.TaxId).HasMaxLength(20);
        b.Property(x => x.TradeRegister).HasMaxLength(30);
        b.Property(x => x.Address).HasMaxLength(500);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> b)
    {
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.TimeZone).HasMaxLength(64);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
    }
}

internal sealed class PointOfSaleConfiguration : IEntityTypeConfiguration<PointOfSale>
{
    public void Configure(EntityTypeBuilder<PointOfSale> b)
    {
        b.ToTable("PointsOfSale");
        b.Property(x => x.Code).HasMaxLength(32);
        b.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId);
        b.HasIndex(x => new { x.SiteId, x.Code }).IsUnique();
    }
}

internal sealed class RegisterConfiguration : IEntityTypeConfiguration<Register>
{
    public void Configure(EntityTypeBuilder<Register> b)
    {
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.TicketPrefix).HasMaxLength(12);
        b.Property(x => x.LastSyncedTicketHash).HasMaxLength(64).IsFixedLength();
        b.Property(x => x.DeviceKeyHash).HasMaxLength(256);
        b.HasOne<PointOfSale>().WithMany().HasForeignKey(x => x.PointOfSaleId);
        b.HasIndex(x => new { x.PointOfSaleId, x.Code }).IsUnique();
        b.HasIndex(x => x.TicketPrefix).IsUnique();
    }
}

internal sealed class OperatorConfiguration : IEntityTypeConfiguration<Operator>
{
    public void Configure(EntityTypeBuilder<Operator> b)
    {
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.FirstName).HasMaxLength(100);
        b.Property(x => x.LastName).HasMaxLength(100);
        b.Property(x => x.PinHash).HasMaxLength(256);
        b.Property(x => x.Roles).HasConversion<int>();
        b.Ignore(x => x.DisplayName);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        b.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId);
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
    }
}

internal sealed class UserAccessScopeConfiguration : IEntityTypeConfiguration<UserAccessScope>
{
    public void Configure(EntityTypeBuilder<UserAccessScope> b)
    {
        b.Property(x => x.UserName).HasMaxLength(200);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        b.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId);
        b.HasIndex(x => new { x.UserName, x.CompanyId, x.SiteId }).IsUnique();
    }
}
