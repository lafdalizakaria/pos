using Microsoft.Extensions.Configuration;

namespace Newrest.Pos.Infrastructure.Hosting;

public enum ServerHost
{
    Api,
    BackOffice,
}

/// <summary>
/// Configuration checks run at startup in Production: the process refuses to start with a development setting or a
/// missing secret, instead of failing later (or silently running insecurely).
/// </summary>
public static class ProductionReadiness
{
    public sealed record Report(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

    public static Report Check(IConfiguration configuration, ServerHost host)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<string>();
        var warnings = new List<string>();
        string? Value(string key) => string.IsNullOrWhiteSpace(configuration[key]) ? null : configuration[key];

        var connection = configuration.GetConnectionString(DependencyInjection.ConnectionStringName) ?? "";
        var lowered = connection.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        if (connection.Length == 0)
        {
            errors.Add("ConnectionStrings:PosDb est absente.");
        }
        else
        {
            if (lowered.Contains("userid=sa;", StringComparison.Ordinal) || lowered.Contains("uid=sa;", StringComparison.Ordinal))
            {
                errors.Add("Le compte SQL 'sa' est interdit : utiliser pos_api (deploy/sql/least-privilege.sql).");
            }

            if (lowered.Contains("trustservercertificate=true", StringComparison.Ordinal) || lowered.Contains("encrypt=false", StringComparison.Ordinal))
            {
                errors.Add("Connexion SQL non chiffrée ou certificat non vérifié (Encrypt=True, TrustServerCertificate=False attendus).");
            }
        }

        if (Value("Storage:RootPath") is null)
        {
            errors.Add("Storage:RootPath est absent (photos, modèles, archives).");
        }

        if (int.TryParse(Value("Security:PinHashIterations"), out var iterations) && iterations < 600_000)
        {
            errors.Add("Security:PinHashIterations doit être au moins 600 000 en production.");
        }

        if (Value("Archive:SigningKeyPem") is null)
        {
            errors.Add("Archive:SigningKeyPem est absente (signature des archives fiscales).");
        }

        if (string.Equals(Value("Archive:AllowEphemeralKey"), "true", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Archive:AllowEphemeralKey est interdit en production.");
        }

        if (host == ServerHost.Api)
        {
            if ((Value("Authentication:Registers:SigningKey")?.Length ?? 0) < 32)
            {
                errors.Add("Authentication:Registers:SigningKey doit contenir au moins 32 caractères aléatoires.");
            }

            if (Value("Authentication:Users:Authority") is null || Value("Authentication:Users:Audience") is null)
            {
                errors.Add("Authentication:Users:Authority et Audience (Entra ID) sont requis.");
            }

            if (Value("Authentication:Users:DevSigningKey") is not null)
            {
                errors.Add("Authentication:Users:DevSigningKey est interdit en production.");
            }

            if (Value("Supervision:WebhookUrl") is null)
            {
                warnings.Add("Supervision:WebhookUrl absent : les alertes ne seront visibles que dans le back-office.");
            }

            if (string.Equals(Value("Supervision:JobsEnabled"), "false", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("Supervision:JobsEnabled=false : vérifier qu'une autre instance exécute les tâches de supervision.");
            }
        }
        else
        {
            if (Value("Authentication:Authority") is null || Value("Authentication:ClientId") is null || Value("Authentication:ClientSecret") is null)
            {
                errors.Add("Authentication:Authority, ClientId et ClientSecret (Entra ID) sont requis.");
            }

            if (string.Equals(Value("Authentication:DevLoginEnabled"), "true", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Authentication:DevLoginEnabled est interdit en production.");
            }
        }

        if (Value("OTEL_EXPORTER_OTLP_ENDPOINT") is null)
        {
            warnings.Add("OTEL_EXPORTER_OTLP_ENDPOINT absent : traces et métriques non exportées.");
        }

        return new Report(errors, warnings);
    }

    /// <summary>Throws with every problem at once (the operator fixes them in one go).</summary>
    public static IReadOnlyList<string> EnsureReady(IConfiguration configuration, ServerHost host)
    {
        var report = Check(configuration, host);
        if (report.Errors.Count > 0)
        {
            throw new InvalidOperationException("Configuration de production invalide :\n - " + string.Join("\n - ", report.Errors));
        }

        return report.Warnings;
    }
}
