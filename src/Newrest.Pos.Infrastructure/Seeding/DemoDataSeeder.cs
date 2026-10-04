using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Catalog;
using Newrest.Pos.Domain.Clients;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Menus;
using Newrest.Pos.Domain.Organization;
using Newrest.Pos.Domain.Security;
using Newrest.Pos.Infrastructure.Persistence;

namespace Newrest.Pos.Infrastructure.Seeding;

/// <summary>
/// Demonstration data (never run in production): 2 companies, 3 sites, 5 points of sale, 6 registers,
/// 30 articles, 2 fictitious B2B clients with contracts, subsidy rules, diners, badges and funded accounts,
/// and today's lunch menu on every point of sale. Identifiers are deterministic so the seeder is idempotent.
/// Demo operator PINs are documented in docs/runbook.md and must be changed on any shared environment.
/// </summary>
public sealed partial class DemoDataSeeder(PosDbContext db, IPinHasher pinHasher, TimeProvider clock, ILogger<DemoDataSeeder> logger)
{
    public const string DemoCashierPin = "1234";
    public const string DemoSupervisorPin = "5678";

    private static readonly TimeZoneInfo Morocco = TimeZoneInfo.FindSystemTimeZoneById("Africa/Casablanca");

    public async Task<bool> SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await db.Companies.AnyAsync(c => c.Id == Id("company:NFMS"), cancellationToken))
        {
            LogAlreadySeeded(logger);
            return false;
        }

        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Morocco).DateTime);

        // --- Organisation -------------------------------------------------------------------------
        var nfms = new Company(Id("company:NFMS"), "NFMS", "NFMS", "Newrest Food Management Services Maroc (démo)")
        {
            Ice = "000000000000001",
            TaxId = "00000001",
            TradeRegister = "RC-DEMO-1",
            Address = "Casablanca",
        };
        var nms = new Company(Id("company:NMS"), "NMS", "NMS", "Newrest Maroc Services (démo)")
        {
            Ice = "000000000000002",
            TaxId = "00000002",
            TradeRegister = "RC-DEMO-2",
            Address = "Rabat",
        };
        db.Companies.AddRange(nfms, nms);

        var casa = new Site(Id("site:CAS-SM"), nfms.Id, "CAS-SM", "Casablanca Sidi Maârouf", "Casablanca");
        var tanger = new Site(Id("site:TNG-TFZ"), nfms.Id, "TNG-TFZ", "Tanger Free Zone", "Tanger");
        var kenitra = new Site(Id("site:KEN-AFZ"), nms.Id, "KEN-AFZ", "Kénitra Atlantic Free Zone", "Kénitra");
        db.Sites.AddRange(casa, tanger, kenitra);

        var casaSelf = new PointOfSale(Id("pos:CAS-SELF"), casa.Id, "CAS-SELF", "Self Casablanca", PointOfSaleType.Self);
        var casaCafet = new PointOfSale(Id("pos:CAS-CAFET"), casa.Id, "CAS-CAFET", "Cafétéria Casablanca", PointOfSaleType.Cafeteria);
        var tngSelf = new PointOfSale(Id("pos:TNG-SELF"), tanger.Id, "TNG-SELF", "Self Tanger", PointOfSaleType.Self);
        var tngSnack = new PointOfSale(Id("pos:TNG-SNACK"), tanger.Id, "TNG-SNACK", "Snack Tanger", PointOfSaleType.Snack);
        var kenSelf = new PointOfSale(Id("pos:KEN-SELF"), kenitra.Id, "KEN-SELF", "Self Kénitra", PointOfSaleType.Self);
        PointOfSale[] pointsOfSale = [casaSelf, casaCafet, tngSelf, tngSnack, kenSelf];
        db.PointsOfSale.AddRange(pointsOfSale);

        db.Registers.AddRange(
            new Register(Id("register:CAS1"), casaSelf.Id, "C01", "Self Casablanca - Caisse 1", "CAS1"),
            new Register(Id("register:CAS2"), casaSelf.Id, "C02", "Self Casablanca - Caisse 2", "CAS2"),
            new Register(Id("register:CAS3"), casaCafet.Id, "C01", "Cafétéria Casablanca - Caisse 1", "CAS3"),
            new Register(Id("register:TNG1"), tngSelf.Id, "C01", "Self Tanger - Caisse 1", "TNG1"),
            new Register(Id("register:TNG2"), tngSnack.Id, "C01", "Snack Tanger - Caisse 1", "TNG2"),
            new Register(Id("register:KEN1"), kenSelf.Id, "C01", "Self Kénitra - Caisse 1", "KEN1"));

        var cashierPin = pinHasher.Hash(DemoCashierPin);
        var supervisorPin = pinHasher.Hash(DemoSupervisorPin);
        db.Operators.AddRange(
            new Operator(Id("operator:NFMS:CAIS01"), nfms.Id, "CAIS01", "Fatima", "Zahra", OperatorRoles.Cashier, cashierPin) { SiteId = casa.Id },
            new Operator(Id("operator:NFMS:CAIS02"), nfms.Id, "CAIS02", "Hamza", "Idrissi", OperatorRoles.Cashier, cashierPin) { SiteId = tanger.Id },
            new Operator(Id("operator:NFMS:RESP01"), nfms.Id, "RESP01", "Karim", "Alaoui", OperatorRoles.Cashier | OperatorRoles.Supervisor, supervisorPin),
            new Operator(Id("operator:NMS:CAIS01"), nms.Id, "CAIS01", "Salma", "Bennani", OperatorRoles.Cashier, cashierPin) { SiteId = kenitra.Id },
            new Operator(Id("operator:NMS:RESP01"), nms.Id, "RESP01", "Youssef", "Tazi", OperatorRoles.Cashier | OperatorRoles.Supervisor | OperatorRoles.Admin, supervisorPin));

        // --- Catalogue ----------------------------------------------------------------------------
        var categories = new Dictionary<string, Category>
        {
            ["PLAT"] = new(Id("category:PLAT"), "PLAT", "Plats chauds", 1, "#C0392B"),
            ["ENTREE"] = new(Id("category:ENTREE"), "ENTREE", "Entrées & soupes", 2, "#27AE60"),
            ["ACCOMP"] = new(Id("category:ACCOMP"), "ACCOMP", "Accompagnements & pain", 3, "#D4AC0D"),
            ["DESSERT"] = new(Id("category:DESSERT"), "DESSERT", "Desserts & fruits", 4, "#8E44AD"),
            ["BOISSON"] = new(Id("category:BOISSON"), "BOISSON", "Boissons", 5, "#2980B9"),
        };
        db.Categories.AddRange(categories.Values);

        var articles = Articles.Select(a =>
        {
            var article = new Article(Id($"article:{a.Code}"), a.Code, a.Name, categories[a.Category].Id, a.Price, a.Vat)
            {
                VisualDescription = a.Visual,
                IsSubsidizable = a.Subsidizable,
            };
            return article;
        }).ToDictionary(a => a.Code);
        db.Articles.AddRange(articles.Values);

        // Snack Tanger sells hot drinks cheaper (point-of-sale override).
        var snackPrices = new PriceList(Id("pricelist:TNG-SNACK"), "TNG-SNACK", "Tarifs Snack Tanger", nfms.Id,
            today.AddDays(-30), siteId: tanger.Id, pointOfSaleId: tngSnack.Id);
        snackPrices.SetPrice(articles["CAF-EXP"].Id, 6.00m);
        snackPrices.SetPrice(articles["THE-MEN"].Id, 5.00m);
        // Kénitra (NMS) has its own company-level price list for main dishes.
        var nmsPrices = new PriceList(Id("pricelist:NMS-STD"), "NMS-STD", "Tarifs standard NMS", nms.Id, today.AddDays(-30));
        nmsPrices.SetPrice(articles["CSC-VND"].Id, 40.00m);
        nmsPrices.SetPrice(articles["CSC-PLT"].Id, 36.00m);
        PriceList[] priceLists = [snackPrices, nmsPrices];
        db.PriceLists.AddRange(priceLists);

        // --- Today's lunch menus ------------------------------------------------------------------
        var siteOf = new Dictionary<Guid, Site> { [casa.Id] = casa, [tanger.Id] = tanger, [kenitra.Id] = kenitra };
        foreach (var pos in pointsOfSale)
        {
            var site = siteOf[pos.SiteId];
            var menu = new DailyMenu(Id($"menu:{pos.Code}:{today:yyyy-MM-dd}:Lunch"), pos.Id, today, MealService.Lunch);
            var codes = pos.Type == PointOfSaleType.Self ? SelfMenu : SnackMenu;
            var context = new PriceContext(site.CompanyId, site.Id, pos.Id);
            foreach (var code in codes)
            {
                var article = articles[code];
                menu.AddItem(article.Id, PriceResolver.Resolve(article, priceLists, context, today));
            }

            menu.Publish();
            db.DailyMenus.Add(menu);
        }

        // --- B2B clients --------------------------------------------------------------------------
        var atlas = new ClientCompany(Id("client:ATLAS"), nfms.Id, "ATLAS", "Atlas Automotive Components (fictif)") { Ice = "000000000000101" };
        var sahara = new ClientCompany(Id("client:SAHARA"), nms.Id, "SAHARA", "Sahara Aero Structures (fictif)") { Ice = "000000000000102" };
        db.ClientCompanies.AddRange(atlas, sahara);

        var contractStart = new DateOnly(today.Year, 1, 1);
        var atlasContract = new Contract(Id("contract:ATLAS"), atlas.Id, "CTR-ATLAS-2026", contractStart) { BillingMode = BillingMode.EmployerInvoice };
        atlasContract.AcceptPointOfSale(casaSelf.Id);
        atlasContract.AcceptPointOfSale(casaCafet.Id);
        atlasContract.AcceptPointOfSale(tngSelf.Id);
        atlasContract.AcceptPointOfSale(tngSnack.Id);
        atlasContract.AddSubsidyRule(new SubsidyRule(Id("subsidy:ATLAS:STD"), atlasContract.Id, "Standard 50 % plafonné 20 MAD/jour",
            SubsidyKind.Percentage, 50m, contractStart, maxPerDay: 20m));
        atlasContract.AddSubsidyRule(new SubsidyRule(Id("subsidy:ATLAS:CADRE"), atlasContract.Id, "Cadres 25 MAD par repas",
            SubsidyKind.FixedAmount, 25m, contractStart, maxPerDay: 25m, dinerCategory: "CADRE"));

        var saharaContract = new Contract(Id("contract:SAHARA"), sahara.Id, "CTR-SAHARA-2026", contractStart) { BillingMode = BillingMode.PayrollDeduction };
        saharaContract.AcceptPointOfSale(kenSelf.Id);
        saharaContract.AddSubsidyRule(new SubsidyRule(Id("subsidy:SAHARA:STD"), saharaContract.Id, "15 MAD par repas, 1 repas/jour",
            SubsidyKind.FixedAmount, 15m, contractStart, maxMealsPerDay: 1));
        db.Contracts.AddRange(atlasContract, saharaContract);

        AddDiners(atlas, atlasContract, "ATL", now,
        [
            ("Youssef", "Benali", "CADRE", AccountType.Prepaid, 0m, 300m),
            ("Khadija", "El Amrani", null, AccountType.Prepaid, 0m, 150m),
            ("Mehdi", "Chraibi", null, AccountType.Prepaid, 0m, 20m),
            ("Nadia", "Berrada", null, AccountType.Mixed, 200m, 50m),
            ("Omar", "Fassi", null, AccountType.Postpaid, 800m, 0m),
        ]);
        AddDiners(sahara, saharaContract, "SAH", now,
        [
            ("Amine", "Kettani", null, AccountType.Postpaid, 600m, 0m),
            ("Imane", "Lahlou", null, AccountType.Prepaid, 0m, 100m),
            ("Rachid", "Ouazzani", null, AccountType.Mixed, 100m, 40m),
        ]);

        await db.SaveChangesAsync(cancellationToken);
        LogSeeded(logger, Articles.Length, pointsOfSale.Length);
        return true;
    }

    private void AddDiners(ClientCompany client, Contract contract, string prefix, DateTimeOffset now,
        (string First, string Last, string? Category, AccountType Type, decimal Overdraft, decimal InitialTopUp)[] diners)
    {
        for (var i = 0; i < diners.Length; i++)
        {
            var d = diners[i];
            var number = $"{prefix}{i + 1:D4}";
            var diner = new Diner(Id($"diner:{number}"), client.Id, $"M{number}", d.First, d.Last) { Category = d.Category };
            var badge = new Badge(Id($"badge:{number}"), diner.Id, $"BDG-{number}", now);
            var account = new Account(Id($"account:{number}"), diner.Id, contract.Id, d.Type, d.Overdraft);
            db.Diners.Add(diner);
            db.Badges.Add(badge);
            db.Accounts.Add(account);
            if (d.InitialTopUp > 0)
            {
                db.AccountMovements.Add(account.Post(new MovementRequest(MovementType.TopUp, d.InitialTopUp, Id($"topup:{number}"), now)
                {
                    PaymentMethod = PaymentMethod.BankTransfer,
                    PerformedBy = "seed",
                    Comment = "Solde initial de démonstration",
                }));
            }
        }
    }

    /// <summary>Deterministic name-based identifier (not a security primitive).</summary>
    public static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("newrest-pos-demo:" + name)).AsSpan(0, 16));

    private static readonly string[] SelfMenu =
    [
        "CSC-VND", "CSC-PLT", "CSC-LEG", "TAJ-PLT", "TAJ-KFT", "POI-GRL", "LEG-VEG",
        "SAL-MAR", "HAR-SOU", "ZAA-LUK", "PAIN", "RIZ-BLC",
        "FRU-SAI", "YAO-NAT", "FLA-CAR", "EAU-50", "SOD-33", "JUS-ORA",
    ];

    private static readonly string[] SnackMenu =
    [
        "PAS-BOL", "BRO-PLT", "PST-PLT", "SAL-CES", "BRI-VND", "FRITES",
        "SAL-FRU", "YAO-NAT", "EAU-50", "EAU-150", "SOD-33", "JUS-ORA", "THE-MEN", "CAF-EXP",
    ];

    private sealed record DemoArticle(string Code, string Name, string Category, decimal Price, decimal Vat, string Visual, bool Subsidizable = true);

    // VAT rates are placeholders to be validated by the chartered accountant (see docs/assumptions.md).
    private static readonly DemoArticle[] Articles =
    [
        new("CSC-VND", "Couscous viande", "PLAT", 42.00m, 0.10m,
            "Semoule jaune en dôme, gros morceaux de viande rouge (bœuf/agneau) brun foncé, légumes (carottes, courgettes, navets, chou), pois chiches; pas de volaille."),
        new("CSC-PLT", "Couscous poulet", "PLAT", 38.00m, 0.10m,
            "Semoule jaune en dôme, cuisse ou morceaux de poulet à peau dorée/jaune clair avec os, légumes (carottes, courgettes, navets), pois chiches."),
        new("CSC-LEG", "Couscous légumes", "PLAT", 30.00m, 0.10m,
            "Semoule jaune en dôme avec uniquement des légumes (carottes, courgettes, citrouille orange, chou) et pois chiches, sans viande."),
        new("TAJ-PLT", "Tajine poulet citron olives", "PLAT", 38.00m, 0.10m,
            "Poulet en sauce jaune safranée, quartiers de citron confit, olives vertes ou violettes."),
        new("TAJ-KFT", "Tajine kefta aux œufs", "PLAT", 35.00m, 0.10m,
            "Petites boulettes de viande hachée dans une sauce tomate rouge, œufs entiers au plat sur le dessus."),
        new("TAJ-VND", "Tajine viande aux pruneaux", "PLAT", 45.00m, 0.10m,
            "Morceaux de viande rouge en sauce brune sucrée, pruneaux noirs, amandes et graines de sésame."),
        new("POI-GRL", "Poisson grillé", "PLAT", 45.00m, 0.10m,
            "Filet ou poisson entier grillé avec marques de grill, rondelle de citron, légumes vapeur."),
        new("PAS-BOL", "Pâtes bolognaise", "PLAT", 32.00m, 0.10m,
            "Spaghetti ou penne avec sauce tomate à la viande hachée, fromage râpé possible."),
        new("RIZ-PLT", "Riz au poulet", "PLAT", 34.00m, 0.10m,
            "Riz blanc ou jaune avec morceaux de poulet en sauce."),
        new("PST-PLT", "Pastilla au poulet", "PLAT", 40.00m, 0.10m,
            "Feuilleté rond doré saupoudré de sucre glace et de cannelle en croisillons."),
        new("BRO-PLT", "Brochettes de poulet", "PLAT", 36.00m, 0.10m,
            "Deux ou trois brochettes de cubes de poulet grillés sur pique, servies avec frites ou riz."),
        new("LEG-VEG", "Plat végétarien du jour", "PLAT", 28.00m, 0.10m,
            "Assiette de légumes cuisinés et légumineuses (lentilles, haricots blancs), sans viande."),
        new("SAL-MAR", "Salade marocaine", "ENTREE", 10.00m, 0.10m,
            "Petit bol de tomates et concombres coupés en dés, oignon, persil."),
        new("SAL-CES", "Salade César", "ENTREE", 18.00m, 0.10m,
            "Laitue romaine, croûtons, lamelles de poulet, copeaux de parmesan, sauce blanche."),
        new("HAR-SOU", "Harira", "ENTREE", 8.00m, 0.10m,
            "Bol de soupe épaisse rouge-orangé avec lentilles et pois chiches."),
        new("ZAA-LUK", "Zaalouk", "ENTREE", 10.00m, 0.10m,
            "Petit bol de purée d'aubergines et tomates, couleur rouge-brun, filet d'huile d'olive."),
        new("BRI-VND", "Briouates viande (3 pièces)", "ENTREE", 12.00m, 0.10m,
            "Trois triangles feuilletés dorés et frits."),
        new("PAIN", "Pain", "ACCOMP", 1.50m, 0.00m,
            "Pain rond marocain (khobz) ou demi-baguette."),
        new("FRITES", "Frites", "ACCOMP", 8.00m, 0.10m,
            "Portion de frites dorées."),
        new("RIZ-BLC", "Riz blanc", "ACCOMP", 6.00m, 0.10m,
            "Portion de riz blanc nature."),
        new("FRU-SAI", "Fruit de saison", "DESSERT", 5.00m, 0.10m,
            "Un fruit entier : orange, pomme, banane ou poire."),
        new("YAO-NAT", "Yaourt nature", "DESSERT", 4.00m, 0.10m,
            "Pot de yaourt blanc individuel avec opercule."),
        new("FLA-CAR", "Flan caramel", "DESSERT", 8.00m, 0.10m,
            "Flan jaune pâle nappé de caramel brun dans un ramequin ou pot transparent."),
        new("SAL-FRU", "Salade de fruits", "DESSERT", 10.00m, 0.10m,
            "Coupe de fruits frais coupés en dés, multicolore."),
        new("EAU-50", "Eau minérale 50 cl", "BOISSON", 5.00m, 0.20m,
            "Petite bouteille plastique transparente d'eau, bouchon bleu ou blanc.", Subsidizable: false),
        new("EAU-150", "Eau minérale 1,5 L", "BOISSON", 8.00m, 0.20m,
            "Grande bouteille plastique transparente d'eau.", Subsidizable: false),
        new("SOD-33", "Soda 33 cl", "BOISSON", 8.00m, 0.20m,
            "Canette métallique de soda 33 cl.", Subsidizable: false),
        new("JUS-ORA", "Jus d'orange frais", "BOISSON", 12.00m, 0.10m,
            "Verre ou gobelet de jus orange opaque."),
        new("THE-MEN", "Thé à la menthe", "BOISSON", 6.00m, 0.10m,
            "Verre à thé marocain avec thé ambré et feuilles de menthe."),
        new("CAF-EXP", "Café espresso", "BOISSON", 7.00m, 0.10m,
            "Petite tasse de café noir avec crème."),
    ];

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo data already present, nothing to do")]
    private static partial void LogAlreadySeeded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo data seeded: {ArticleCount} articles, {PointOfSaleCount} points of sale")]
    private static partial void LogSeeded(ILogger logger, int articleCount, int pointOfSaleCount);
}
