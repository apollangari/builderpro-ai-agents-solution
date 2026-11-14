using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuilderPro.Assistant.Api.Tests;

/// <summary>4a: the concierge reaches the Python scheduling agent over A2A, on-behalf-of the customer.</summary>
public sealed class SchedulingAgentTests
{
    private sealed class FakeExchange(string? token = "agent-token") : ITokenExchange
    {
        public List<string> Audiences { get; } = [];
        public bool Enabled => true;
        public Task<string?> ForPlatformAsync(string callerToken, CancellationToken ct) => Task.FromResult<string?>("platform-token");
        public Task<string?> ForAudienceAsync(string callerToken, string audience, string? entraScope, CancellationToken ct)
        {
            Audiences.Add(audience);
            return Task.FromResult(token);
        }
    }

    private sealed class FakeAgent(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return answer();
        }
    }

    private static readonly Guid Project = Guid.Parse("8a362998-d12a-454d-b986-b77197bb0d83");

    private static (A2ASchedulingAgent Agent, FakeAgent Http, FakeExchange Exchange) Make(Func<HttpResponseMessage> answer, string? exchanged = "agent-token")
    {
        var http = new FakeAgent(answer);
        var exchange = new FakeExchange(exchanged);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer customer-token";
        var agent = new A2ASchedulingAgent(new HttpClient(http), new SchedulingAgentOptions { Enabled = true, BaseUrl = "http://scheduling.test" },
            exchange, new HttpContextAccessor { HttpContext = context }, NullLogger<A2ASchedulingAgent>.Instance);
        return (agent, http, exchange);
    }

    private static HttpResponseMessage Completed() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            jsonrpc = "2.0", id = "1",
            result = new
            {
                task = new
                {
                    id = "t1", status = new { state = "TASK_STATE_COMPLETED" },
                    artifacts = new[] { new { parts = new object[] {
                        new { data = new { outcome = "proposed", proposals = new[] { new { start = "2026-11-16T19:00:00+00:00" } }, message = "These times fit." } },
                        new { text = "These times fit." } } } },
                },
            },
        }),
    };

    [Fact]
    public async Task Sends_an_A2A_message_with_a_token_issued_for_the_agent_and_reads_the_proposals()
    {
        var (agent, http, exchange) = Make(Completed);
        var result = await agent.ProposeVisitTimesAsync(Project, default);

        Assert.Equal("proposed", result.GetProperty("outcome").GetString());
        Assert.Equal(new[] { "builderpro-scheduling" }, exchange.Audiences);
        Assert.Equal("Bearer agent-token", http.Last!.Headers.Authorization!.ToString());   // never the customer's own token
        Assert.Equal("1.0", http.Last.Headers.GetValues("A2A-Version").Single());
        Assert.Equal("http://scheduling.test/a2a", http.Last.RequestUri!.ToString());
        var body = JsonDocument.Parse(http.Body!).RootElement;
        Assert.Equal("SendMessage", body.GetProperty("method").GetString());
        Assert.Equal(Project.ToString(), body.GetProperty("params").GetProperty("message").GetProperty("parts")[0].GetProperty("data").GetProperty("projectId").GetString());
    }

    [Fact]
    public async Task Failures_become_data_the_model_can_explain()
    {
        Assert.Equal(401, (await Make(Completed, exchanged: null).Agent.ProposeVisitTimesAsync(Project, default)).GetProperty("error").GetInt32());
        Assert.Equal(401, (await Make(() => new HttpResponseMessage(HttpStatusCode.Unauthorized)).Agent.ProposeVisitTimesAsync(Project, default)).GetProperty("error").GetInt32());
        Assert.Equal(503, (await Make(() => throw new HttpRequestException("down")).Agent.ProposeVisitTimesAsync(Project, default)).GetProperty("error").GetInt32());

        var failed = A2ASchedulingAgent.Read(JsonDocument.Parse("""
            {"result":{"task":{"status":{"state":"TASK_STATE_FAILED","message":{"parts":[{"text":"That project isn't available."}]}}}}}
            """).RootElement);
        Assert.Equal("That project isn't available.", failed.GetProperty("detail").GetString());
        Assert.Equal(502, A2ASchedulingAgent.Read(JsonDocument.Parse("""{"error":{"code":-32600,"message":"bad"}}""").RootElement).GetProperty("error").GetInt32());
    }

    [Fact]
    public void The_concierge_offers_the_tool_only_in_a_project_and_only_when_the_agent_is_deployed()
    {
        var api = new FakePlatform();
        var (agent, _, _) = Make(Completed);
        static IEnumerable<string> Names(ConciergeTools t) => t.All().Select(x => x.Name);
        Assert.Contains("propose_visit_times", Names(new ConciergeTools(api, new PriceLedger(), new DraftLedger(), Project, scheduling: agent)));
        Assert.DoesNotContain("propose_visit_times", Names(new ConciergeTools(api, new PriceLedger(), new DraftLedger(), Project, scheduling: new NoSchedulingAgent())));
        Assert.DoesNotContain("propose_visit_times", Names(new ConciergeTools(api, new PriceLedger(), new DraftLedger(), null, scheduling: agent)));
    }
}
