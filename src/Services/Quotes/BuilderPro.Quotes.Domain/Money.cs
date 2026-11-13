using System.Globalization;

namespace BuilderPro.Quotes.Domain;

/// <summary>
/// An amount in ONE currency. decimal (base-10), never double: 0.1 + 0.2 must equal 0.3.
///
/// Arithmetic only between equal currencies; mixing them throws, because silently adding
/// USD to EUR is a bug, not a business case. Rounding lives in <see cref="Rounding"/>, one place.
/// </summary>
public readonly record struct Money
{
    public decimal Amount { get; }
    public string Currency { get; }

    public Money(decimal amount, string currency)
    {
        if (!IsCurrencyCode(currency))
        {
            throw new ArgumentException($"'{currency}' is not an ISO 4217 code (three upper-case letters).", nameof(currency));
        }
        Amount = amount;
        Currency = currency;
    }

    public static Money Zero(string currency) => new(0m, currency);

    public static bool IsCurrencyCode(string? code) =>
        code is { Length: 3 } && code.All(c => c is >= 'A' and <= 'Z');

    public static Money operator +(Money a, Money b)
    {
        SameCurrency(a, b);
        return new Money(a.Amount + b.Amount, a.Currency);
    }

    public static Money operator *(Money m, decimal factor) => new(m.Amount * factor, m.Currency);

    private static void SameCurrency(Money a, Money b)
    {
        if (a.Currency != b.Currency)
        {
            throw new InvalidOperationException($"Cannot combine {a.Currency} and {b.Currency}.");
        }
    }

    public override string ToString() => $"{Amount.ToString("0.00##", CultureInfo.InvariantCulture)} {Currency}";
}

/// <summary>
/// THE rounding rule, in one place: to the currency's minor unit (cents), halves away from
/// zero ("commercial" rounding: 0.125 -> 0.13), which is what a customer expects on a receipt.
///
/// .NET's default is banker's rounding (0.125 -> 0.12). Using the default somewhere and this
/// elsewhere is how two screens end up a cent apart, so nothing else in the code calls Math.Round.
/// </summary>
public static class Rounding
{
    // Every currency we sell in has 2 minor digits. A per-currency table goes here when that changes
    // (JPY 0, BHD 3), and only here.
    public const int MinorDigits = 2;

    public static Money ToMinorUnit(Money m) =>
        new(Math.Round(m.Amount, MinorDigits, MidpointRounding.AwayFromZero), m.Currency);
}
