using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Testing;

namespace Newrest.Pos.Api.Tests;

public sealed class CatalogTests(SqlServerFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Admin_manages_the_master_catalogue_and_price_changes_are_audited()
    {
        var admin = AdminClient();
        var plat = DemoIds.Category("PLAT");
        var created = await ReadAsync<ArticleDto>(await admin.PostAsJsonAsync("/api/v1/articles",
            new ArticleUpsert("taj-agn", "Tajine agneau", null, plat, 48m, 0.10m, "Agneau en sauce, abricots secs")));
        created.Code.Should().Be("TAJ-AGN");
        (await admin.PostAsJsonAsync("/api/v1/articles", new ArticleUpsert("TAJ-AGN", "x", null, plat, 1m, 0.1m, null)))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync("/api/v1/articles", new ArticleUpsert("BAD", "x", null, plat, 1.234m, 0.1m, null)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await admin.PostAsJsonAsync("/api/v1/articles", new ArticleUpsert("BAD2", "x", null, Guid.NewGuid(), 1m, 0.1m, null)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var updated = await ReadAsync<ArticleDto>(await admin.PutAsJsonAsync($"/api/v1/articles/{created.Id}",
            new ArticleUpsert("TAJ-AGN", "Tajine d'agneau", "TAJINE AGNEAU", plat, 50m, 0.10m, "Agneau, abricots", IsSubsidizable: false)));
        updated.BasePrice.Should().Be(50m);
        updated.IsSubsidizable.Should().BeFalse();
        (await admin.PutAsJsonAsync($"/api/v1/articles/{created.Id}", new ArticleUpsert("OTHER", "x", null, plat, 1m, 0.1m, null)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "article codes are immutable");

        var audit = await ReadAsync<PagedResult<AuditEntryDto>>(await admin.GetAsync($"/api/v1/audit?entityType=Article&entityId={created.Id}"));
        var priceChange = audit.Items.Single(a => a.Action == "PriceChanged");
        priceChange.BeforeJson.Should().Contain("48");
        priceChange.AfterJson.Should().Contain("50");

        var page = await ReadAsync<PagedResult<ArticleDto>>(await ViewerClient().GetAsync("/api/v1/articles?search=couscous&pageSize=2"));
        page.TotalCount.Should().Be(3);
        page.Items.Should().HaveCount(2);
        (await ReadAsync<List<CategoryDto>>(await ViewerClient().GetAsync("/api/v1/categories"))).Should().HaveCount(5);

        var category = await ReadAsync<CategoryDto>(await admin.PostAsJsonAsync("/api/v1/categories", new CategoryUpsert("viennois", "Viennoiseries", 6, "#AA7700")));
        (await ReadAsync<CategoryDto>(await admin.PutAsJsonAsync($"/api/v1/categories/{category.Id}",
            new CategoryUpsert("VIENNOIS", "Viennoiseries & pâtisseries", 6, null, false)))).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Reference_photos_are_uploaded_validated_served_and_deleted()
    {
        var admin = AdminClient();
        using var image = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var form = new MultipartFormDataContent { { image, "file", "couscous.jpg" } };

        var photo = await ReadAsync<ArticlePhotoDto>(await admin.PostAsync($"/api/v1/articles/{CouscousViande}/photos?caption=vue%20de%20dessus", form));
        photo.StoragePath.Should().StartWith("articles/CSC-VND/").And.EndWith(".jpg");
        (await (await ViewerClient().GetAsync($"/api/v1/photos/{photo.Id}")).Content.ReadAsByteArrayAsync()).Should().HaveCount(7);
        (await ReadAsync<ArticleDto>(await admin.GetAsync($"/api/v1/articles/{CouscousViande}"))).Photos.Should().ContainSingle();

        using var text = new MultipartFormDataContent { { new StringContent("hello"), "file", "notes.txt" } };
        var rejected = await admin.PostAsync($"/api/v1/articles/{CouscousViande}/photos", text);
        rejected.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(rejected)).Should().Be("invalid_file");

        (await admin.DeleteAsync($"/api/v1/articles/{CouscousViande}/photos/{photo.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ViewerClient().GetAsync($"/api/v1/photos/{photo.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Site_manager_overrides_prices_for_its_point_of_sale_only()
    {
        var manager = ManagerClient();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var list = await ReadAsync<PriceListDto>(await manager.PostAsJsonAsync("/api/v1/price-lists",
            new PriceListCreate("cas-self-promo", "Promo self Casablanca", Nfms, CasaSite, CasaSelf, today.AddDays(-1), null)));
        list.Code.Should().Be("CAS-SELF-PROMO");

        var withPrice = await ReadAsync<PriceListDto>(await manager.PutAsJsonAsync(
            $"/api/v1/price-lists/{list.Id}/prices/{CouscousPoulet}", new SetPriceRequest(35m)));
        withPrice.Overrides.Should().ContainSingle().Which.Should().Match<PriceOverrideDto>(o => o.ArticleCode == "CSC-PLT" && o.Price == 35m);

        var prices = await ReadAsync<List<EffectivePriceDto>>(await manager.GetAsync($"/api/v1/points-of-sale/{CasaSelf}/prices"));
        prices.Single(p => p.ArticleId == CouscousPoulet).EffectivePrice.Should().Be(35m);
        prices.Single(p => p.ArticleId == CouscousViande).EffectivePrice.Should().Be(42m);
        var snack = await ReadAsync<List<EffectivePriceDto>>(await AdminClient().GetAsync($"/api/v1/points-of-sale/{TngSnack}/prices"));
        snack.Single(p => p.ArticleId == Cafe).EffectivePrice.Should().Be(6m);
        snack.Single(p => p.ArticleId == CouscousPoulet).EffectivePrice.Should().Be(38m);

        (await manager.PostAsJsonAsync("/api/v1/price-lists", new PriceListCreate("TNG", "Tanger", Nfms, TangerSite, null, today, null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.PostAsJsonAsync("/api/v1/price-lists", new PriceListCreate("ALL", "Société", Nfms, null, null, today, null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "company-wide lists need a company scope");
        (await manager.PostAsJsonAsync("/api/v1/price-lists", new PriceListCreate("BAD", "x", Nfms, TangerSite, CasaSelf, today, null)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var closed = await ReadAsync<PriceListDto>(await manager.PutAsJsonAsync($"/api/v1/price-lists/{list.Id}",
            new PriceListUpdate("Promo terminée", today.AddDays(-1), today.AddDays(-1), true)));
        closed.ValidTo.Should().Be(today.AddDays(-1));
        (await ReadAsync<List<EffectivePriceDto>>(await manager.GetAsync($"/api/v1/points-of-sale/{CasaSelf}/prices")))
            .Single(p => p.ArticleId == CouscousPoulet).EffectivePrice.Should().Be(38m);

        (await ReadAsync<PriceListDto>(await manager.DeleteAsync($"/api/v1/price-lists/{list.Id}/prices/{CouscousPoulet}"))).Overrides.Should().BeEmpty();
        (await manager.DeleteAsync($"/api/v1/price-lists/{list.Id}/prices/{CouscousPoulet}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync<PriceListDto>(await manager.GetAsync($"/api/v1/price-lists/{list.Id}"))).Name.Should().Be("Promo terminée");
        (await ReadAsync<List<PriceListDto>>(await manager.GetAsync("/api/v1/price-lists"))).Select(p => p.Code)
            .Should().Contain("CAS-SELF-PROMO").And.NotContain("TNG-SNACK").And.NotContain("NMS-STD");

        var audit = await ReadAsync<PagedResult<AuditEntryDto>>(await AdminClient().GetAsync($"/api/v1/audit?entityType=PriceList&entityId={list.Id}"));
        audit.Items.Select(a => a.Action).Should().Contain(["Created", "PriceChanged", "PriceRemoved"]);
    }
}
