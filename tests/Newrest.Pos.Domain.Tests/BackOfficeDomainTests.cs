using Newrest.Pos.Domain.Catalog;
using Newrest.Pos.Domain.Clients;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Menus;
using Newrest.Pos.Domain.Organization;
using Newrest.Pos.Domain.Security;

namespace Newrest.Pos.Domain.Tests;

/// <summary>Domain operations used by the back-office (phase 2).</summary>
public class BackOfficeDomainTests
{
    [Fact]
    public void Organization_updates_validate_inputs()
    {
        var company = new Company(Guid.CreateVersion7(), "NFMS", "NFMS", "Old SA");
        company.Update("NFMS Maroc", "New SA");
        company.LegalName.Should().Be("New SA");
        FluentActions.Invoking(() => company.Update(" ", "x")).Should().Throw<DomainException>();

        var site = new Site(Guid.CreateVersion7(), company.Id, "CAS", "Casa", "Casablanca");
        site.Update("Casa Nearshore", "Casablanca");
        site.Name.Should().Be("Casa Nearshore");

        var pos = new PointOfSale(Guid.CreateVersion7(), site.Id, "SELF", "Self", PointOfSaleType.Self);
        pos.Rename("Self principal");
        pos.Name.Should().Be("Self principal");
    }

    [Fact]
    public void Register_device_key_is_set_and_revoked()
    {
        var register = new Register(Guid.CreateVersion7(), Guid.CreateVersion7(), "C01", "Caisse", "CAS1");
        register.SetDeviceKeyHash("abc", TestData.Now);
        register.DeviceKeyHash.Should().Be("abc");
        register.DeviceKeyIssuedAt.Should().Be(TestData.Now);
        register.Rename("Caisse 1");
        register.RevokeDeviceKey();
        register.DeviceKeyHash.Should().BeNull();
        register.DeviceKeyIssuedAt.Should().BeNull();
        FluentActions.Invoking(() => register.SetDeviceKeyHash("", TestData.Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Operator_rename_and_roles()
    {
        var op = new Operator(Guid.CreateVersion7(), Guid.CreateVersion7(), "C1", "A", "B", OperatorRoles.Cashier, "hash");
        op.Rename("Amina", "Berrada");
        op.SetRoles(OperatorRoles.Supervisor);
        op.DisplayName.Should().Be("Amina Berrada");
        op.HasRole(OperatorRoles.Cashier).Should().BeFalse();
        FluentActions.Invoking(() => op.SetRoles(OperatorRoles.None)).Should().Throw<DomainException>().Which.Code.Should().Be("role_required");
    }

    [Fact]
    public void Price_override_can_be_removed()
    {
        var list = new PriceList(Guid.CreateVersion7(), "L", "L", Guid.CreateVersion7(), TestData.Today);
        var article = Guid.CreateVersion7();
        list.SetPrice(article, 10m);
        list.RemovePrice(article).Should().BeTrue();
        list.RemovePrice(article).Should().BeFalse();
        list.Overrides.Should().BeEmpty();
    }

    [Fact]
    public void Menu_items_can_be_repriced_removed_and_menu_unpublished()
    {
        var menu = new DailyMenu(Guid.CreateVersion7(), Guid.CreateVersion7(), TestData.Today, MealService.Lunch);
        var article = Guid.CreateVersion7();
        menu.AddItem(article, 40m);
        menu.SetItemPrice(article, 38.5m);
        menu.Items.Single().EffectivePrice.Should().Be(38.5m);
        FluentActions.Invoking(() => menu.SetItemPrice(article, -1m)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => menu.SetItemPrice(article, 1.001m)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => menu.SetItemPrice(Guid.CreateVersion7(), 1m)).Should().Throw<DomainException>();
        menu.Publish();
        menu.Unpublish();
        menu.IsPublished.Should().BeFalse();
        menu.RemoveItem(article);
        menu.Items.Should().BeEmpty();
        FluentActions.Invoking(() => menu.RemoveItem(article)).Should().Throw<DomainException>().Which.Code.Should().Be("menu_item_not_found");
    }

    [Fact]
    public void Contract_period_and_subsidy_rule_closing()
    {
        var contract = new Contract(Guid.CreateVersion7(), Guid.CreateVersion7(), "CTR", new DateOnly(2026, 1, 1));
        contract.SetPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 12, 31));
        contract.IsActiveOn(new DateOnly(2026, 1, 15)).Should().BeFalse();
        FluentActions.Invoking(() => contract.SetPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1))).Should().Throw<DomainException>();

        var rule = new SubsidyRule(Guid.CreateVersion7(), contract.Id, "R", SubsidyKind.Percentage, 50m, new DateOnly(2026, 3, 1));
        rule.Close(new DateOnly(2026, 6, 30));
        rule.IsValidOn(new DateOnly(2026, 7, 1)).Should().BeFalse();
        rule.Close(new DateOnly(2026, 5, 31));
        rule.ValidTo.Should().Be(new DateOnly(2026, 5, 31));
        FluentActions.Invoking(() => rule.Close(new DateOnly(2026, 6, 15))).Should().Throw<DomainException>("a closed rule cannot be extended");
        FluentActions.Invoking(() => rule.Close(new DateOnly(2026, 2, 1))).Should().Throw<DomainException>();

        var future = new SubsidyRule(Guid.CreateVersion7(), contract.Id, "F", SubsidyKind.FixedAmount, 10m, new DateOnly(2026, 9, 1));
        future.Close(new DateOnly(2026, 8, 31));
        future.IsValidOn(new DateOnly(2026, 9, 1)).Should().BeFalse("closing the day before its start cancels a future rule");
    }

    [Fact]
    public void Access_scope_normalizes_user_names()
    {
        var company = Guid.CreateVersion7();
        var scope = new UserAccessScope(Guid.CreateVersion7(), " Karim.Alaoui@Newrest.MA ", company, Guid.Empty);

        scope.UserName.Should().Be("karim.alaoui@newrest.ma");
        scope.SiteId.Should().BeNull();
        FluentActions.Invoking(() => new UserAccessScope(Guid.CreateVersion7(), "x", Guid.Empty, null)).Should().Throw<DomainException>();
    }
}
