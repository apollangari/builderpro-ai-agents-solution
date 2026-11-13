namespace BuilderPro.Quotes.Domain;

/// <summary>
/// THE deposit rule (6a, owner decision): the price of an accepted quote is held only once the customer
/// pays an advance of DepositPercent of the quote's total (5 % by default). Pure, like QuotePricer.
///
///   amount = total x percent / 100, rounded to cents (half away from zero), at least 0.01 for a non-zero total.
/// </summary>
public static class DepositPolicy
{
    public const decimal DefaultPercent = 5m;
    public const decimal MaxPercent = 100m;

    public static Money AmountFor(Money total, decimal percent)
    {
        if (percent <= 0 || percent > MaxPercent) throw new ArgumentOutOfRangeException(nameof(percent), $"Deposit percent must be in (0, {MaxPercent}].");
        if (total.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(total), "Nothing to deposit on a zero total.");
        var amount = Rounding.ToMinorUnit(total * (percent / 100m));
        return amount.Amount < 0.01m ? new Money(0.01m, total.Currency) : amount;
    }
}
