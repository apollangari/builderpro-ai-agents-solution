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

    /// <summary>Answers by "VERB path" prefix, e.g. "POST /api/quotes/guided/preview"; anything else gets the defaults below.</summary>
    public Dictionary<string, object> Responses { get; } = [];
    public List<string> Bodies { get; } = [];

    private bool TryAnswer(string call, out JsonElement answer)
    {
        var hit = Responses.FirstOrDefault(r => call.StartsWith(r.Key, StringComparison.Ordinal));
        answer = hit.Key is null ? default : JsonSerializer.SerializeToElement(hit.Value);
        return hit.Key is not null;
    }

    public Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        Calls.Add("GET " + path);
        if (TryAnswer("GET " + path, out var answer)) return Task.FromResult(answer);
        return Task.FromResult(JsonSerializer.SerializeToElement(new[]
        {
            new { sku = "CT-QTZ", name = "Quartz countertop, Calacatta white", unit = "sqft", listPrice = 89.00m, currency = "USD", category = "countertops" },
            new { sku = "CT-GRN", name = "Granite countertop, Absolute black", unit = "sqft", listPrice = 72.00m, currency = "USD", category = "countertops" },
        }));
    }

    public Task<JsonElement> PutAsync(string path, object body, CancellationToken ct)
    {
        Calls.Add("PUT " + path);
        Bodies.Add(JsonSerializer.Serialize(body));
        return Task.FromResult(TryAnswer("PUT " + path, out var answer) ? answer : JsonSerializer.SerializeToElement(new { error = 404 }));
    }

    public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct)
    {
        Calls.Add("POST " + path);
        Bodies.Add(JsonSerializer.Serialize(body));
        if (TryAnswer("POST " + path, out var answer)) return Task.FromResult(answer);
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
    public IReadOnlyList<string> LastToolNames { get; private set; } = [];

    /// <summary>When set, every model call fails with this HTTP status (as the OpenAI SDK does).</summary>
    public int? FailWith { get; set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (FailWith is int status) throw new ClientResultException("model failed", new StubResponse(status));
        LastMessages = [.. messages];
        LastInstructions = options?.Instructions;
        LastToolNames = [.. (options?.Tools ?? []).Select(t => t.Name)];
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

/// <summary>3b: the concierge prices a draft through the guided preview and never saves.</summary>
public sealed class ConciergeTests
{
    private static readonly object Preview = new
    {
        measurement = new { kind = "countertop", values = new { sqft = 21.88m, edgeFt = 11m }, productBasis = "sqft" },
        lines = new object[]
        {
            new { sku = "CT-QTZ", name = "Quartz", unit = "sqft", quantity = 21.88m, unitPrice = 89.00m, lineTotal = 1947.32m, group = "product" },
            new { sku = "CT-QTZ-MIN", name = "Minimum charge (25 sq ft minimum)", unit = "sqft", quantity = 3.12m, unitPrice = 89.00m, lineTotal = 277.68m, group = "minimum" },
        },
        currency = "USD", subtotal = 2225.00m, taxRate = 0.0825m, tax = 183.56m, total = 2408.56m,
        missing = new[] { "ct-edge" }, complete = false,
    };

    private static AssistantFactory Factory(string finalText)
    {
        var f = new AssistantFactory();
        f.Platform.Responses["POST /api/quotes/guided/preview"] = Preview;
        f.Model.Tool = "price_draft";
        f.Model.ToolArgs = new()
        {
            ["zip"] = "62701",
            ["sku"] = "CT-QTZ",
            ["measurements"] = JsonSerializer.SerializeToElement(new { layout = "straight", countertop = new[] { new { widthFt = 10, widthIn = 6, depthIn = 25 } } }),
            ["choices"] = JsonSerializer.SerializeToElement(new[] { new { group = "ct-plan", sku = (string?)null } }),
        };
        f.Model.FinalText = finalText;
        return f;
    }

    private static Task<HttpResponseMessage> Talk(AssistantFactory f, string text, string sub = "alice", object? draft = null) =>
        f.As(sub).PostAsJsonAsync("/api/assistant/concierge", new { messages = new[] { new { role = "user", content = text } }, accountZip = "62701", draft });

    [Fact]
    public async Task The_concierge_prices_a_draft_converting_feet_and_inches_in_code()
    {
        using var f = Factory("That's 21.88 sq ft. With the 25 sq ft minimum your draft total is $2,408.56. Still to choose: edge style.");
        var r = await Talk(f, "10 ft 6 in by 25 inches, quartz please");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var reply = (await r.Content.ReadFromJsonAsync<ConciergeReply>())!;

        Assert.False(reply.Guarded);
        Assert.Equal(["price_draft"], reply.ToolsUsed);
        Assert.Contains("POST /api/quotes/guided/preview", f.Platform.Calls);
        Assert.Contains("\"widthIn\":126", f.Platform.Bodies.Single());   // 10 ft 6 in, converted by code, not the model
        Assert.Equal(2408.56m, reply.Quote!.Value.GetProperty("total").GetDecimal());
        Assert.Equal("CT-QTZ", reply.Draft!.Value.GetProperty("sku").GetString());
        Assert.DoesNotContain(f.Platform.Calls, c => c.StartsWith("POST /api/quotes/guided", StringComparison.Ordinal) && !c.Contains("preview"));   // never saves
    }

    [Fact]
    public async Task An_invented_total_is_blocked_but_the_priced_draft_still_reaches_the_page()
    {
        using var f = Factory("Your total comes to about $2,300.00.");
        var reply = (await (await Talk(f, "How much?")).Content.ReadFromJsonAsync<ConciergeReply>())!;
        Assert.True(reply.Guarded);
        Assert.Equal(AssistantEndpoints.GuardedReply, reply.Reply);
        Assert.NotNull(reply.Draft);
    }

    [Fact]
    public async Task The_model_gets_the_account_zip_and_current_draft_as_context_and_the_no_save_rule()
    {
        using var f = Factory("Is this project at ZIP 62701?");
        f.Model.Tool = "";
        await Talk(f, "Hi, I need a countertop", draft: new { zip = "62701", sku = "CT-QTZ" });

        var context = string.Join("\n", f.Model.LastMessages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        Assert.Contains("Customer's account ZIP: 62701", context);
        Assert.Contains("\"sku\":\"CT-QTZ\"", context);
        Assert.Contains("cannot save, accept, pay", f.Model.LastInstructions);
        Assert.Contains("NEVER calculate money, areas", f.Model.LastInstructions);
    }

    [Fact]
    public async Task Non_members_and_bad_requests_never_reach_the_model()
    {
        using var f = Factory("x");
        Assert.Equal(HttpStatusCode.Forbidden, (await Talk(f, "Hi", sub: "erin")).StatusCode);
        var tooBig = await f.As("alice").PostAsJsonAsync("/api/assistant/concierge",
            new { messages = new[] { new { role = "user", content = "Hi" } }, draft = new { blob = new string('x', 9000) } });
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        Assert.Equal(0, f.Model.Calls);
    }

    [Fact]
    public void Feet_and_inches_become_inches_and_only_the_measured_part_is_sent()
    {
        var json = JsonSerializer.Serialize(ConciergeTools.ToMeasurements(new DraftMeasurements(
            Layout: "l-island", Countertop: [new(8, 0, 25)], Island: [new(6, 3, 36)])));
        Assert.Contains("\"widthIn\":96", json);
        Assert.Contains("\"widthIn\":75", json);
        Assert.Contains("\"roof\":null", json);
    }
}

public sealed class ChatTextTests
{
    [Fact]
    public void Markdown_seen_live_is_cleaned_for_the_chat_bubble()
    {
        var raw = "Here's your draft:\r\n\r\n\r\n**Total:** $7,245.71\n\n\n\n## Next\n- **Edge** - $18.00/ft   \nDone";
        Assert.Equal("Here's your draft:\n\nTotal: $7,245.71\n\nNext\n- Edge - $18.00/ft\nDone", ChatText.Clean(raw));
    }

    [Fact]
    public void Money_survives_cleaning() =>
        Assert.Equal(PriceGuard.AmountsIn("**$2,890.28**"), PriceGuard.AmountsIn(ChatText.Clean("**$2,890.28**")));
}

/// <summary>2n-f: in a project, the concierge saves answers to it and can issue its quote, never accept it.</summary>
public sealed class ConciergeProjectTests
{
    private static readonly Guid ProjectId = Guid.Parse("6b8e8645-fe5c-4041-84f3-2504f9ce327c");

    private static object ProjectJson(decimal total) => new
    {
        id = ProjectId, title = "Kitchen countertops", zip = "62701", stage = "configure", sku = "CT-QTZ",
        preview = new { total, subtotal = 6693.50m, tax = 552.21m, lines = new[] { new { sku = "CT-QTZ", unitPrice = 89.00m, lineTotal = 4939.50m } }, missing = Array.Empty<string>() },
    };

    private static AssistantFactory Factory(string finalText, string tool = "save_answers")
    {
        var f = new AssistantFactory();
        f.Platform.Responses[$"GET /api/projects/{ProjectId}"] = ProjectJson(5347.01m);
        f.Platform.Responses[$"PUT /api/projects/{ProjectId}/answers"] = ProjectJson(7245.71m);
        f.Model.Tool = tool;
        f.Model.ToolArgs = new()
        {
            ["sku"] = "CT-QTZ",
            ["measurements"] = JsonSerializer.SerializeToElement(new { layout = "l-island", countertop = new[] { new { widthFt = 10, widthIn = 0, depthIn = 25 }, new { widthFt = 8, widthIn = 0, depthIn = 25 } }, island = new[] { new { widthFt = 6, widthIn = 0, depthIn = 36 } } }),
            ["choices"] = JsonSerializer.SerializeToElement(new[] { new { group = "ct-edge", sku = "EDGE-OGEE" } }),
        };
        f.Model.FinalText = finalText;
        return f;
    }

    private static Task<HttpResponseMessage> Talk(AssistantFactory f, string text, Guid? project = null) =>
        f.As("alice").PostAsJsonAsync("/api/assistant/concierge", new { messages = new[] { new { role = "user", content = text } }, projectId = project ?? ProjectId });

    [Fact]
    public async Task In_a_project_the_concierge_saves_answers_to_it_and_reports_the_change()
    {
        using var f = Factory("Saved. Your project now totals $7,245.71.");
        var reply = (await (await Talk(f, "Ogee edge please")).Content.ReadFromJsonAsync<ConciergeReply>())!;

        Assert.False(reply.Guarded);
        Assert.True(reply.ProjectChanged);
        Assert.Equal(["save_answers"], reply.ToolsUsed);          // loading the project as context is not a tool
        Assert.Contains($"PUT /api/projects/{ProjectId}/answers", f.Platform.Calls);
        Assert.Contains("\"widthIn\":96", f.Platform.Bodies.Single());   // 8 ft, converted in code
        Assert.Contains("\"stage\":\"configure\"", string.Join("\n", f.Model.LastMessages.Where(m => m.Role == ChatRole.System).Select(m => m.Text)));
    }

    [Fact]
    public async Task The_projects_own_total_may_be_quoted_but_not_an_invented_one()
    {
        using var ok = Factory("Right now the project is $5,347.01.", tool: "");
        Assert.False((await (await Talk(ok, "What's my total?")).Content.ReadFromJsonAsync<ConciergeReply>())!.Guarded);

        using var bad = Factory("It should come to about $5,000.00.", tool: "");
        Assert.True((await (await Talk(bad, "Roughly?")).Content.ReadFromJsonAsync<ConciergeReply>())!.Guarded);
    }

    [Fact]
    public async Task There_is_no_accept_tool_and_the_rule_is_in_the_instructions()
    {
        using var f = Factory("Please press Accept quote yourself.", tool: "");
        await Talk(f, "Accept it for me");
        Assert.Contains("NEVER accept", f.Model.LastInstructions);
        Assert.DoesNotContain(f.Model.LastToolNames, n => n.Contains("accept", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("issue_quote", f.Model.LastToolNames);
        Assert.DoesNotContain(f.Platform.Calls, c => c.Contains("/accept"));
    }

    [Fact]
    public async Task Someone_elses_project_is_refused_before_the_model()
    {
        using var f = Factory("x");
        var r = await Talk(f, "Hi", Guid.NewGuid());   // the platform answers {error: 404}
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(0, f.Model.Calls);
    }
}

/// <summary>Catalog mode: the customer narrows a type's products by asking; the server applies the filter.</summary>
public sealed class ConciergeCatalogTests
{
    private static AssistantFactory Factory(string finalText, object? args = null)
    {
        var f = new AssistantFactory();
        f.Platform.Responses["GET /api/catalog/products?zip=75078&category=countertops"] = new[]
        {
            new { sku = "CT-QTZ-CALACATTA", name = "Quartz countertop, Calacatta white", unit = "sqft", listPrice = 89.00m, attributes = new Dictionary<string, string> { ["material"] = "quartz", ["color"] = "Calacatta white" } },
            new { sku = "CT-QTZ-LUX", name = "Quartz countertop, Lux white", unit = "sqft", listPrice = 109.00m, attributes = new Dictionary<string, string> { ["material"] = "quartz", ["color"] = "Lux white" } },
            new { sku = "CT-GRN-ABSOLUTE", name = "Granite countertop, Absolute black", unit = "sqft", listPrice = 72.00m, attributes = new Dictionary<string, string> { ["material"] = "granite", ["color"] = "Absolute black" } },
        };
        f.Platform.Responses["GET /api/catalog/categories"] = new[] { new { id = "countertops", attributes = new[] { new { key = "material" }, new { key = "color" } } } };
        f.Model.Tool = "filter_catalog";
        f.Model.ToolArgs = new()
        {
            ["attributes"] = JsonSerializer.SerializeToElement(args ?? new { material = "quartz", color = "white" }),
            ["maxPrice"] = 90,
        };
        f.Model.FinalText = finalText;
        return f;
    }

    private static async Task<ConciergeReply> Ask(AssistantFactory f, string text)
    {
        var r = await f.As("alice").PostAsJsonAsync("/api/assistant/concierge",
            new { messages = new[] { new { role = "user", content = text } }, catalog = new { zip = "75078", category = "countertops" } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ConciergeReply>())!;
    }

    [Fact]
    public async Task White_quartz_under_90_filters_on_the_server_and_the_page_gets_the_matches()
    {
        using var f = Factory("I found 1 white quartz under $90: Quartz countertop, Calacatta white at $89.00 per sq ft.");
        var reply = await Ask(f, "white quartz under $90");

        Assert.False(reply.Guarded);                       // $90 is the customer's own bound, $89.00 the tool's price
        Assert.Equal(["filter_catalog"], reply.ToolsUsed);
        Assert.Equal(["CT-QTZ-CALACATTA"], reply.Filter!.Skus);
        Assert.Equal(90m, reply.Filter.MaxPrice);
        Assert.Equal("quartz", reply.Filter.Attributes["material"]);
        Assert.Contains("Browsing: category countertops in ZIP 75078", string.Join("\n", f.Model.LastMessages.Where(m => m.Role == ChatRole.System).Select(m => m.Text)));
        Assert.Equal(["filter_catalog"], f.Model.LastToolNames);   // no project or quote tools here
    }

    [Fact]
    public async Task An_invented_price_is_still_blocked_but_the_filter_reaches_the_page()
    {
        using var f = Factory("The cheapest is about $79.00.");
        var reply = await Ask(f, "cheapest white quartz?");
        Assert.True(reply.Guarded);
        Assert.NotNull(reply.Filter);
    }

    [Fact]
    public void Attribute_values_match_by_containing_ignoring_case_and_hyphens()
    {
        var p = JsonSerializer.SerializeToElement(new { listPrice = 45m, name = "Butcher block, maple", attributes = new { material = "butcher-block", color = "Natural maple" } });
        Assert.True(CatalogFilterTools.Matches(p, new Dictionary<string, string> { ["material"] = "Butcher Block" }, null, null, []));
        Assert.True(CatalogFilterTools.Matches(p, new Dictionary<string, string> { ["color"] = "maple" }, null, 50, ["block"]));
        Assert.False(CatalogFilterTools.Matches(p, new Dictionary<string, string> { ["color"] = "white" }, null, null, []));
        Assert.False(CatalogFilterTools.Matches(p, new Dictionary<string, string>(), 50, null, []));
    }
}
