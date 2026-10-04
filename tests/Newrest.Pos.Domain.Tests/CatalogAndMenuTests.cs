using Newrest.Pos.Domain.Catalog;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Menus;

namespace Newrest.Pos.Domain.Tests;

public class CatalogAndMenuTests
{
    private static readonly Guid CompanyId = Guid.CreateVersion7();
    private static readonly Guid SiteId = Guid.CreateVersion7();
    private static readonly Guid PosId = Guid.CreateVersion7();
    private static readonly PriceContext Context = new(CompanyId, SiteId, PosId);

    private static Article Couscous() => new(Guid.CreateVersion7(), "csc-vnd", "Couscous viande", Guid.CreateVersion7(), 40m, 0.10m);

    [Fact]
    public void Base_price_is_used_without_override()
    {
        var article = Couscous();

        article.Code.Should().Be("CSC-VND");
        PriceResolver.Resolve(article, [], Context, TestData.Today).Should().Be(40m);
    }

    [Fact]
    public void Most_specific_valid_override_wins()
    {
        var article = Couscous();
        var company = new PriceList(Guid.CreateVersion7(), "STD", "Standard", CompanyId, new DateOnly(2026, 1, 1));
        company.SetPrice(article.Id, 42m);
        var site = new PriceList(Guid.CreateVersion7(), "SITE", "Site", CompanyId, new DateOnly(2026, 1, 1), siteId: SiteId);
        site.SetPrice(article.Id, 38m);
        var pos = new PriceList(Guid.CreateVersion7(), "POS", "Snack", CompanyId, new DateOnly(2026, 1, 1), siteId: SiteId, pointOfSaleId: PosId);
        pos.SetPrice(article.Id, 35m);
        var otherPos = new PriceList(Guid.CreateVersion7(), "OTHER", "Autre", CompanyId, new DateOnly(2026, 1, 1), pointOfSaleId: Guid.CreateVersion7());
        otherPos.SetPrice(article.Id, 1m);
        var expiredPos = new PriceList(Guid.CreateVersion7(), "OLD", "Promo", CompanyId, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), pointOfSaleId: PosId);
        expiredPos.SetPrice(article.Id, 2m);

        PriceResolver.Resolve(article, [company, site, pos, otherPos, expiredPos], Context, TestData.Today).Should().Be(35m);
        PriceResolver.Resolve(article, [company, site, otherPos], Context, TestData.Today).Should().Be(38m);
        PriceResolver.Resolve(article, [company], Context, TestData.Today).Should().Be(42m);
        PriceResolver.Resolve(article, [expiredPos], Context, new DateOnly(2026, 1, 15)).Should().Be(2m);
        pos.IsActive = false;
        PriceResolver.Resolve(article, [company, site, pos], Context, TestData.Today).Should().Be(38m);
        pos.Scope.Should().Be(PriceScope.PointOfSale);
        site.Scope.Should().Be(PriceScope.Site);
        company.Scope.Should().Be(PriceScope.Company);
    }

    [Fact]
    public void Newer_list_wins_on_same_scope_and_set_price_updates()
    {
        var article = Couscous();
        var older = new PriceList(Guid.CreateVersion7(), "A", "A", CompanyId, new DateOnly(2026, 1, 1));
        older.SetPrice(article.Id, 41m);
        var newer = new PriceList(Guid.CreateVersion7(), "B", "B", CompanyId, new DateOnly(2026, 9, 1));
        newer.SetPrice(article.Id, 43m);
        newer.SetPrice(article.Id, 44m);

        newer.Overrides.Should().ContainSingle();
        PriceResolver.Resolve(article, [older, newer], Context, TestData.Today).Should().Be(44m);
    }

    [Fact]
    public void Catalog_guards()
    {
        var article = Couscous();
        FluentActions.Invoking(() => article.SetBasePrice(-1m)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => article.SetBasePrice(1.234m)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => article.SetVatRate(1m)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => new PriceList(Guid.CreateVersion7(), "X", "X", CompanyId, new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)))
            .Should().Throw<DomainException>();
        article.AddPhoto("articles/csc-vnd/1.jpg").DisplayOrder.Should().Be(0);
        article.AddPhoto("articles/csc-vnd/2.jpg").DisplayOrder.Should().Be(1);
        new Category(Guid.CreateVersion7(), "plat", "Plats", 1).Code.Should().Be("PLAT");
    }

    [Fact]
    public void Daily_menu_lifecycle_and_copy()
    {
        var menu = new DailyMenu(Guid.CreateVersion7(), PosId, TestData.Today, MealService.Lunch);
        FluentActions.Invoking(menu.Publish).Should().Throw<DomainException>().Which.Code.Should().Be("empty_menu");

        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        menu.AddItem(a, 40m);
        menu.AddItem(b, 38m);
        FluentActions.Invoking(() => menu.AddItem(a, 1m)).Should().Throw<DomainException>().Which.Code.Should().Be("duplicate_menu_item");
        menu.SetAvailability(b, false);
        FluentActions.Invoking(() => menu.SetAvailability(Guid.CreateVersion7(), false)).Should().Throw<DomainException>();
        menu.Publish();
        menu.IsPublished.Should().BeTrue();

        var copy = menu.CopyTo(TestData.Today.AddDays(1));

        copy.Id.Should().NotBe(menu.Id);
        copy.IsPublished.Should().BeFalse();
        copy.Date.Should().Be(TestData.Today.AddDays(1));
        copy.Items.Select(i => (i.ArticleId, i.EffectivePrice, i.IsAvailable)).Should().Equal((a, 40m, true), (b, 38m, true));
    }
}
