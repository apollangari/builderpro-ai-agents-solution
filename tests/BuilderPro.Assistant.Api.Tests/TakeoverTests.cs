using System.Net;
using System.Net.Http.Json;
using BuilderPro.Security;

namespace BuilderPro.Assistant.Api.Tests;

/// <summary>3e: a person can take over a concierge chat; the concierge pauses; the customer sees who answers.</summary>
public sealed class TakeoverTests
{
    private static AssistantFactory Factory()
    {
        var f = new AssistantFactory();
        f.Platform.Responses["GET /api/catalog/service-area"] = new { zip = "75078", served = true };
        f.Model.Tool = "check_zip";
        f.Model.ToolArgs = new() { ["zip"] = "75078" };
        f.Model.FinalText = "Yes, we serve 75078.";
        return f;
    }

    private static async Task<ConciergeReply> Say(AssistantFactory f, string text, Guid? conversation = null)
    {
        var r = await f.As("alice").PostAsJsonAsync("/api/assistant/concierge", new { messages = new[] { new { role = "user", content = text } }, conversationId = conversation });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ConciergeReply>())!;
    }

    [Fact]
    public async Task Staff_see_the_chat_take_it_over_reply_by_name_and_hand_it_back()
    {
        using var f = Factory();
        var first = await Say(f, "Do you serve 75078?");
        Assert.Equal(("concierge", "Yes, we serve 75078."), (first.Answering, first.Reply));
        var id = first.ConversationId!.Value;

        var list = (await f.As("bob").GetFromJsonAsync<ConversationSummaryDto[]>("/api/assistant/conversations"))!;
        var mine = Assert.Single(list);
        Assert.Equal((id, "c-alice", "concierge", "concierge"), (mine.Id, mine.CustomerId, mine.Mode, mine.LastRole));

        var taken = (await (await f.As("bob").PostAsync($"/api/assistant/conversations/{id}/takeover", null)).Content.ReadFromJsonAsync<ThreadDto>())!;
        Assert.Equal(("staff", "Bob"), (taken.Mode, taken.StaffName));
        Assert.Equal(HttpStatusCode.Conflict, (await f.As("mia").PostAsync($"/api/assistant/conversations/{id}/takeover", null)).StatusCode);   // one person at a time

        // The concierge is paused: the customer's message goes to Bob, the model isn't called.
        var calls = f.Model.Calls;
        var paused = await Say(f, "Can someone call me?", id);
        Assert.Equal(("staff", "Bob", ""), (paused.Answering, paused.StaffName, paused.Reply));
        Assert.Equal(calls, f.Model.Calls);

        Assert.Equal(HttpStatusCode.Conflict, (await f.As("mia").PostAsJsonAsync($"/api/assistant/conversations/{id}/messages", new { text = "Hi" })).StatusCode);
        await f.As("bob").PostAsJsonAsync($"/api/assistant/conversations/{id}/messages", new { text = "Sure, I'll call you at 3." });

        // The customer polls and sees who said what.
        var thread = (await f.As("alice").GetFromJsonAsync<ThreadDto>($"/api/assistant/conversations/{id}/messages?after={paused.LastSeq}"))!;
        var last = thread.Messages[^1];
        Assert.Equal(("staff", "Bob", "Sure, I'll call you at 3."), (last.Role, last.Author, last.Text));

        await f.As("bob").PostAsync($"/api/assistant/conversations/{id}/release", null);
        var back = await Say(f, "Thanks! Do you serve 75078?", id);
        Assert.Equal(("concierge", "Yes, we serve 75078."), (back.Answering, back.Reply));
        var all = (await f.As("alice").GetFromJsonAsync<ThreadDto>($"/api/assistant/conversations/{id}/messages"))!;
        Assert.Equal(new[] { "customer", "concierge", "system", "customer", "staff", "system", "customer", "concierge" }, all.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task Only_store_support_staff_see_or_answer_and_never_an_agent_or_another_store()
    {
        using var f = Factory();
        var id = (await Say(f, "Hello")).ConversationId!.Value;
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("sam").GetAsync("/api/assistant/conversations")).StatusCode);       // staff without support.chat
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("alice").GetAsync("/api/assistant/conversations")).StatusCode);     // a customer
        Assert.Empty((await f.As("gus").GetFromJsonAsync<ConversationSummaryDto[]>("/api/assistant/conversations"))!);         // another store sees nothing
        Assert.Equal(HttpStatusCode.NotFound, (await f.As("gus").PostAsync($"/api/assistant/conversations/{id}/takeover", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.As("gail").GetAsync($"/api/assistant/conversations/{id}/messages")).StatusCode);   // another customer

        var agent = f.As("bob");
        agent.DefaultRequestHeaders.Add(AgentCall.Header, "concierge");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync($"/api/assistant/conversations/{id}/takeover", null)).StatusCode);
    }
}
