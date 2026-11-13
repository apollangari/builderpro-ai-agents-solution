using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuilderPro.Catalog.Api.Domain;

namespace BuilderPro.Catalog.Api.Tests;

public sealed class CategoryRulesTests
{
    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    /// <summary>
    /// The repo root, found from two starting points because neither works everywhere:
    ///  - the test's output folder: inside the repo on CI, but outside when built with --artifacts-path;
    ///  - this source file's path: real locally, but rewritten to "/_/..." by ContinuousIntegrationBuild on CI.
    /// </summary>
    private static DirectoryInfo? RepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(ThisFile()) ?? "" })
        {
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "BuilderPro.slnx"))) return dir;
            }
        }
        return null;
    }

    private static Dictionary<string, string> A(params (string K, string V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public void Category_ids_are_unique_and_every_one_has_a_scene()
    {
        Assert.Equal(Categories.All.Count, Categories.All.Select(c => c.Id).Distinct().Count());
        Assert.All(Categories.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Scene)));
        Assert.Contains(Categories.All, c => c.Id == "countertops");
        Assert.Contains(Categories.All, c => c.Id == "roofing");
        Assert.Contains(Categories.All, c => c.Id == "sheds");
    }

    [Fact]
    public void Every_scene_is_one_the_web_visualizer_draws()
    {
        // Keep in sync with SCENES in web/src/app/features/visualize/scene.ts ("none" = no scene).
        string[] drawn = ["kitchen", "house-roof", "backyard", "room-floor", "room-wall", "house-front", "none"];
        Assert.All(Categories.All, c => Assert.Contains(c.Scene, drawn));
        Assert.All(Categories.All.Where(c => c.Id is not ("labour" or "other")), c => Assert.NotEqual("none", c.Scene));
    }

    [Fact]
    public void A_complete_valid_product_has_no_errors()
    {
        Assert.Empty(Categories.Validate("countertops", A(("material", "quartz"), ("color", "White"), ("finish", "polished"), ("thicknessCm", "3"))));
    }

    [Theory]
    [InlineData("countertops", "material", "concrete", "must be one of")]   // not an option
    [InlineData("countertops", "thicknessCm", "thick", "must be a number")]
    [InlineData("countertops", "flavour", "vanilla", "not an attribute")]
    public void Bad_attribute_values_are_reported(string category, string key, string value, string expected)
    {
        var attrs = A(("material", "quartz"), ("color", "White"));
        attrs[key] = value;
        Assert.Contains(Categories.Validate(category, attrs), e => e.Contains(expected));
    }

    [Fact]
    public void Missing_required_attributes_and_unknown_categories_are_reported()
    {
        Assert.Contains(Categories.Validate("sheds", A(("material", "wood"))), e => e.Contains("'size' is required"));
        Assert.Equal(["unknown category 'spaceships'"], Categories.Validate("spaceships", A()));
    }

    /// <summary>Guards the dev seed: a typo there would otherwise only show up when the AppHost starts.</summary>
    [Fact]
    public void Every_seeded_product_is_valid_and_has_an_image_unless_labour()
    {
        var dir = RepoRoot();
        Assert.NotNull(dir);
        var file = Path.Combine(dir!.FullName, "src", "Services", "Catalog", "BuilderPro.Catalog.Api", "appsettings.Development.json");
        using var json = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

        var problems = new List<string>();
        foreach (var p in json.RootElement.GetProperty("Catalog").GetProperty("DevProducts").EnumerateArray())
        {
            var sku = p.GetProperty("Sku").GetString();
            var category = p.GetProperty("Category").GetString()!;
            var attrs = p.GetProperty("Attributes").EnumerateObject().ToDictionary(o => o.Name, o => o.Value.GetString()!);
            problems.AddRange(Categories.Validate(category, attrs).Select(e => $"{sku}: {e}"));
            if (category != "labour" && !p.TryGetProperty("ImageUrl", out _)) problems.Add($"{sku}: no ImageUrl");
            // Every image must exist in web/public (regenerate with: node web/scripts/generate-product-images.mjs).
            if (p.TryGetProperty("ImageUrl", out var img) &&
                !File.Exists(Path.Combine(dir.FullName, "web", "public", img.GetString()!.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))))
            {
                problems.Add($"{sku}: image file {img.GetString()} is missing");
            }
        }
        Assert.Empty(problems);
    }
}

public sealed class CategoryApiTests(CatalogFactory f) : IClassFixture<CatalogFactory>, IAsyncLifetime
{
    private readonly CatalogFactory _f = f;

    public async Task InitializeAsync()
    {
        await _f.Store.UpsertAsync(new Product("t-acme", "CT-1", "Quartz", null, "sqft", 89m, "USD", true, "countertops", "/products/ct-1.svg",
            new Dictionary<string, string> { ["material"] = "quartz", ["color"] = "White" }), default);
        await _f.Store.UpsertAsync(new Product("t-acme", "SHED-1", "Shed", null, "each", 2899m, "USD", true, "sheds", "/products/shed-1.svg",
            new Dictionary<string, string> { ["material"] = "wood", ["size"] = "8 x 10 ft" }), default);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Categories_endpoint_lists_definitions_with_attributes()
    {
        var cats = await _f.As("alice").GetFromJsonAsync<CategoryDto[]>("/api/catalog/categories");

        var ct = cats!.Single(c => c.Id == "countertops");
        Assert.Equal("kitchen", ct.Scene);
        var material = ct.Attributes.Single(a => a.Key == "material");
        Assert.Equal("choice", material.Kind);
        Assert.True(material.Required);
        Assert.Contains("quartz", material.Options!);
    }

    [Fact]
    public async Task Products_can_be_filtered_by_category_and_carry_attributes_and_image()
    {
        var sheds = await _f.As("alice").GetFromJsonAsync<ProductDto[]>("/api/catalog/products?category=sheds");

        var shed = Assert.Single(sheds!);
        Assert.Equal("SHED-1", shed.Sku);
        Assert.Equal("sheds", shed.Category);
        Assert.Equal("/products/shed-1.svg", shed.ImageUrl);
        Assert.Equal("8 x 10 ft", shed.Attributes["size"]);
    }

    [Fact]
    public async Task Unknown_category_is_400_before_any_tenant_work()
    {
        var r = await _f.As("alice").GetAsync("/api/catalog/products?category=spaceships");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task A_category_filter_never_crosses_tenants()
    {
        var carol = await _f.As("carol").GetFromJsonAsync<ProductDto[]>("/api/catalog/products?category=sheds");
        Assert.DoesNotContain(carol!, p => p.Sku == "SHED-1");
    }
}
