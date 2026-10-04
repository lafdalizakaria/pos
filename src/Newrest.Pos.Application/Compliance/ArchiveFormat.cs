using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Domain.Common;
using Newrest.Pos.Domain.Sales;

namespace Newrest.Pos.Application.Compliance;

/// <summary>Where a register's chain stops in an archive (and the next archive must continue).</summary>
public sealed record RegisterCheckpoint(Guid RegisterId, string Prefix, long FirstSequence, long LastSequence, string PreviousHash, string LastHash,
    int Tickets, int LastZNumber);

public sealed record ArchiveFileEntry(string Name, string Sha256, int Records);

/// <summary>manifest.json: what the archive contains; signed (manifest.sig) with the server's archive key.</summary>
public sealed record ArchiveManifest(int FormatVersion, Guid CompanyId, string CompanyCode, string LegalName, string Period, DateTimeOffset GeneratedAt,
    string GeneratedBy, string KeyId, IReadOnlyList<ArchiveFileEntry> Files, IReadOnlyList<RegisterCheckpoint> Registers);

/// <summary>
/// Archive layout (zip): manifest.json, manifest.sig (ECDSA P-256 / SHA-256, base64), signing-key.pem,
/// tickets.jsonl (sync form: every ticket can be rebuilt and its hash recomputed), references.jsonl (originals of
/// credit notes archived earlier), z-reports.jsonl, account-movements.jsonl, README.txt.
/// </summary>
public static class ArchiveFormat
{
    public const int Version = 1;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static byte[] JsonLines<T>(IEnumerable<T> records) =>
        Encoding.UTF8.GetBytes(string.Concat(records.Select(r => JsonSerializer.Serialize(r, Json) + "\n")));

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string KeyIdOf(ECDsa key) => Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16];

    /// <summary>Checks signature, file hashes, every ticket hash and every register's chain; optionally continuity with the previous archive.</summary>
    public static ArchiveVerificationDto Verify(Stream archive, string? expectedKeyId = null, IReadOnlyList<RegisterCheckpoint>? previous = null)
    {
        var issues = new List<string>();
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        byte[]? Read(string name)
        {
            var entry = zip.GetEntry(name);
            if (entry is null)
            {
                issues.Add($"Fichier manquant : {name}");
                return null;
            }

            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        var manifestBytes = Read("manifest.json");
        var signature = Read("manifest.sig");
        var pem = Read("signing-key.pem");
        if (manifestBytes is null || signature is null || pem is null)
        {
            return new ArchiveVerificationDto(false, null, 0, 0, 0, issues);
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(Encoding.UTF8.GetString(pem));
        var keyId = KeyIdOf(key);
        if (!key.VerifyData(manifestBytes, Convert.FromBase64String(Encoding.ASCII.GetString(signature).Trim()), HashAlgorithmName.SHA256))
        {
            issues.Add("Signature du manifeste invalide.");
        }

        if (expectedKeyId is not null && !string.Equals(expectedKeyId, keyId, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add($"Clé de signature inattendue ({keyId}, attendu {expectedKeyId}).");
        }

        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(manifestBytes, Json)!;
        if (manifest.KeyId != keyId)
        {
            issues.Add("Le manifeste annonce une autre clé que celle fournie.");
        }

        var files = new Dictionary<string, byte[]>();
        foreach (var file in manifest.Files)
        {
            if (Read(file.Name) is { } data)
            {
                files[file.Name] = data;
                if (Sha256(data) != file.Sha256)
                {
                    issues.Add($"Empreinte de {file.Name} différente du manifeste (fichier modifié).");
                }
            }
        }

        var tickets = Lines<TicketSyncDto>(files.GetValueOrDefault("tickets.jsonl"));
        var references = Lines<TicketSyncDto>(files.GetValueOrDefault("references.jsonl")).ToDictionary(t => t.Id);
        var zReports = Lines<JsonElement>(files.GetValueOrDefault("z-reports.jsonl")).Count;
        var movements = Lines<JsonElement>(files.GetValueOrDefault("account-movements.jsonl")).Count;

        foreach (var checkpoint in manifest.Registers)
        {
            var own = tickets.Where(t => t.Number.StartsWith(checkpoint.Prefix + "-", StringComparison.Ordinal)).OrderBy(t => t.Sequence).ToList();
            var rebuilt = new Dictionary<Guid, Ticket>();
            var domainTickets = new List<Ticket>();
            foreach (var dto in own)
            {
                try
                {
                    var ticket = dto.Kind == nameof(TicketKind.CreditNote)
                        ? dto.RebuildCreditNote(Original(dto.CreditedTicketId!.Value), checkpoint.Prefix)
                        : dto.RebuildSale(checkpoint.RegisterId, checkpoint.Prefix);
                    if (ticket.Hash != dto.Hash)
                    {
                        issues.Add($"{dto.Number} : contenu modifié (empreinte recalculée différente).");
                    }

                    rebuilt[ticket.Id] = ticket;
                    domainTickets.Add(ticket);
                }
                catch (DomainException ex)
                {
                    issues.Add($"{dto.Number} : ticket invalide ({ex.Code}).");
                }
            }

            Ticket Original(Guid id) => rebuilt.TryGetValue(id, out var t) ? t
                : references.TryGetValue(id, out var r) ? r.RebuildSale(checkpoint.RegisterId, checkpoint.Prefix)
                : throw new DomainException("missing_original", "Original ticket absent.");

            var chain = TicketChainVerifier.Verify(checkpoint.RegisterId, domainTickets, checkpoint.FirstSequence, checkpoint.PreviousHash);
            issues.AddRange(chain.Issues.Select(i => $"{checkpoint.Prefix} n° {i.Sequence} : {i.Kind}"));
            if (own.Count != checkpoint.Tickets || (own.Count > 0 && (own[0].Sequence != checkpoint.FirstSequence || own[^1].Sequence != checkpoint.LastSequence
                                                                       || own[^1].Hash != checkpoint.LastHash)))
            {
                issues.Add($"{checkpoint.Prefix} : tickets différents du point de contrôle du manifeste.");
            }

            if (previous?.FirstOrDefault(p => p.RegisterId == checkpoint.RegisterId) is { } before && checkpoint.Tickets > 0
                && (checkpoint.FirstSequence != before.LastSequence + 1 || checkpoint.PreviousHash != before.LastHash))
            {
                issues.Add($"{checkpoint.Prefix} : discontinuité avec l'archive précédente (n° {before.LastSequence} → {checkpoint.FirstSequence}).");
            }
        }

        return new ArchiveVerificationDto(issues.Count == 0, keyId, tickets.Count, zReports, movements, issues);
    }

    private static List<T> Lines<T>(byte[]? data) => data is null ? []
        : [.. Encoding.UTF8.GetString(data).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonSerializer.Deserialize<T>(l, Json)!)];
}
