namespace LicensingCore;

/// <summary>The signed contents of a license key -- which device it's for,
/// when it stops working, when it was issued, and who it's for. Shared
/// between the vendor's LicenseGenerator (which builds one from a device ID
/// + expiry) and the product's own LicenseValidator (which reads one back
/// after verifying its signature), so both sides always agree on the exact
/// shape being signed.</summary>
public record LicensePayload(string DeviceId, DateTime ExpiryUtc, DateTime IssuedUtc, string? Customer);
