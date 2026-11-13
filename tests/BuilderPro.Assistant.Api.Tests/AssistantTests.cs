using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuilderPro.Assistant.Api;
using BuilderPro.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuilderPro.Assistant.Api.Tests;

public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
    : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    public const string SchemeName = "Test";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub)) return Task.FromResult(AuthenticateResult.NoResult());
        var id = new ClaimsIdentity([new Claim("iss", "http://idp.test/realms/builderpro"), new Claim("sub", sub.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), SchemeName)));
    }
}

public sealed class FakeTenants : ITenantLookup
{
    public Task<TenantLookup> ResolveAsync(HttpContext http, CancellationToken ct) =>
        Task.FromResult(http.User.FindFirst("sub")!.Value == "erin"
            ? TenantLookup.Fail(StatusCodes.Status403Forbidden, "no_membership")
            : TenantLookup.Ok("t-acme", ["quotes.read"], "c-alice"));
}

/// <summary>Stands in for Catalog and Quotes.</summary>
public sealed class FakePlatform : IPlatformApi
{
    public List<string> Calls { get; } = [];

    public Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        Calls.Add("GET " + path);
        return Task.FromResult(JsonSerializer.SerializeToElement(new[]
        {
            new { sku = "CT-QTZ", name = "Quartz countertop, Calacatta white", unit = "sqft", listPrice = 89.00m, currency = "USD", category = "countertops" },
            new { sku = "CT-GRN", name = "Granite countertop, Absolute black", unit = "sqft", listPrice = 72.00m, currency = "USD", category = "countertops" },
        }));
    }

    public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct)
    {
        Calls.Add("POST " + path);
        return Task.FromResult(JsonSerializer.SerializeToElement(new[]
        {
            new { sku = "CT-QTZ", quantity = 30, unitPrice = 89.00m, subtotal = 2670.00m, taxRate = 0.0825m, tax = 220.28m, total = 2890.28m, currency = "USD" },
        }));
    }
}

/// <summary>
/// A scripted model: on the first call it asks for <see cref="Tool"/>; once it has seen a tool
/// result it answers with <see cref="FinalText"/>. Lets the tests run the real Agent Framework loop.
/// </summary>
public sealed class ScriptedModel : IChatClient
{
    public string Tool { get; set; } = "search_products";
    public Dictionary<string, object?> ToolArgs { get; set; } = new() { ["category"] = "countertops" };
    public string FinalText { get; set; } = "";
    public int Calls { get; private set; }
    public IList<ChatMessage> LastMessages { get; private set; } = [];
    public string? LastInstructions { get; private set; }

