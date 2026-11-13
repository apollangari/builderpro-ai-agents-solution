namespace BuilderPro.Security;

/// <summary>
/// Marks a request made by an AI agent on the user's behalf (2n-f). The assistant service sets it on
/// every platform call its tools make. Endpoints that commit the customer (accepting a quote, later
/// paying a deposit or booking a visit) refuse requests that carry it, so an agent cannot press the
/// customer's buttons even though it calls with the customer's token. A browser sending it only
/// blocks itself.
/// </summary>
public static class AgentCall
{
    public const string Header = "X-BuilderPro-Agent";

    public static bool IsAgent(Microsoft.AspNetCore.Http.HttpContext http) => http.Request.Headers.ContainsKey(Header);
}
