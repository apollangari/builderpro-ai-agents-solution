using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BuilderPro.Assistant.Api.Tests;

/// <summary>3c: an MCP client lists BuilderPro's tools and calls them as the signed-in customer.</summary>
public sealed class McpTests
{
    /// <summary>One JSON-RPC call over Streamable HTTP; the answer may come as JSON or as one SSE "data:" line.</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Result)> Rpc(HttpClient client, string method, object? @params = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/assistant/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return (response.StatusCode, default);
        var json = text.TrimStart().StartsWith('{')
            ? text
            : string.Join("", text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()));
        return (response.StatusCode, JsonDocument.Parse(json).RootElement.GetProperty("result").Clone());
    }

    [Fact]
    public async Task An_MCP_client_lists_the_read_and_price_tools_only()
    {
        using var f = new AssistantFactory();
        var (status, result) = await Rpc(f.As("alice"), "tools/list");
        Assert.Equal(HttpStatusCode.OK, status);
        var names = result.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?> { "check_zip", "list_project_types", "find_products", "get_project_steps", "price_draft", "list_my_projects", "get_project" }, names);
        Assert.Subset(new HashSet<string?> { "check_zip", "list_project_types", "find_products", "get_project_steps", "price_draft", "list_my_projects", "get_project" }, names);
        // Nothing that commits: no accept, pay, book or save.
        Assert.DoesNotContain(names, n => n!.Contains("accept") || n.Contains("pay") || n.Contains("save") || n.Contains("issue"));
    }

    [Fact]
    public async Task A_tool_call_reaches_the_platform_as_the_caller()
    {
        using var f = new AssistantFactory();
        f.Platform.Responses["GET /api/catalog/service-area"] = new { zip = "75078", served = true };
        var (status, result) = await Rpc(f.As("alice"), "tools/call", new { name = "check_zip", arguments = new { zip = "75078" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("GET /api/catalog/service-area?zip=75078", f.Platform.Calls);
        Assert.Contains("\"served\":true", result.GetProperty("content")[0].GetProperty("text").GetString()!.Replace(" ", ""));
    }

    [Fact]
    public async Task Anonymous_MCP_calls_are_refused()
    {
        using var f = new AssistantFactory();
        var (status, _) = await Rpc(f.CreateClient(), "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }
}
