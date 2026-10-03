using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Application.Clients;

/// <summary>One data row of a diner import file. Null = column absent or cell empty (field left unchanged on update).</summary>
public sealed record DinerImportRow(int Line, string? EmployeeNumber, string? LastName, string? FirstName, string? Category,
    string? BadgeNumber, string? AccountType, decimal? OverdraftLimit, bool? IsActive, string? Error = null);

/// <summary>Reads CSV (UTF-8, ';' or ',' auto-detected, RFC 4180 quotes) or Excel (.xlsx, first worksheet) diner files.</summary>
public static class DinerImportReader
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["matricule"] = "EmployeeNumber",
        ["employeenumber"] = "EmployeeNumber",
        ["nom"] = "LastName",
        ["lastname"] = "LastName",
        ["prenom"] = "FirstName",
        ["prénom"] = "FirstName",
        ["firstname"] = "FirstName",
        ["categorie"] = "Category",
        ["catégorie"] = "Category",
        ["category"] = "Category",
        ["badge"] = "BadgeNumber",
        ["badgenumber"] = "BadgeNumber",
        ["numerobadge"] = "BadgeNumber",
        ["numérobadge"] = "BadgeNumber",
        ["typecompte"] = "AccountType",
        ["accounttype"] = "AccountType",
        ["decouvert"] = "OverdraftLimit",
        ["découvert"] = "OverdraftLimit",
        ["overdraftlimit"] = "OverdraftLimit",
        ["actif"] = "IsActive",
        ["isactive"] = "IsActive",
    };

    public static IReadOnlyList<DinerImportRow> Read(string fileName, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        var table = extension switch
        {
            ".xlsx" => ReadExcel(content),
            ".csv" or ".txt" => ReadCsv(content),
            _ => throw new DomainException("invalid_file", "Accepted formats: .csv (UTF-8) and .xlsx."),
        };
        return Map(table);
    }

    private static List<(int Line, string[] Cells)> ReadExcel(Stream content)
    {
        try
        {
            using var workbook = new XLWorkbook(content);
            var sheet = workbook.Worksheets.First();
            var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            return [.. sheet.RowsUsed().Select(r => (r.RowNumber(),
                Enumerable.Range(1, lastColumn).Select(c => r.Cell(c).GetFormattedString()).ToArray()))];
        }
        catch (Exception ex) when (ex is not DomainException)
        {
            throw new DomainException("invalid_file", "The Excel file could not be read.");
        }
    }

    private static List<(int Line, string[] Cells)> ReadCsv(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = firstLine.Count(c => c == ';') >= firstLine.Count(c => c == ',') ? ';' : ',';
        var rows = new List<(int, string[])>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var rowStart = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    cell.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    cells.Add(cell.ToString());
                    cell.Clear();
                    AddRow(rows, rowStart, cells);
                    cells = [];
                    line++;
                    rowStart = line;
                    break;
                default:
                    if (c == delimiter)
                    {
                        cells.Add(cell.ToString());
                        cell.Clear();
                    }
                    else
                    {
                        cell.Append(c);
                    }

                    break;
            }
        }

        if (inQuotes)
        {
            throw new DomainException("invalid_file", "Unterminated quoted field in the CSV file.");
        }

        cells.Add(cell.ToString());
        AddRow(rows, rowStart, cells);
        return rows;
    }

    private static void AddRow(List<(int, string[])> rows, int line, List<string> cells)
    {
        if (cells.Any(c => !string.IsNullOrWhiteSpace(c)))
        {
            rows.Add((line, [.. cells]));
        }
    }

    private static List<DinerImportRow> Map(List<(int Line, string[] Cells)> table)
    {
        if (table.Count == 0)
        {
            throw new DomainException("invalid_file", "The file is empty.");
        }

        var header = table[0].Cells.Select(h => Aliases.GetValueOrDefault(Normalize(h))).ToArray();
        foreach (var required in new[] { "EmployeeNumber", "LastName", "FirstName" })
        {
            if (!header.Contains(required))
            {
                throw new DomainException("invalid_file", "Missing required column: Matricule, Nom and Prénom are mandatory.");
            }
        }

        string? Get(string[] cells, string column)
        {
            var index = Array.IndexOf(header, column);
            return index >= 0 && index < cells.Length && !string.IsNullOrWhiteSpace(cells[index]) ? cells[index].Trim() : null;
        }

        return [.. table.Skip(1).Select(r =>
        {
            var overdraft = Get(r.Cells, "OverdraftLimit");
            var active = Get(r.Cells, "IsActive");
            var parsedOverdraft = ParseDecimal(overdraft);
            var parsedActive = ParseBool(active);
            var error = overdraft is not null && parsedOverdraft is null ? $"Découvert invalide : '{overdraft}'."
                : active is not null && parsedActive is null ? $"Valeur Actif invalide : '{active}' (oui/non)."
                : null;
            return new DinerImportRow(
                r.Line,
                Get(r.Cells, "EmployeeNumber"),
                Get(r.Cells, "LastName"),
                Get(r.Cells, "FirstName"),
                Array.IndexOf(header, "Category") >= 0 ? Get(r.Cells, "Category") ?? string.Empty : null,
                Get(r.Cells, "BadgeNumber"),
                Get(r.Cells, "AccountType"),
                parsedOverdraft,
                parsedActive,
                error);
        })];
    }

    private static string Normalize(string header) => header.Trim().Replace(" ", string.Empty, StringComparison.Ordinal)
        .Replace("_", string.Empty, StringComparison.Ordinal).Replace("﻿", string.Empty, StringComparison.Ordinal);

    private static decimal? ParseDecimal(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "1" or "oui" or "o" or "yes" or "y" or "true" or "vrai" or "x" => true,
        "0" or "non" or "n" or "no" or "false" or "faux" => false,
        _ => null,
    };
}
