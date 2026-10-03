using System.Globalization;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Sales;

/// <summary>
/// Ticket numbering: <c>{RegisterPrefix}-{Sequence:D8}</c>. The sequence is per register, starts at 1,
/// is shared by sales and credit notes, and must never have gaps.
/// </summary>
public static class TicketNumber
{
    public const int SequenceDigits = 8;

    public static string Format(string registerPrefix, long sequence)
    {
        if (sequence < 1)
        {
            throw new DomainException("invalid_sequence", "Ticket sequences start at 1.");
        }

        return $"{ValidatePrefix(registerPrefix)}-{sequence.ToString($"D{SequenceDigits}", CultureInfo.InvariantCulture)}";
    }

    public static string ValidatePrefix(string prefix)
    {
        var value = Guard.NotBlank(prefix, nameof(prefix), 12).ToUpperInvariant();
        if (value.Length < 2 || !value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
        {
            throw new DomainException("invalid_prefix", "A register prefix has 2 to 12 characters A-Z or 0-9.");
        }

        return value;
    }

    public static long Next(long lastSequence) => lastSequence < 0
        ? throw new DomainException("invalid_sequence", "The last sequence cannot be negative.")
        : checked(lastSequence + 1);

    /// <summary>Missing sequences between <paramref name="firstExpected"/> and the highest sequence present.</summary>
    public static IReadOnlyList<long> FindGaps(IEnumerable<long> sequences, long firstExpected = 1)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        var present = sequences.Where(s => s >= firstExpected).ToHashSet();
        if (present.Count == 0)
        {
            return [];
        }

        var max = present.Max();
        var gaps = new List<long>();
        for (var s = firstExpected; s <= max; s++)
        {
            if (!present.Contains(s))
            {
                gaps.Add(s);
            }
        }

        return gaps;
    }
}
