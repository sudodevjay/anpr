using System.Security.Cryptography;
using LicensingCore;

// Vendor-only tool: this is what generates license keys for customer
// installs of UvssService. It must NEVER be shipped to a customer -- it's
// the only place the private signing key exists. Keep vendor_private.key
// backed up somewhere safe; if it's lost, no more license keys can ever be
// issued for licenses already signed with it (customers already licensed
// keep working -- the product only needs the public key to verify).

const string DefaultKeyFile = "vendor_private.key";
const string DateFormat = "yyyy-MM-dd";
const string DateExample = "2026-12-31";

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

switch (args[0].ToLowerInvariant())
{
    case "keygen":
        return CmdKeyGen(args);
    case "issue":
        return CmdIssue(args);
    case "verify":
        return CmdVerify(args);
    default:
        PrintUsage();
        return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        UvssService License Generator (vendor tool -- keep this off customer machines)

        Usage:
          dotnet run -- keygen [keyFile]
              Generates a new ECDSA keypair. Writes the private key to keyFile
              (default: vendor_private.key) and prints the public key -- paste
              that into UvssService's Licensing/LicenseValidator.cs once.
              Only ever run this ONCE per product; re-running it invalidates
              every license key issued with the old key.

          dotnet run -- issue [deviceId] [expiryDate] [customerName] [keyFile]
              Issues a license key for a given customer Device ID (shown on
              that customer's /license page), valid through the given expiry
              date (format: yyyy-MM-dd, e.g. 2026-12-31 -- valid through the
              end of that day). Prints the license key to give back to the
              customer. Leave deviceId/expiryDate off and it will ask for
              them interactively, with an example shown.

          dotnet run -- verify <licenseKey> <deviceId> [keyFile]
              Locally checks a license key the same way the product will --
              useful to sanity-check one before sending it out.
        """);
}

static int CmdKeyGen(string[] args)
{
    var keyFile = args.ElementAtOrDefault(1) ?? DefaultKeyFile;
    if (File.Exists(keyFile))
    {
        Console.WriteLine($"'{keyFile}' already exists -- refusing to overwrite an existing signing key.");
        Console.WriteLine("Delete it yourself first if you really intend to replace it (this invalidates every license issued with it).");
        return 1;
    }

    using var key = KeyPairFormat.GenerateKeyPair();
    File.WriteAllText(keyFile, KeyPairFormat.ExportPrivateKeyBase64(key));
    Console.WriteLine($"Private key written to '{keyFile}'. KEEP THIS FILE SECRET AND BACKED UP.");
    Console.WriteLine();
    Console.WriteLine("Public key (paste this into UvssService/Licensing/LicenseValidator.cs's PublicKeyBase64 constant):");
    Console.WriteLine();
    Console.WriteLine(KeyPairFormat.ExportPublicKeyBase64(key));
    return 0;
}

static int CmdIssue(string[] args)
{
    var keyFile = args.ElementAtOrDefault(4) ?? DefaultKeyFile;
    if (!File.Exists(keyFile))
    {
        Console.WriteLine($"'{keyFile}' not found -- run 'dotnet run -- keygen' first.");
        return 1;
    }

    var interactive = false;

    var deviceId = args.ElementAtOrDefault(1);
    if (string.IsNullOrWhiteSpace(deviceId))
    {
        interactive = true;
        Console.Write("Enter the customer's Device ID (shown on their /license page): ");
        deviceId = Console.ReadLine();
    }
    if (string.IsNullOrWhiteSpace(deviceId))
    {
        Console.WriteLine("Device ID is required.");
        return 1;
    }

    var expiryText = args.ElementAtOrDefault(2);
    if (string.IsNullOrWhiteSpace(expiryText))
    {
        interactive = true;
        Console.Write($"Enter the expiry date, format {DateFormat} (e.g. {DateExample}): ");
        expiryText = Console.ReadLine();
    }
    if (!DateTime.TryParseExact(expiryText?.Trim(), DateFormat, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var expiryDate))
    {
        Console.WriteLine($"'{expiryText}' isn't a valid date -- expected format {DateFormat}, e.g. {DateExample}.");
        return 1;
    }

    var customer = args.ElementAtOrDefault(3);
    if (customer == null && interactive)
    {
        Console.Write("Enter customer name (optional, press Enter to skip): ");
        var entered = Console.ReadLine();
        customer = string.IsNullOrWhiteSpace(entered) ? null : entered;
    }

    using var privateKey = KeyPairFormat.ImportPrivateKeyBase64(File.ReadAllText(keyFile).Trim());
    var now = DateTime.UtcNow;
    // Valid through the END of the given day (UTC), not the start of it --
    // so "2026-12-31" covers all of Dec 31st, not just midnight.
    var expiryUtc = DateTime.SpecifyKind(expiryDate.Date, DateTimeKind.Utc).AddDays(1).AddTicks(-1);
    var payload = new LicensePayload(deviceId.Trim(), expiryUtc, now, customer);
    var licenseKey = LicenseCodec.Issue(payload, privateKey);

    Console.WriteLine();
    Console.WriteLine($"Device ID:  {payload.DeviceId}");
    Console.WriteLine($"Customer:   {payload.Customer ?? "(none)"}");
    Console.WriteLine($"Issued:     {payload.IssuedUtc:yyyy-MM-dd HH:mm} UTC");
    Console.WriteLine($"Expires:    {payload.ExpiryUtc:yyyy-MM-dd HH:mm} UTC (through {expiryDate:yyyy-MM-dd})");
    Console.WriteLine();
    Console.WriteLine("License key:");
    Console.WriteLine(licenseKey);
    return 0;
}

static int CmdVerify(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: dotnet run -- verify <licenseKey> <deviceId> [keyFile]");
        return 1;
    }
    var licenseKey = args[1];
    var deviceId = args[2];
    var keyFile = args.ElementAtOrDefault(3) ?? DefaultKeyFile;
    if (!File.Exists(keyFile))
    {
        Console.WriteLine($"'{keyFile}' not found -- run 'dotnet run -- keygen' first.");
        return 1;
    }

    using var privateKey = KeyPairFormat.ImportPrivateKeyBase64(File.ReadAllText(keyFile).Trim());
    if (!LicenseCodec.TryVerify(licenseKey, privateKey, out var payload, out var error) || payload == null)
    {
        Console.WriteLine($"INVALID -- {error}");
        return 1;
    }

    Console.WriteLine($"Signature: OK");
    Console.WriteLine($"Device ID: {payload.DeviceId} ({(string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) ? "MATCHES" : "DOES NOT MATCH given device id")})");
    Console.WriteLine($"Customer:  {payload.Customer ?? "(none)"}");
    Console.WriteLine($"Expires:   {payload.ExpiryUtc:yyyy-MM-dd HH:mm} UTC ({(DateTime.UtcNow > payload.ExpiryUtc ? "EXPIRED" : "still valid")})");
    return 0;
}
