using System.Globalization;

namespace Newrest.Pos.Client.Core.ViewModels;

/// <summary>French amounts on screen whatever the culture of the machine ("1 234,50 MAD").</summary>
public static class Format
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-MA");

    public static string Mad(decimal amount) => Amount(amount) + " MAD";

    public static string Amount(decimal amount) => amount.ToString("#,##0.00", French);
}
