namespace BuilderPro.Security;

/// <summary>
/// What a store's package includes (1h, owner): the guided workflow always; a help chat that only
/// answers; the AI concierge that works on the project; AI phone scheduling (4b). Services check these
/// on the tenant lookup they already do; the web hides what isn't included. Server-side is what counts.
/// </summary>
public static class StoreFeatures
{
    public const string Guided = "guided";
    public const string HelpChat = "help-chat";
    public const string Concierge = "concierge";
    public const string PhoneScheduling = "phone-scheduling";

    public static readonly IReadOnlyList<string> All = [Guided, HelpChat, Concierge, PhoneScheduling];

    /// <summary>A store without a configured package gets the guided workflow, the help chat and the concierge.</summary>
    public static readonly IReadOnlyList<string> Default = [Guided, HelpChat, Concierge];
}
