using BuilderPro.Catalog.Api.Domain;
using BuilderPro.Security;

namespace BuilderPro.Catalog.Api;

public sealed record ProductDto(
    string Sku, string Name, string? Description, string Unit, decimal ListPrice, string Currency,
    string Category, string? ImageUrl, IReadOnlyDictionary<string, string> Attributes);

public sealed record AttributeDefinitionDto(string Key, string Label, string Kind, bool Required, IReadOnlyList<string>? Options, string? Unit);
public sealed record CategoryDto(string Id, string Name, string DefaultUnit, string Scene, IReadOnlyList<AttributeDefinitionDto> Attributes);

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/catalog").RequireAuthorization();

        api.MapGet("/categories", () => Results.Ok(Categories.All.Select(ToDto)))
            .WithSummary("The platform's product categories and their attribute definitions (2h).");
        api.MapGet("/products", ListAsync)
            .WithSummary("Active products of the caller's tenant (tenant resolved by the Identity service).");
        api.MapGet("/products/{sku}", GetAsync)
            .WithSummary("One active product of the caller's tenant. Another tenant's SKU is a plain 404.");

        return app;
    }

    internal static async Task<IResult> ListAsync(string? category, HttpContext http, ITenantLookup tenants, IProductStore store, CancellationToken ct)
    {
        if (category is not null && Categories.Find(category) is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "unknown_category", "category is not a known category id (GET /api/catalog/categories).");
        }

        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return Problem(tenant.Status, tenant.Code, "Could not determine your tenant.");

        // Filtered in memory on purpose: the tenant's whole list is ONE cache entry (2f).
        var products = await store.ListActiveAsync(tenant.TenantId!, ct);
        return Results.Ok(products.Where(p => category is null || p.Category == category).Select(ToDto));
    }

    internal static async Task<IResult> GetAsync(string sku, HttpContext http, ITenantLookup tenants, IProductStore store, CancellationToken ct)
    {
        if (!ClaimsNormalizer.IsIdentifier(sku)) return Problem(StatusCodes.Status400BadRequest, "invalid_sku", "sku is not a valid identifier.");

        var tenant = await tenants.ResolveAsync(http, ct);
        if (!tenant.Resolved) return Problem(tenant.Status, tenant.Code, "Could not determine your tenant.");

        var product = await store.FindActiveAsync(tenant.TenantId!, sku, ct);
        return product is null ? Results.NotFound() : Results.Ok(ToDto(product));
    }

    // TenantId is deliberately NOT in the DTO: the caller already knows their tenant, and
    // nothing in a response should invite a client to send it back as input.
    private static ProductDto ToDto(Product p) =>
        new(p.Sku, p.Name, p.Description, p.Unit, p.ListPrice, p.Currency, p.Category, p.ImageUrl, p.AttributeValues);

    private static CategoryDto ToDto(CategoryDefinition c) => new(c.Id, c.Name, c.DefaultUnit, c.Scene,
        [.. c.Attributes.Select(a => new AttributeDefinitionDto(a.Key, a.Label, a.Kind.ToString().ToLowerInvariant(), a.Required, a.Options, a.Unit))]);

    // RFC 9457 problem+json with the same stable "code" extension the Identity service uses.
    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
