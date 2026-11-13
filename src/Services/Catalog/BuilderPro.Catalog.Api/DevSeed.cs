using BuilderPro.Catalog.Api.Domain;

namespace BuilderPro.Catalog.Api;

/// <summary>
/// LOCAL DEVELOPMENT ONLY: a few products per test tenant, so the catalog isn't empty on first
/// run. Products come from Catalog:DevProducts in appsettings.Development.json. Refuses to run
/// in Production.
/// </summary>
public static class DevSeed
{
    public static async Task RunAsync(WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Catalog:SeedDevProducts")) return;
        if (app.Environment.IsProduction())
        {
            throw new InvalidOperationException("Catalog:SeedDevProducts must never be enabled in Production.");
        }

        var products = app.Configuration.GetSection("Catalog:DevProducts").Get<SeedProduct[]>() ?? [];
        var store = app.Services.GetRequiredService<IProductStore>();
        // Fail fast: a seed product that breaks its category's rules stops startup, with every problem listed.
        var problems = products
            .SelectMany(p => Categories.Validate(p.Category, p.Attributes).Select(e => $"{p.TenantId}/{p.Sku}: {e}"))
            .ToArray();
        if (problems.Length > 0)
        {
            throw new InvalidOperationException("Invalid Catalog:DevProducts:\n" + string.Join("\n", problems));
        }

        foreach (var p in products)
        {
            await store.UpsertAsync(new Product(p.TenantId, p.Sku, p.Name, p.Description, p.Unit, p.ListPrice, p.Currency, Active: true,
                p.Category, p.ImageUrl, p.Attributes), CancellationToken.None);
        }
        app.Logger.LogInformation("Seeded {Count} development products", products.Length);

        // 2n-a price zones: every ZIP must be valid, and prices derive from the seeded list prices.
        var zones = app.Configuration.GetSection("Catalog:DevZones").Get<SeedZone[]>() ?? [];
        var badZips = zones.SelectMany(z => z.Zips.Where(zip => Zip.Normalize(zip) != zip).Select(zip => $"{z.TenantId}/{z.ZoneId}: bad ZIP '{zip}'")).ToArray();
        if (badZips.Length > 0) throw new InvalidOperationException("Invalid Catalog:DevZones:\n" + string.Join("\n", badZips));

        var zoneStore = app.Services.GetRequiredService<IPriceZoneStore>();
        foreach (var z in zones)
        {
            await zoneStore.UpsertZoneAsync(new PriceZone(z.TenantId, z.ZoneId, z.Name, z.Zips), CancellationToken.None);
            foreach (var p in products.Where(p => p.TenantId == z.TenantId && !z.NotSold.Contains(p.Sku)))
            {
                var price = Math.Round(p.ListPrice * z.PriceFactor, 2, MidpointRounding.AwayFromZero);
                await zoneStore.UpsertPriceAsync(new ZonePrice(z.TenantId, p.Sku, z.ZoneId, price), CancellationToken.None);
            }
        }
        app.Logger.LogInformation("Seeded {Count} development price zones", zones.Length);
    }

    public sealed class SeedZone
    {
        public string TenantId { get; set; } = "";
        public string ZoneId { get; set; } = "";
        public string Name { get; set; } = "";
        public decimal PriceFactor { get; set; } = 1m;
        public string[] Zips { get; set; } = [];
        public string[] NotSold { get; set; } = [];
    }

    public sealed class SeedProduct
    {
        public string TenantId { get; set; } = "";
        public string Sku { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string Unit { get; set; } = "each";
        public decimal ListPrice { get; set; }
        public string Currency { get; set; } = "USD";
        public string Category { get; set; } = "other";
        public string? ImageUrl { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = [];
    }
}
