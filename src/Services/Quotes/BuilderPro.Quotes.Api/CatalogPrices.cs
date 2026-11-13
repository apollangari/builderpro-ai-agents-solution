using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Net.Http.Headers;

namespace BuilderPro.Quotes.Api;

/// <summary>A product as the Catalog service lists it: the ONLY source of a unit price.</summary>
public sealed record CatalogProduct(
    [property: JsonPropertyName("sku")] string Sku,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("listPrice")] decimal ListPrice,
    [property: JsonPropertyName("currency")] string Currency);

/// <summary>
/// What the catalog answered: the products by SKU, or why not. ZipProblem is the catalog's own code
/// ("invalid_zip", "zip_not_served") when the ZIP was refused; Products null without it = catalog down.
/// </summary>
public sealed record CatalogRead(IReadOnlyDictionary<string, CatalogProduct>? Products, string? ZipProblem = null)
{
    public static readonly CatalogRead Unavailable = new(Products: null);
}

/// <summary>The caller's tenant's products by SKU; with a ZIP, only those sold there, at that zone's price (2n-a).</summary>
public interface ICatalogPrices
{
    Task<CatalogRead> GetAsync(HttpContext http, string? zip, CancellationToken ct);
}

/// <summary>
/// Reads list prices from the Catalog service with the CALLER'S OWN token, so the catalog applies
/// its own tenant isolation: Quotes can only ever price with products the caller's tenant sells.
/// Any failure returns null and the request fails closed (503); we never price without the catalog.
/// </summary>
public sealed class CatalogPriceClient(HttpClient http, ILogger<CatalogPriceClient> log) : ICatalogPrices
{
    public async Task<CatalogRead> GetAsync(HttpContext context, string? zip, CancellationToken ct)
    {
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers[HeaderNames.Authorization], out var auth) || auth.Parameter is null)
        {
            return CatalogRead.Unavailable;
        }

        var url = string.IsNullOrWhiteSpace(zip) ? "/api/catalog/products" : $"/api/catalog/products?zip={Uri.EscapeDataString(zip)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Parameter);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if ((int)response.StatusCode is 400 or 422 && zip is not null)
            {
                // The catalog refused the ZIP: pass its reason on instead of "catalog down".
                var problem = await response.Content.ReadFromJsonAsync<CatalogProblem>(ct);
                if (problem?.Code is "invalid_zip" or "zip_not_served") return new CatalogRead(null, problem.Code);
            }
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Catalog answered {Status}", (int)response.StatusCode);
                return CatalogRead.Unavailable;
            }
            var products = await response.Content.ReadFromJsonAsync<CatalogProduct[]>(ct) ?? [];
            return new CatalogRead(products.ToDictionary(p => p.Sku, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false }
                                       or System.Text.Json.JsonException or ArgumentException)
        {
            log.LogWarning(ex, "Reading catalog prices failed");
            return CatalogRead.Unavailable;
        }
    }

    private sealed record CatalogProblem([property: JsonPropertyName("code")] string? Code);
}
