namespace Newrest.Pos.Domain.Common;

/// <summary>
/// Monetary helpers. All amounts are MAD expressed as <see cref="decimal"/> with 2 decimals.
/// Rounding is commercial (half away from zero), the convention used on Moroccan receipts.
/// </summary>
public static class Money
{
    public const string Currency = "MAD";
    public const int Decimals = 2;

    public static decimal Round(decimal amount) => Math.Round(amount, Decimals, MidpointRounding.AwayFromZero);

    public static bool HasAtMostTwoDecimals(decimal amount) => Round(amount) == amount;

    public static decimal EnsureValid(decimal amount, string name)
    {
        if (!HasAtMostTwoDecimals(amount))
        {
            throw new DomainException("invalid_amount", $"{name} must have at most {Decimals} decimals.");
        }

        return amount;
    }

    /// <summary>VAT included in a tax-inclusive amount: ttc * rate / (1 + rate).</summary>
    public static decimal VatFromInclusive(decimal amountInclTax, decimal vatRate)
        => vatRate == 0 ? 0m : Round(amountInclTax * vatRate / (1 + vatRate));
}
