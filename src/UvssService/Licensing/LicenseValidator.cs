using System.Security.Cryptography;
using LicensingCore;

namespace UvssService.Licensing;

public enum LicenseState { NotLicensed, Valid, Expired, DeviceMismatch, Invalid }

public record LicenseStatus(LicenseState State, DateTime? ExpiryUtc, string? Customer, string Message)
{
    public bool IsValid => State == LicenseState.Valid;
}

/// <summary>Verifies a license key against this build's embedded public
/// key -- the counterpart to the vendor-only LicenseGenerator tool (see
/// src/LicenseGenerator), which holds the private key and is never shipped
/// to a customer. This class can only ever confirm a key was signed by the
/// vendor; it has no way to produce one.</summary>
public class LicenseValidator
{
    // Generated once via `dotnet run --project src/LicenseGenerator -- keygen`
    // and pasted in here. Safe to embed -- a public key can verify license
    // keys but can never be used to forge one.
    private const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE4rY2jhWQVahI9+O/N1r+TjNaTCUjzYm1N8nn0fJTqQB99ZbrFFfYMmOXOmWAiNTcjHOdwKYLSomNbcDPRVry1g==";

    private readonly ECDsa _publicKey = KeyPairFormat.ImportPublicKeyBase64(PublicKeyBase64);

    public LicenseStatus Validate(string? licenseKey, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            return new LicenseStatus(LicenseState.NotLicensed, null, null, "No license key installed yet.");
        }

        if (!LicenseCodec.TryVerify(licenseKey, _publicKey, out var payload, out var error) || payload == null)
        {
            return new LicenseStatus(LicenseState.Invalid, null, null, $"License key is invalid -- {error}");
        }

        if (!string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            return new LicenseStatus(LicenseState.DeviceMismatch, payload.ExpiryUtc, payload.Customer,
                $"This license was issued for a different device ({payload.DeviceId}) -- this machine's Device ID is {deviceId}.");
        }

        if (DateTime.UtcNow > payload.ExpiryUtc)
        {
            return new LicenseStatus(LicenseState.Expired, payload.ExpiryUtc, payload.Customer,
                $"License expired on {payload.ExpiryUtc:yyyy-MM-dd} (UTC).");
        }

        return new LicenseStatus(LicenseState.Valid, payload.ExpiryUtc, payload.Customer,
            $"Licensed until {payload.ExpiryUtc:yyyy-MM-dd} (UTC).");
    }
}
