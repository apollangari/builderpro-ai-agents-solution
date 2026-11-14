using System.ClientModel;
using System.ClientModel.Primitives;
using BuilderPro.Assistant.Api;
using BuilderPro.Security;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();
builder.Services.AddBuilderProAuth(builder.Configuration);
builder.Services.AddTenantLookup(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);

// 3e: the Assistant's own database: conversations, so store staff can take over a chat.
builder.AddNpgsqlDataSource("assistantdb");
builder.Services.AddSingleton<IConversationStore, PostgresConversationStore>();

// The tools call the platform through service discovery, as the caller (PlatformTools.cs).
builder.Services.AddHttpClient("catalog", c => c.BaseAddress = new Uri("http://catalog"));
builder.Services.AddHttpClient("quotes", c => c.BaseAddress = new Uri("http://quotes"));
builder.Services.AddScoped<IPlatformApi, HttpPlatformApi>();

// 3c: on-behalf-of. Tokens arriving here are FOR the Assistant (Auth:Audience = builderpro-assistant);
// tools call the platform with an exchanged token for the same person (OnBehalfOf.cs).
var exchange = builder.Configuration.GetSection("Assistant:TokenExchange").Get<TokenExchangeOptions>() ?? new TokenExchangeOptions();
if (exchange.Enabled)
{
    exchange.Validate(allowHttpForLocalDev: !builder.Environment.IsProduction());
    builder.Services.AddSingleton(exchange);
    // A dedicated client: no caller credentials, no redirects, a short timeout.
    builder.Services.AddHttpClient<ITokenExchange, OAuthTokenExchange>(c => c.Timeout = TimeSpan.FromSeconds(10))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
}
else
{
    builder.Services.AddSingleton<ITokenExchange, NoTokenExchange>();
}

// 4a: the Python scheduling agent over A2A (on-behalf-of with audience builderpro-scheduling).
var scheduling = builder.Configuration.GetSection("Assistant:SchedulingAgent").Get<SchedulingAgentOptions>() ?? new SchedulingAgentOptions();
if (scheduling.Enabled)
{
    builder.Services.AddSingleton(scheduling);
    builder.Services.AddHttpClient<ISchedulingAgent, A2ASchedulingAgent>(c => c.Timeout = TimeSpan.FromSeconds(30));
}
else
{
    builder.Services.AddSingleton<ISchedulingAgent, NoSchedulingAgent>();
}

// 3c: the same catalog/quote tools for any MCP client (Streamable HTTP, stateless: each call is one
// HTTP request carrying the customer's own token, so the tools act exactly as that customer).
builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "builderpro", Version = "1.0" })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<McpTools>();

// The model, chosen by configuration (the AppHost passes it from user secrets; Key Vault in Azure):
//   foundry (default):  Microsoft Foundry's OpenAI v1 API, https://<resource>.services.ai.azure.com/openai/v1/,
//                       the deployment name as the model, API key required.
//   openai-compatible:  any OpenAI-compatible server, e.g. a free local LLM (Ollama, LM Studio, vLLM):
//                       Assistant:Endpoint is its full base URL (e.g. http://localhost:11434/v1/), key optional.
// Missing settings = no assistant (503), never a crash.
builder.Services.AddSingleton<IAssistantChatClient>(sp => new AssistantChatClient(AssistantModel.Create(sp.GetRequiredService<IConfiguration>())));

var app = builder.Build();

await app.Services.GetRequiredService<IConversationStore>().InitializeAsync(CancellationToken.None);

app.UseExceptionHandler();
app.UseMcpAuthChallenge();   // 3c: 401s from MCP say where to get a token
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapAssistantEndpoints();
app.MapConciergeEndpoints();   // 3b
app.MapConversationEndpoints(); // 3e: staff see, take over, reply, hand back
app.MapMcp("/api/assistant/mcp").RequireAuthorization();   // 3c: MCP, signed-in callers only
app.MapMcpAuthMetadata();                                    // 3c: OAuth protected-resource metadata

app.Run();

public partial class Program;

/// <summary>Builds the chat client from configuration (the two providers are described in Program.cs).</summary>
public static class AssistantModel
{
    public static IChatClient? Create(IConfiguration config)
    {
        var provider = config["Assistant:Provider"] ?? "foundry";
        var endpoint = config["Assistant:Endpoint"];
        var model = config["Assistant:Deployment"];
        var key = config["Assistant:ApiKey"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model)) return null;

        Uri baseUrl;
        if (provider == "openai-compatible")
        {
            baseUrl = new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/");
            if (string.IsNullOrWhiteSpace(key)) key = "local";   // local servers usually ignore the key
        }
        else
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            // Accepts a resource URL or a Foundry project URL (.../api/projects/x): only the host is used.
            baseUrl = new Uri($"{new Uri(endpoint).GetLeftPart(UriPartial.Authority)}/openai/v1/");
        }

        var chat = new ChatClient(model, new ApiKeyCredential(key), new OpenAIClientOptions
        {
            Endpoint = baseUrl,
            // One retry, then fail fast: the default backs off for ~90 s on a 429 and the user sees nothing.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 1),
            NetworkTimeout = TimeSpan.FromSeconds(60),
        });
        return chat.AsIChatClient()
            .AsBuilder()
            .UseOpenTelemetry(sourceName: "BuilderPro.Assistant") // prompts and tool calls show up as spans in the dashboard
            .Build();
    }
}
