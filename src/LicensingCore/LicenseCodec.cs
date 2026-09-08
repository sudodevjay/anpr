using System.Security.Cryptography;
using System.Text;

namespace LicensingCore;

/// <summary>Turns a LicensePayload into a license key string (signed with
/// the vendor's private key) and back (verified against the product's
/// embedded public key). A license key is "{payload}.{signature}", both
/// base64url -- the payload is a plain pipe-delimited string, not JSON, so
/// there's no serializer-version mismatch risk between the generator and
/// the validator ever silently changing what bytes get signed.</summary>
public static class LicenseCodec
{
    private const string FormatVersion = "v1";

    /// <summary>The exact bytes that get signed -- deliberately a fixed,
    /// simple format (not JSON) so both sides always agree byte-for-byte on
    /// what was signed. Customer name is last and unbounded (Split with a
    /// count limit below) so a literal "|" in a customer name can't corrupt
    /// the other fields.</summary>
    private static string BuildSignedString(LicensePayload p) =>
        $"{FormatVersion}|{p.DeviceId}|{p.ExpiryUtc:O}|{p.IssuedUtc:O}|{p.Customer ?? ""}";

    public static string Issue(LicensePayload payload, ECDsa privateKey)
    {
        var signedBytes = Encoding.UTF8.GetBytes(BuildSignedString(payload));
        var signature = privateKey.SignData(signedBytes, HashAlgorithmName.SHA256);
        return $"{Base64UrlEncode(signedBytes)}.{Base64UrlEncode(signature)}";
    }

    public static bool TryVerify(string? licenseKey, ECDsa publicKey, out LicensePayload? payload, out string error)
    {
        payload = null;
        error = "";

        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            error = "Empty license key.";
            return false;
        }

        var parts = licenseKey.Trim().Split('.');
        if (parts.Length != 2)
        {
            error = "Malformed license key (expected two dot-separated parts).";
            return false;
        }

        byte[] signedBytes, signature;
        try
        {
            signedBytes = Base64UrlDecode(parts[0]);
            signature = Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            error = "Malformed license key (not valid base64url).";
            return false;
        }

        if (!publicKey.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256))
        {
            error = "Signature does not match -- this key was not issued for this product, or has been tampered with.";
            return false;
        }

        var signed = Encoding.UTF8.GetString(signedBytes);
        var fields = signed.Split('|', 5);
        if (fields.Length < 5 || fields[0] != FormatVersion)
        {
            error = "Unrecognized license format.";
            return false;
        }
        if (!DateTime.TryParse(fields[2], null, System.Globalization.DateTimeStyles.RoundtripKind, out var expiryUtc) ||
            !DateTime.TryParse(fields[3], null, System.Globalization.DateTimeStyles.RoundtripKind, out var issuedUtc))
        {
            error = "Corrupt license payload.";
            return false;
        }

        payload = new LicensePayload(fields[1], expiryUtc, issuedUtc, fields[4].Length == 0 ? null : fields[4]);
        return true;
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s,
        };
        return Convert.FromBase64String(s);
    }
}
