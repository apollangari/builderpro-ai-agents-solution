using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Net.Http.Headers;

namespace BuilderPro.Assistant.Api;

public sealed class SchedulingAgentOptions
{
    /// <summary>Off: the concierge has no scheduling tool (the agent isn't deployed).</summary>
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://scheduling-agent";
    public string Audience { get; set; } = "builderpro-scheduling";
    public string? EntraScope { get; set; }
}

/// <summary>
/// 4a: the Python scheduling agent (LangGraph), reached over A2A 1.0 JSON-RPC (SendMessage). It proposes
/// measure-visit times within the customer's contact hours; it never books.
///
/// Auth is on-behalf-of at every hop: this service exchanges the customer's token for one issued FOR the
/// scheduling agent (audience builderpro-scheduling), and the agent exchanges that again for the platform
/// API. No hop forwards the token it received. Trace context flows with the HTTP call (HttpClient
/// instrumentation), so the conversation is one trace in the Aspire dashboard.
/// </summary>
public interface ISchedulingAgent
{
    bool Enabled { get; }
    Task<JsonElement> ProposeVisitTimesAsync(Guid projectId, CancellationToken ct);
}

public sealed class A2ASchedulingAgent(HttpClient http, SchedulingAgentOptions options, ITokenExchange exchange, IHttpContextAccessor accessor,
    ILogger<A2ASchedulingAgent> log) : ISchedulingAgent
{
    public bool Enabled => options.Enabled;

    public async Task<JsonElement> ProposeVisitTimesAsync(Guid projectId, CancellationToken ct)
    {
        var caller = accessor.HttpContext?.Request.Headers[HeaderNames.Authorization].ToString();
        if (!AuthenticationHeaderValue.TryParse(caller, out var auth) || auth.Parameter is null) return Error(401, "not_signed_in");
        var token = await exchange.ForAudienceAsync(auth.Parameter, options.Audience, options.EntraScope, ct);
        if (token is null) return Error(401, "token_exchange_failed");

        var body = new
        {
            jsonrpc = "2.0",
            id = Guid.NewGuid().ToString("N"),
            method = "SendMessage",
            @params = new
            {
                message = new
                {
                    messageId = Guid.NewGuid().ToString("N"),
                    role = "ROLE_USER",
                    parts = new object[] { new { data = new { projectId } } },
                },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/a2a") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("A2A-Version", "1.0");
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return Error((int)response.StatusCode, "scheduling_agent_refused");
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return Read(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false } or JsonException)
        {
            log.LogWarning("Scheduling agent unavailable ({Error})", ex.GetType().Name);
            return Error(503, "scheduling_agent_unavailable");
        }
    }

    /// <summary>The task's data artifact (outcome, proposals, message), or the agent's own failure message.</summary>
    internal static JsonElement Read(JsonElement rpc)
    {
        if (rpc.TryGetProperty("error", out var error)) return Error(502, error.TryGetProperty("message", out var m) ? m.GetString() ?? "a2a_error" : "a2a_error");
        if (!rpc.TryGetProperty("result", out var result) || !result.TryGetProperty("task", out var task)) return Error(502, "a2a_unexpected_answer");
        var state = task.TryGetProperty("status", out var status) && status.TryGetProperty("state", out var s) ? s.GetString() : null;
        if (state == "TASK_STATE_COMPLETED" && task.TryGetProperty("artifacts", out var artifacts))
        {
            foreach (var artifact in artifacts.EnumerateArray())
                foreach (var part in artifact.GetProperty("parts").EnumerateArray())
                    if (part.TryGetProperty("data", out var data)) return data.Clone();
        }
        var said = status.TryGetProperty("message", out var msg) && msg.TryGetProperty("parts", out var parts)
            ? string.Join(" ", parts.EnumerateArray().Where(p => p.TryGetProperty("text", out _)).Select(p => p.GetProperty("text").GetString()))
            : state;
        return JsonSerializer.SerializeToElement(new { error = 502, detail = said });
    }

    private static JsonElement Error(int status, string detail) => JsonSerializer.SerializeToElement(new { error = status, detail });
}

/// <summary>The agent isn't deployed: no tool.</summary>
public sealed class NoSchedulingAgent : ISchedulingAgent
{
    public bool Enabled => false;
    public Task<JsonElement> ProposeVisitTimesAsync(Guid projectId, CancellationToken ct) =>
        Task.FromResult(JsonSerializer.SerializeToElement(new { error = 503, detail = "scheduling_agent_not_deployed" }));
}
