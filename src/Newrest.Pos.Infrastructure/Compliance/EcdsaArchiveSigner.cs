using System.Security.Cryptography;
using Newrest.Pos.Application.Abstractions;
using Newrest.Pos.Application.Compliance;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Infrastructure.Compliance;

/// <summary>
/// ECDSA P-256 key of the fiscal archives (<c>Archive:SigningKeyPem</c>, PKCS#8 PEM, from the key vault). Without a key,
/// an ephemeral key is used only when <c>Archive:AllowEphemeralKey</c> is true (development, tests); otherwise signing
/// is refused. Generate a key: <c>openssl ecparam -name prime256v1 -genkey | openssl pkcs8 -topk8 -nocrypt</c>.
/// </summary>
public sealed class EcdsaArchiveSigner : IArchiveSigner, IDisposable
{
    private readonly ECDsa? _key;

    public EcdsaArchiveSigner(string? privateKeyPem, bool allowEphemeralKey)
    {
        if (!string.IsNullOrWhiteSpace(privateKeyPem))
        {
            _key = ECDsa.Create();
            _key.ImportFromPem(privateKeyPem);
            if (_key.KeySize != 256)
            {
                throw new InvalidOperationException("Archive:SigningKeyPem must be an ECDSA P-256 key.");
            }
        }
        else if (allowEphemeralKey)
        {
            _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            IsEphemeral = true;
        }
    }

    public bool IsEphemeral { get; }

    public string KeyId => _key is null ? "none" : ArchiveFormat.KeyIdOf(_key);

    public string PublicKeyPem => Key.ExportSubjectPublicKeyInfoPem();

    public byte[] Sign(byte[] data) => Key.SignData(data, HashAlgorithmName.SHA256);

    private ECDsa Key => _key ?? throw new DomainException("archive_key_missing", "No archive signing key configured (Archive:SigningKeyPem).");

    public void Dispose() => _key?.Dispose();
}
