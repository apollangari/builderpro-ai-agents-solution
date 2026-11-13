using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuilderPro.Assistant.Api;

/// <summary>
/// The one rule, enforced in code (3a): the assistant may only state a money amount that a TOOL
/// returned during this turn. The model is told the same in its instructions, but instructions are
/// a request; this is the guarantee. An unbacked amount means the whole reply is replaced.
/// </summary>
public static partial class PriceGuard
{
    // $1,234.56 | $ 12 | USD 12.50 | 12.50 USD | 12 dollars
    [GeneratedRegex(@"(?:\$\s?|USD\s?)(?<n>\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)|(?<n>\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)\s?(?:USD|dollars?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Money();

    /// <summary>Every money amount in a text, as decimals.</summary>
    public static IReadOnlyList<decimal> AmountsIn(string text) =>
        [.. Money().Matches(text).Select(m => decimal.Parse(m.Groups["n"].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture))];

    /// <summary>Amounts in the reply that no tool returned (empty = safe).</summary>
    public static IReadOnlyList<decimal> Unbacked(string reply, IReadOnlySet<decimal> allowed) =>
        [.. AmountsIn(reply).Where(a => !allowed.Contains(decimal.Round(a, 2))).Distinct()];
}

/// <summary>
/// What the tools returned this turn: every money-like value in their JSON (any property whose
/// name says price/total/tax/subtotal/amount). Only these may appear in the reply.
/// </summary>
public sealed class PriceLedger
{
    private static readonly string[] MoneyWords = ["price", "total", "tax", "subtotal", "amount"];
    private readonly HashSet<decimal> _amounts = [];

    public IReadOnlySet<decimal> Amounts => _amounts;
    public List<string> ToolsUsed { get; } = [];

    public void Record(string tool, JsonElement result)
    {
        ToolsUsed.Add(tool);
        Walk(result, null);
    }

    /// <summary>Platform data the agent was given as context (e.g. the project): its amounts count, it isn't a tool.</summary>
    public void RecordContext(JsonElement result) => Walk(result, null);

    private void Walk(JsonElement e, string? property)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Walk(p.Value, p.Name);
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Walk(item, property);
                break;
            case JsonValueKind.Number when property is not null
                && MoneyWords.Any(w => property.Contains(w, StringComparison.OrdinalIgnoreCase))
                && !property.Contains("rate", StringComparison.OrdinalIgnoreCase):
                _amounts.Add(decimal.Round(e.GetDecimal(), 2));
                break;
        }
    }
}
