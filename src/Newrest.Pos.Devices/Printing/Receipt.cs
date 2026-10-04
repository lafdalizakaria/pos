namespace Newrest.Pos.Devices.Printing;

public enum ReceiptAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>One printed line. <see cref="Large"/> doubles width and height (totals, ticket number).</summary>
public sealed record ReceiptLine(string Text, ReceiptAlignment Alignment = ReceiptAlignment.Left, bool Bold = false, bool Large = false)
{
    public static readonly ReceiptLine Blank = new(string.Empty);
}

/// <summary>Device-independent receipt: built by the register, encoded by each printer implementation.</summary>
public sealed record ReceiptDocument(IReadOnlyList<ReceiptLine> Lines, bool Cut = true, bool OpenDrawer = false)
{
    public string ToPlainText() => string.Join(Environment.NewLine, Lines.Select(l => l.Text));
}

/// <summary>Helpers for fixed-width receipt layouts.</summary>
public static class ReceiptLayout
{
    /// <summary>Text on the left, amount on the right, on one line of <paramref name="width"/> characters (left part truncated).</summary>
    public static string TwoColumns(string left, string right, int width)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var room = Math.Max(0, width - right.Length - 1);
        var l = left.Length > room ? left[..room] : left;
        return l + new string(' ', Math.Max(1, width - l.Length - right.Length)) + right;
    }

    public static string Separator(int width, char c = '-') => new(c, width);

    /// <summary>Word-wraps long text (addresses, legal mentions).</summary>
    public static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();
        foreach (var word in (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word.Length > width ? word[..width] : word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }
}

public interface IReceiptPrinter : IDevice
{
    /// <summary>Number of characters per line in normal size (42 for 80 mm paper, 32 for 58 mm).</summary>
    int LineWidth { get; }

    Task PrintAsync(ReceiptDocument receipt, CancellationToken cancellationToken = default);
}

/// <summary>Cash drawer, usually wired to the receipt printer (RJ11 kick-out).</summary>
public interface ICashDrawer : IDevice
{
    Task OpenAsync(CancellationToken cancellationToken = default);
}
