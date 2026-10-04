using System.Net;
using System.Net.Http.Json;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Api.Tests;

public sealed class MenuTests(SqlServerFixture fixture) : ApiTestBase(fixture)
{
    private static readonly DateOnly Monday = new(2027, 1, 4);

    [Fact]
    public async Task Menu_is_built_with_resolved_prices_edited_published_and_copied()
    {
        var manager = ManagerClient();
        var menu = await ReadAsync<DailyMenuDto>(await manager.PostAsJsonAsync("/api/v1/daily-menus",
            new DailyMenuCreate(CasaSelf, Monday, "Lunch", [CouscousViande, CouscousPoulet])));
        menu.Items.Select(i => (i.ArticleCode, i.EffectivePrice)).Should().Equal(("CSC-VND", 42m), ("CSC-PLT", 38m));
        (await manager.PostAsJsonAsync("/api/v1/daily-menus", new DailyMenuCreate(CasaSelf, Monday, "Lunch", null)))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await manager.PostAsJsonAsync("/api/v1/daily-menus", new DailyMenuCreate(CasaSelf, Monday, "Brunch", null)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        menu = await ReadAsync<DailyMenuDto>(await manager.PostAsJsonAsync($"/api/v1/daily-menus/{menu.Id}/items", new MenuItemAdd(Cafe, 6.5m)));
        menu.Items.Should().HaveCount(3);
        (await manager.PostAsJsonAsync($"/api/v1/daily-menus/{menu.Id}/items", new MenuItemAdd(Cafe, null)))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        menu = await ReadAsync<DailyMenuDto>(await manager.PutAsJsonAsync($"/api/v1/daily-menus/{menu.Id}/items/{CouscousPoulet}",
            new MenuItemUpdate(36m, false)));
        menu.Items.Single(i => i.ArticleId == CouscousPoulet).Should().Match<DailyMenuItemDto>(i => i.EffectivePrice == 36m && !i.IsAvailable);
        menu = await ReadAsync<DailyMenuDto>(await manager.DeleteAsync($"/api/v1/daily-menus/{menu.Id}/items/{Cafe}"));
        menu.Items.Should().HaveCount(2);

        menu = await ReadAsync<DailyMenuDto>(await manager.PostAsync($"/api/v1/daily-menus/{menu.Id}/publish", null));
        menu.IsPublished.Should().BeTrue();
        (await manager.DeleteAsync($"/api/v1/daily-menus/{menu.Id}")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var copy = await ReadAsync<MenuCopyResult>(await manager.PostAsJsonAsync($"/api/v1/daily-menus/{menu.Id}/copy",
            new MenuCopyRequest([Monday.AddDays(1), Monday.AddDays(2)], null)));
        copy.Created.Should().Be(2);
        var again = await ReadAsync<MenuCopyResult>(await manager.PostAsJsonAsync($"/api/v1/daily-menus/{menu.Id}/copy",
            new MenuCopyRequest([Monday.AddDays(1)], null)));
        again.Skipped.Should().Be(1);
        var replaced = await ReadAsync<MenuCopyResult>(await manager.PostAsJsonAsync($"/api/v1/daily-menus/{menu.Id}/copy",
            new MenuCopyRequest([Monday.AddDays(1)], null, Overwrite: true)));
        replaced.Replaced.Should().Be(1);

        var week = await ReadAsync<List<DailyMenuDto>>(await manager.GetAsync(
            $"/api/v1/daily-menus?pointOfSaleId={CasaSelf}&from={Monday:yyyy-MM-dd}&to={Monday.AddDays(6):yyyy-MM-dd}"));
        week.Should().HaveCount(3);
        week.Where(m => m.Date != Monday).Should().OnlyContain(m => !m.IsPublished && m.Items.Count == 2);
        week.Single(m => m.Date == Monday.AddDays(1)).Items.Single(i => i.ArticleId == CouscousPoulet).EffectivePrice.Should().Be(36m);

        var nextWeek = await ReadAsync<MenuCopyResult>(await manager.PostAsJsonAsync("/api/v1/daily-menus/copy-week",
            new MenuWeekCopyRequest(CasaSelf, Monday, Monday.AddDays(7))));
        nextWeek.Created.Should().Be(3);
        (await manager.PostAsJsonAsync("/api/v1/daily-menus/copy-week", new MenuWeekCopyRequest(CasaSelf, Monday, Monday)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        menu = await ReadAsync<DailyMenuDto>(await manager.PostAsync($"/api/v1/daily-menus/{menu.Id}/unpublish", null));
        (await manager.DeleteAsync($"/api/v1/daily-menus/{menu.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await manager.GetAsync($"/api/v1/daily-menus/{menu.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Menus_respect_scope_and_cross_point_of_sale_copy_needs_target_access()
    {
        var manager = ManagerClient();
        (await manager.PostAsJsonAsync("/api/v1/daily-menus", new DailyMenuCreate(TngSnack, Monday, "Lunch", null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var source = await ReadAsync<DailyMenuDto>(await manager.PostAsJsonAsync("/api/v1/daily-menus",
            new DailyMenuCreate(CasaSelf, Monday.AddDays(20), "Dinner", [CouscousViande])));
        (await manager.PostAsJsonAsync($"/api/v1/daily-menus/{source.Id}/copy", new MenuCopyRequest([Monday], TngSnack)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var copied = await ReadAsync<MenuCopyResult>(await AdminClient().PostAsJsonAsync($"/api/v1/daily-menus/{source.Id}/copy",
            new MenuCopyRequest([Monday.AddDays(20)], TngSnack)));
        copied.Created.Should().Be(1);
        (await manager.GetAsync($"/api/v1/daily-menus?pointOfSaleId={CasaSelf}&from={Monday:yyyy-MM-dd}&to={Monday.AddDays(100):yyyy-MM-dd}"))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "range limited to 62 days");
    }
}
