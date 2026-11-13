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
