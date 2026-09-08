using System.Security.Cryptography;

namespace LicensingCore;

/// <summary>ECDSA (P-256) keypair helpers, shared so the vendor-side
/// generator and the product's validator import/export keys the exact same
/// way. The private key must only ever exist on the vendor's own machine --
/// it signs license keys. The public key is safe to embed in the shipped
/// product -- it can only verify, never issue.</summary>
public static class KeyPairFormat
{
    public static ECDsa GenerateKeyPair() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string ExportPrivateKeyBase64(ECDsa key) =>
        Convert.ToBase64String(key.ExportPkcs8PrivateKey());

    public static string ExportPublicKeyBase64(ECDsa key) =>
        Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    public static ECDsa ImportPrivateKeyBase64(string base64)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(base64), out _);
        return key;
    }

    public static ECDsa ImportPublicKeyBase64(string base64)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64), out _);
        return key;
    }
}
