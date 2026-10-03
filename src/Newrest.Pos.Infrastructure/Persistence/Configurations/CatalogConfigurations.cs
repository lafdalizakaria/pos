using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Newrest.Pos.Domain.Catalog;
using Newrest.Pos.Domain.Menus;
using Newrest.Pos.Domain.Organization;

namespace Newrest.Pos.Infrastructure.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.ToTable("Categories");
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.ColorHex).HasMaxLength(9);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class ArticleConfiguration : IEntityTypeConfiguration<Article>
{
    public void Configure(EntityTypeBuilder<Article> b)
    {
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(150);
        b.Property(x => x.ReceiptLabel).HasMaxLength(40);
        b.Property(x => x.VisualDescription).HasMaxLength(1000);
        b.Property(x => x.VatRate).IsRate();
        b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId);
        b.HasMany(x => x.Photos).WithOne().HasForeignKey(x => x.ArticleId).CascadeChildren();
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class ArticlePhotoConfiguration : IEntityTypeConfiguration<ArticlePhoto>
{
    public void Configure(EntityTypeBuilder<ArticlePhoto> b)
    {
        b.Property(x => x.StoragePath).HasMaxLength(500);
        b.HasIndex(x => new { x.ArticleId, x.DisplayOrder });
    }
}

internal sealed class PriceListConfiguration : IEntityTypeConfiguration<PriceList>
{
    public void Configure(EntityTypeBuilder<PriceList> b)
    {
        b.Property(x => x.Code).HasMaxLength(32);
        b.Ignore(x => x.Scope);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        b.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId);
        b.HasOne<PointOfSale>().WithMany().HasForeignKey(x => x.PointOfSaleId);
        b.HasMany(x => x.Overrides).WithOne().HasForeignKey(x => x.PriceListId).CascadeChildren();
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
    }
}

internal sealed class PriceOverrideConfiguration : IEntityTypeConfiguration<PriceOverride>
{
    public void Configure(EntityTypeBuilder<PriceOverride> b)
    {
        b.HasOne<Article>().WithMany().HasForeignKey(x => x.ArticleId);
        b.HasIndex(x => new { x.PriceListId, x.ArticleId }).IsUnique();
    }
}

internal sealed class DailyMenuConfiguration : IEntityTypeConfiguration<DailyMenu>
{
    public void Configure(EntityTypeBuilder<DailyMenu> b)
    {
        b.HasOne<PointOfSale>().WithMany().HasForeignKey(x => x.PointOfSaleId);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.DailyMenuId).CascadeChildren();
        b.HasIndex(x => new { x.PointOfSaleId, x.Date, x.Service }).IsUnique();
    }
}

internal sealed class DailyMenuItemConfiguration : IEntityTypeConfiguration<DailyMenuItem>
{
    public void Configure(EntityTypeBuilder<DailyMenuItem> b)
    {
        b.HasOne<Article>().WithMany().HasForeignKey(x => x.ArticleId);
        b.HasIndex(x => new { x.DailyMenuId, x.ArticleId }).IsUnique();
    }
}
