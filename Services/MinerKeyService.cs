using System.Text;
using NSec.Cryptography;
using Qadopoolminer.Infrastructure;

namespace Qadopoolminer.Services;

public sealed class MinerKeyService
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    public (string PrivateKeyHex, string PublicKeyHex) GenerateEd25519KeyPair()
    {
        using var key = Key.Create(Algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });

        var privateKey = key.Export(KeyBlobFormat.RawPrivateKey);
        var publicKey = key.Export(KeyBlobFormat.RawPublicKey);
        return (HexUtility.ToLowerHex(privateKey), HexUtility.ToLowerHex(publicKey));
    }

    public string NormalizePrivateKey(string privateKeyHex)
        => HexUtility.NormalizeLower(privateKeyHex, 32);

    public string DerivePublicKey(string privateKeyHex)
    {
        var privateKeyBytes = HexUtility.Parse(privateKeyHex, 32);
        using var key = Key.Import(
            Algorithm,
            privateKeyBytes,
            KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters
            {
                ExportPolicy = KeyExportPolicies.AllowPlaintextExport
            });

        var publicKey = key.Export(KeyBlobFormat.RawPublicKey);
        return HexUtility.ToLowerHex(publicKey);
    }

    public string SignMessage(string privateKeyHex, string message)
    {
        var privateKeyBytes = HexUtility.Parse(privateKeyHex, 32);
        using var key = Key.Import(
            Algorithm,
            privateKeyBytes,
            KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters
            {
                ExportPolicy = KeyExportPolicies.AllowPlaintextExport
            });

        var signature = Algorithm.Sign(key, Encoding.UTF8.GetBytes(message));
        return HexUtility.ToLowerHex(signature);
    }
}
