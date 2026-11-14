using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.AI;

namespace BuilderPro.Assistant.Api.Tests;

/// <summary>Gap fix: the product assistant (3a) prices for the customer's ZIP, like the guided quote.</summary>
public sealed class ZipPricingTests
{
    [Fact]
    public async Task The_assistant_is_told_the_ZIP_and_prices_with_it()
    {
        using var f = new AssistantFactory();
        f.Platform.Responses["POST /api/quotes/estimates"] = new { items = new[] { new { sku = "CT-QTZ", total = 2671.43m } } };
        f.Model.Tool = "price_products";
        f.Model.ToolArgs = new()
        {
            ["items"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { new { sku = "CT-QTZ", quantity = 30 } }),
            ["zip"] = "75078",
        };
        f.Model.FinalText = "30 sq ft of quartz is $2,671.43 with tax.";

        var r = await f.As("alice").PostAsJsonAsync("/api/assistant/chat", new { messages = new[] { new { role = "user", content = "Price 30 sq ft of quartz" } }, accountZip = "75078" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(f.Model.LastMessages, m => m.Role == ChatRole.System && m.Text.Contains("Customer's project ZIP: 75078"));
        Assert.Contains("\"zip\":\"75078\"", f.Platform.Bodies[^1]);
        Assert.Contains("Always pass the customer's ZIP", f.Model.LastInstructions);
    }

    [Fact]
    public async Task Without_a_ZIP_the_assistant_is_told_to_ask_for_it()
    {
        using var f = new AssistantFactory();
        f.Model.Tool = "list_categories";
        f.Model.ToolArgs = new();
        f.Model.FinalText = "Which ZIP is the project in?";
        await f.As("alice").PostAsJsonAsync("/api/assistant/chat", new { messages = new[] { new { role = "user", content = "How much is quartz?" } } });
        Assert.Contains(f.Model.LastMessages, m => m.Role == ChatRole.System && m.Text.Contains("unknown (ask before quoting a price)"));
    }
}
