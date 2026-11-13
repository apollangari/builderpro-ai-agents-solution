using BuilderPro.Catalog.Api.Domain;
using BuilderPro.Security;

namespace BuilderPro.Catalog.Api;

public sealed record ProductDto(
    string Sku, string Name, string? Description, string Unit, decimal ListPrice, string Currency,
    string Category, string? ImageUrl, IReadOnlyDictionary<string, string> Attributes);

public sealed record AttributeDefinitionDto(string Key, string Label, string Kind, bool Required, IReadOnlyList<string>? Options, string? Unit);
public sealed record TypicalSizeDto(decimal Quantity, string Description);
public sealed record ServiceAreaDto(string Zip, bool Served);
public sealed record CategoryDto(string Id, string Name, string DefaultUnit, string Scene, IReadOnlyList<AttributeDefinitionDto> Attributes, TypicalSizeDto? Typical, string Measure);

// 2n-c/d: a product's option steps at one ZIP's prices.
public sealed record OptionItemDto(string Sku, string Name, string Unit, decimal Price, string Currency, string? ImageUrl, decimal Included);
public sealed record OptionGroupDto(string Id, string Step, string Label, string? Help, string Selection, string Basis, bool Required, string? NoneLabel, IReadOnlyList<OptionItemDto> Options);
public sealed record ProductOptionsDto(ProductDto Product, string Measure, decimal? MinimumQuantity, IReadOnlyList<OptionGroupDto> Groups);

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/catalog").RequireAuthorization();

        api.MapGet("/categories", () => Results.Ok(Categories.All.Select(ToDto)))
            .WithSummary("The platform's product categories and their attribute definitions (2h).");
        api.MapGet("/service-area", ServiceAreaAsync)
            .WithSummary("Does the caller's store serve this ZIP? Yes/no only: zones and ZIP lists stay on the server (2n-a).");
        api.MapGet("/products", ListAsync)
            .WithSummary("Active products of the caller's tenant. With ?zip=, only products sold in that ZIP's zone, at that zone's price (2n-a).");
        api.MapGet("/products/{sku}/options", OptionsAsync)
            .WithSummary("A product's measurement form, minimum and option steps (add-ons, protection plans) at a ZIP's prices (2n-b..d).");
        api.MapGet("/products/{sku}", GetAsync)
            .WithSummary("One active product of the caller's tenant (with ?zip=, at that zone's price). Another tenant's SKU is a plain 404.");

        return app;
    }

    internal static async Task<IResult> ServiceAreaAsync(string? zip, HttpContext http, ITenantLookup tenants, IPriceZoneStore zones, CancellationToken ct)
    {
        var normalized = Zip.Normalize(zip);
        if (normalized is null) return Problem(StatusCodes.Status400BadRequest, "invalid_zip", "zip must be a US ZIP (12345) or a Canadian postal code (A1A 1A1).");

        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return Problem(tenant.Status, tenant.Code, "Could not determine your tenant.");

        var zone = await zones.ZoneForZipAsync(tenant.TenantId!, normalized, ct);
        return Results.Ok(new ServiceAreaDto(normalized, zone is not null));
    }

    internal static async Task<IResult> ListAsync(string? category, string? zip, HttpContext http, ITenantLookup tenants, IProductStore store, IPriceZoneStore zones, CancellationToken ct)
    {
        if (category is not null && Categories.Find(category) is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "unknown_category", "category is not a known category id (GET /api/catalog/categories).");
        }

        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return Problem(tenant.Status, tenant.Code, "Could not determine your tenant.");

        var (prices, problem) = await ZonePricesAsync(tenant.TenantId!, zip, zones, ct);
        if (problem is not null) return problem;

        // Filtered in memory on purpose: the tenant's whole list is ONE cache entry (2f).
        var products = await store.ListActiveAsync(tenant.TenantId!, ct);
        return Results.Ok(products
            .Where(p => category is null || p.Category == category)
            .Select(p => Priced(p, prices))
            .OfType<Product>()
            .Select(ToDto));
    }

    internal static async Task<IResult> GetAsync(string sku, string? zip, HttpContext http, ITenantLookup tenants, IProductStore store, IPriceZoneStore zones, CancellationToken ct)
    {
        if (!ClaimsNormalizer.IsIdentifier(sku)) return Problem(StatusCodes.Status400BadRequest, "invalid_sku", "sku is not a valid identifier.");

        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return Problem(tenant.Status, tenant.Code, "Could not determine your tenant.");

        var (prices, problem) = await ZonePricesAsync(tenant.TenantId!, zip, zones, ct);
        if (problem is not null) return problem;

        var product = await store.FindActiveAsync(tenant.TenantId!, sku, ct);
        var priced = product is null ? null : Priced(product, prices);
        return priced is null ? Results.NotFound() : Results.Ok(ToDto(priced));
    }

    internal static async Task<IResult> OptionsAsync(
        string sku, string? zip, HttpContext http, ITenantLookup tenants, IProductStore store, IPriceZoneStore zones, IOptionStore options, CancellationToken ct)
    {
        if (!ClaimsNormalizer.IsIdentifier(sku)) return Problem(StatusCodes.Status400BadRequest, "invalid_sku", "sku is not a valid identifier.");
        if (zip is null) return Problem(StatusCodes.Status400BadRequest, "invalid_zip", "Options are priced per ZIP: send ?zip=.");

        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return Problem(tenant.Status, tenant.Code, "Could not determine your tenant.");

        var (prices, problem) = await ZonePricesAsync(tenant.TenantId!, zip, zones, ct);
        if (problem is not null) return problem;

        var product = await store.FindActiveAsync(tenant.TenantId!, sku, ct);
        var priced = product is null ? null : Priced(product, prices);
        if (priced is null) return Results.NotFound();

        // Only steps that apply to this product, and only options with a price in this zone.
        var groups = (await options.ListGroupsAsync(tenant.TenantId!, priced.Category, ct))
            .Where(g => g.AppliesTo(priced))
            .Select(g => new OptionGroupDto(g.GroupId, g.Step, g.Label, g.Help, g.Selection, g.Basis, g.Required, g.NoneLabel,
                [.. g.Options
                    .Where(o => prices!.ContainsKey(o.Sku))
                    .Select(o => new OptionItemDto(o.Sku, o.Name, o.Unit, prices![o.Sku], priced.Currency, o.ImageUrl, o.Included))]))
            .Where(g => g.Options.Count > 0)
            .ToArray();

        var measure = Categories.Find(priced.Category)?.Measure ?? "count";
        return Results.Ok(new ProductOptionsDto(ToDto(priced), measure, await options.MinimumAsync(tenant.TenantId!, priced.Category, ct), groups));
    }

    /// <summary>
    /// No zip: list prices (staff tools, 2m). A zip: that zone's prices, or the problem to return
    /// (400 invalid_zip, 422 zip_not_served). The zone id itself never goes back to the caller.
    /// </summary>
    private static async Task<(IReadOnlyDictionary<string, decimal>? Prices, IResult? Problem)> ZonePricesAsync(
        string tenantId, string? zip, IPriceZoneStore zones, CancellationToken ct)
    {
        if (zip is null) return (null, null);
        var normalized = Zip.Normalize(zip);
        if (normalized is null) return (null, Problem(StatusCodes.Status400BadRequest, "invalid_zip", "zip must be a US ZIP (12345) or a Canadian postal code (A1A 1A1)."));
        var zone = await zones.ZoneForZipAsync(tenantId, normalized, ct);
        if (zone is null) return (null, Problem(StatusCodes.Status422UnprocessableEntity, "zip_not_served", "Your store doesn't serve this ZIP yet."));
        return (await zones.PricesAsync(tenantId, zone, ct), null);
    }

    /// <summary>The product at the zone's price; null when it has no price there (not sold in that zone).</summary>
    private static Product? Priced(Product p, IReadOnlyDictionary<string, decimal>? zonePrices) =>
        zonePrices is null ? p
        : zonePrices.TryGetValue(p.Sku, out var price) ? p with { ListPrice = price }
        : null;

    // TenantId is deliberately NOT in the DTO: the caller already knows their tenant, and
    // nothing in a response should invite a client to send it back as input.
    private static ProductDto ToDto(Product p) =>
        new(p.Sku, p.Name, p.Description, p.Unit, p.ListPrice, p.Currency, p.Category, p.ImageUrl, p.AttributeValues);

    private static CategoryDto ToDto(CategoryDefinition c) => new(c.Id, c.Name, c.DefaultUnit, c.Scene,
        [.. c.Attributes.Select(a => new AttributeDefinitionDto(a.Key, a.Label, a.Kind.ToString().ToLowerInvariant(), a.Required, a.Options, a.Unit))],
        c.Typical is null ? null : new TypicalSizeDto(c.Typical.Quantity, c.Typical.Description), c.Measure);

    // RFC 9457 problem+json with the same stable "code" extension the Identity service uses.
    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
