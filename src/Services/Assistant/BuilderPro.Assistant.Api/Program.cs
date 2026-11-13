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

// The tools call the platform through service discovery, as the caller (PlatformTools.cs).
builder.Services.AddHttpClient("catalog", c => c.BaseAddress = new Uri("http://catalog"));
builder.Services.AddHttpClient("quotes", c => c.BaseAddress = new Uri("http://quotes"));
builder.Services.AddScoped<IPlatformApi, HttpPlatformApi>();

// The model, chosen by configuration (the AppHost passes it from user secrets; Key Vault in Azure):
//   foundry (default):  Microsoft Foundry's OpenAI v1 API, https://<resource>.services.ai.azure.com/openai/v1/,
//                       the deployment name as the model, API key required.
//   openai-compatible:  any OpenAI-compatible server, e.g. a free local LLM (Ollama, LM Studio, vLLM):
//                       Assistant:Endpoint is its full base URL (e.g. http://localhost:11434/v1/), key optional.
// Missing settings = no assistant (503), never a crash.
builder.Services.AddSingleton<IAssistantChatClient>(sp => new AssistantChatClient(AssistantModel.Create(sp.GetRequiredService<IConfiguration>())));

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapAssistantEndpoints();
app.MapConciergeEndpoints();   // 3b

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