    /// <summary>When set, every model call fails with this HTTP status (as the OpenAI SDK does).</summary>
    public int? FailWith { get; set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (FailWith is int status) throw new ClientResultException("model failed", new StubResponse(status));
        LastMessages = [.. messages];
        LastInstructions = options?.Instructions;
        var sawToolResult = LastMessages.Any(m => m.Contents.OfType<FunctionResultContent>().Any());
        var reply = sawToolResult || Tool.Length == 0
            ? new ChatMessage(ChatRole.Assistant, FinalText)
            : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", Tool, ToolArgs)]);
        return Task.FromResult(new ChatResponse(reply));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>The smallest PipelineResponse that carries a status code into a ClientResultException.</summary>
public sealed class StubResponse(int status) : PipelineResponse
{
    public override int Status => status;
    public override string ReasonPhrase => "";
    public override Stream? ContentStream { get; set; }
    public override BinaryData Content => BinaryData.Empty;
    protected override PipelineResponseHeaders HeadersCore => throw new NotSupportedException();
    public override BinaryData BufferContent(CancellationToken cancellationToken = default) => BinaryData.Empty;
    public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(BinaryData.Empty);
    public override void Dispose() { }
}

public sealed class AssistantFactory : WebApplicationFactory<Program>
{
    public ScriptedModel Model { get; } = new();
    public FakePlatform Platform { get; } = new();
    public bool Configured { get; set; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:Authority", "http://idp.test/realms/builderpro");
        builder.UseSetting("Auth:RequireHttpsMetadata", "false");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<ITenantLookup>();
            s.AddSingleton<ITenantLookup, FakeTenants>();
            s.RemoveAll<IPlatformApi>();
            s.AddSingleton<IPlatformApi>(Platform);
            s.RemoveAll<IAssistantChatClient>();
            s.AddSingleton<IAssistantChatClient>(_ => new AssistantChatClient(Configured ? Model : null));
            s.AddAuthentication(a => { a.DefaultAuthenticateScheme = TestAuthHandler.SchemeName; a.DefaultChallengeScheme = TestAuthHandler.SchemeName; })
             .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    public HttpClient As(string sub)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-Sub", sub);
        return c;
    }
}

public sealed class AssistantTests
{
    private static async Task<(HttpResponseMessage Http, ChatReply? Reply)> Ask(AssistantFactory f, string text, string sub = "alice")
    {
        var r = await f.As(sub).PostAsJsonAsync("/api/assistant/chat", new { messages = new[] { new { role = "user", content = text } } });
        return (r, r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<ChatReply>() : null);
    }

    [Fact]
    public async Task The_agent_calls_a_tool_then_answers_with_a_price_the_tool_returned()
    {
        using var f = new AssistantFactory();
        f.Model.FinalText = "The Calacatta quartz is $89.00 per sq ft; Absolute black granite is $72.00.";

        var (http, reply) = await Ask(f, "What quartz countertops do you have?");

        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        Assert.False(reply!.Guarded);
        Assert.Contains("$89.00", reply.Reply);
        Assert.Equal(["search_products"], reply.ToolsUsed);
        Assert.Contains("GET /api/catalog/products?category=countertops", f.Platform.Calls);
        Assert.Equal(2, f.Model.Calls);   // tool call, then the answer with the tool's result
    }

    [Fact]
    public async Task A_price_no_tool_returned_is_never_delivered()
    {
        using var f = new AssistantFactory();
        f.Model.FinalText = "Quartz is $89.00 per sq ft, so 30 sq ft comes to about $2,600.";   // the model did maths

        var (_, reply) = await Ask(f, "How much for 30 sq ft of quartz?");

        Assert.True(reply!.Guarded);
        Assert.Equal(AssistantEndpoints.GuardedReply, reply.Reply);
        Assert.DoesNotContain("2,600", reply.Reply);
    }

    [Fact]
    public async Task Totals_from_price_products_may_be_quoted_exactly()
    {
        using var f = new AssistantFactory();
        f.Model.Tool = "price_products";
        f.Model.ToolArgs = new() { ["items"] = new[] { new { sku = "CT-QTZ", quantity = 30 } } };
        f.Model.FinalText = "30 sq ft of Calacatta quartz is $2,670.00 plus $220.28 tax: $2,890.28 in total.";

        var (_, reply) = await Ask(f, "Price 30 sq ft of the Calacatta quartz");

        Assert.False(reply!.Guarded);
        Assert.Contains("$2,890.28", reply.Reply);
        Assert.Contains("POST /api/quotes/estimates", f.Platform.Calls);
    }

    [Fact]
    public async Task Without_any_tool_call_any_price_is_blocked()
    {
        using var f = new AssistantFactory();
        f.Model.Tool = "";
        f.Model.FinalText = "Countertops usually cost around $60 per sq ft.";

        var (_, reply) = await Ask(f, "Roughly what do countertops cost?");

        Assert.True(reply!.Guarded);
    }

    [Fact]
    public async Task Answers_without_money_pass_through()
    {
        using var f = new AssistantFactory();
        f.Model.Tool = "";
        f.Model.FinalText = "Quartz is non-porous and easy to keep clean; granite needs sealing once a year.";

        var (_, reply) = await Ask(f, "Quartz or granite for a busy kitchen?");

        Assert.False(reply!.Guarded);
        Assert.Empty(reply.ToolsUsed);
    }

    [Fact]
    public async Task The_agent_gets_the_house_rules_as_its_instructions()
    {
        using var f = new AssistantFactory();
        f.Model.Tool = "";
        f.Model.FinalText = "Hello!";

        await Ask(f, "Hi");

        // The Agent Framework passes them as ChatOptions.Instructions (older versions: a system message).
        var system = f.Model.LastInstructions + "\n" + string.Join("\n", f.Model.LastMessages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        Assert.Contains("NEVER calculate", system);
        Assert.Contains("BEFORE tax", system);
        Assert.Contains("Never offer to", system);
    }

    [Fact]
    public async Task Not_configured_is_503_not_a_crash()
    {
        using var f = new AssistantFactory { Configured = false };
        var (http, _) = await Ask(f, "Hi");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, http.StatusCode);
    }

    [Theory]
    [InlineData(429, HttpStatusCode.ServiceUnavailable, "assistant_busy")]
    [InlineData(401, HttpStatusCode.BadGateway, "assistant_model_error")]
    public async Task Model_failures_are_named_not_a_500(int modelStatus, HttpStatusCode expected, string code)
    {
        using var f = new AssistantFactory();
        f.Model.FailWith = modelStatus;
        var (http, _) = await Ask(f, "Which countertops do you have?");
        Assert.Equal(expected, http.StatusCode);
        Assert.Contains(code, await http.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Non_members_are_refused_before_the_model_is_called()
    {
        using var f = new AssistantFactory();
        var (http, _) = await Ask(f, "Hi", sub: "erin");

        Assert.Equal(HttpStatusCode.Forbidden, http.StatusCode);
        Assert.Equal(0, f.Model.Calls);
    }

    [Theory]
    [InlineData("assistant")]   // must end with the user's message
    [InlineData("system")]      // callers can't inject system prompts
    public async Task Malformed_conversations_are_400(string lastRole)
    {
        using var f = new AssistantFactory();
        var r = await f.As("alice").PostAsJsonAsync("/api/assistant/chat", new { messages = new[] { new { role = lastRole, content = "x" } } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        using var f = new AssistantFactory();
        var r = await f.CreateClient().PostAsJsonAsync("/api/assistant/chat", new { messages = new[] { new { role = "user", content = "Hi" } } });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}

public sealed class PriceGuardTests
{
    [Theory]
    [InlineData("It is $89.00 per sq ft", 89.00)]
    [InlineData("Total: $2,890.28", 2890.28)]
    [InlineData("about $ 60", 60)]
    [InlineData("costs USD 12.5", 12.5)]
    [InlineData("12.50 dollars each", 12.50)]
    [InlineData("1,461.38 USD", 1461.38)]
    public void Finds_money_in_common_formats(string text, double amount)
    {
        Assert.Equal([(decimal)amount], PriceGuard.AmountsIn(text));
    }

    [Fact]
    public void Plain_numbers_are_not_money()
    {
        Assert.Empty(PriceGuard.AmountsIn("A 24-square roof needs 74 bundles and 3 rolls at 6/12 pitch."));
    }

    [Fact]
    public void Only_amounts_from_tool_results_are_allowed()
    {
        var ledger = new PriceLedger();
        ledger.Record("price_products", JsonSerializer.SerializeToElement(new { subtotal = 2670.00m, tax = 220.28m, total = 2890.28m, taxRate = 0.0825m, quantity = 30 }));

        Assert.Empty(PriceGuard.Unbacked("$2,670.00 + $220.28 = $2,890.28", ledger.Amounts));
        Assert.Equal([30m], PriceGuard.Unbacked("That's $30 off!", ledger.Amounts));   // quantity isn't a price
    }
}

public sealed class AssistantModelTests
{
    private static Microsoft.Extensions.Configuration.IConfiguration Config(params (string K, string V)[] kv) =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.K, x.V))).Build();

    [Fact]
    public void Foundry_needs_endpoint_model_and_key()
    {
        Assert.NotNull(AssistantModel.Create(Config(("Assistant:Endpoint", "https://r.services.ai.azure.com/api/projects/p"), ("Assistant:Deployment", "gpt-4o"), ("Assistant:ApiKey", "k"))));
        Assert.Null(AssistantModel.Create(Config(("Assistant:Endpoint", "https://r.services.ai.azure.com/"), ("Assistant:Deployment", "gpt-4o"))));
        Assert.Null(AssistantModel.Create(Config()));
    }

    [Fact]
    public void A_local_openai_compatible_server_needs_no_key()
    {
        Assert.NotNull(AssistantModel.Create(Config(("Assistant:Provider", "openai-compatible"), ("Assistant:Endpoint", "http://localhost:11434/v1"), ("Assistant:Deployment", "qwen2.5:7b"))));
    }
}
