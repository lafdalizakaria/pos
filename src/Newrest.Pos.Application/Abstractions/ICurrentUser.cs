namespace Newrest.Pos.Application.Abstractions;

/// <summary>Authenticated caller: a back-office user (Entra ID) or a register (device credential).</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>UPN for people, <c>register:{id}</c> for registers. Used as audit actor.</summary>
    string Name { get; }

    IReadOnlyCollection<string> Roles { get; }

    Guid? RegisterId { get; }

    string? IpAddress { get; }

    string? CorrelationId { get; }

    bool IsInRole(string role) => Roles.Contains(role);
}

/// <summary>Reference photo and dataset storage (local disk now, blob storage later).</summary>
public interface IFileStorage
{
    Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Signs fiscal archives (ECDSA P-256; private key held by the server configuration / key vault).</summary>
public interface IArchiveSigner
{
    /// <summary>First 16 hex characters of the SHA-256 of the public key (SubjectPublicKeyInfo).</summary>
    string KeyId { get; }

    string PublicKeyPem { get; }

    byte[] Sign(byte[] data);
}
