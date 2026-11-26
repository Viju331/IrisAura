using System.Security.Cryptography;
using System.Text.Json;

// IrisAura.Tools: developer and release commands (BLUEPRINT §6). Camera commands are added in Phase 0B.
//
//   update-key create  [--name NAME]                       create a non-exportable ECDSA P-256 CNG key in your
//                                                          user profile and print its public key (SPKI, base64)
//   update-key public  [--name NAME]                       print the public key again
//   update-manifest write --msi FILE --version X.Y.Z --out MANIFEST
//   update-manifest sign  --manifest FILE [--name NAME]    write FILE.sig (ECDSA P-256 / SHA-256, base64)
//
// The private key never leaves the Windows key store (CNG, current user, export not allowed).

const string DefaultKey = "IrisAura Update Signing (Development)";

try
{
    return (args.ElementAtOrDefault(0), args.ElementAtOrDefault(1)) switch
    {
        ("update-key", "create") => CreateKey(Option("name") ?? DefaultKey),
        ("update-key", "public") => PrintPublic(Option("name") ?? DefaultKey),
        ("update-manifest", "write") => WriteManifest(Required("msi"), Required("version"), Required("out")),
        ("update-manifest", "sign") => SignManifest(Required("manifest"), Option("name") ?? DefaultKey),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is CryptographicException or IOException or ArgumentException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"IrisAura.Tools: {ex.Message}");
    return 1;
}

int Usage()
{
    Console.Error.WriteLine("usage: IrisAura.Tools update-key create|public [--name NAME] | update-manifest write --msi F --version V --out M | update-manifest sign --manifest M [--name NAME]");
    return 2;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string Required(string name) => Option(name) ?? throw new ArgumentException($"--{name} is required");

int CreateKey(string name)
{
    if (CngKey.Exists(name))
    {
        Console.Error.WriteLine($"Key '{name}' already exists; printing its public key.");
        return PrintPublic(name);
    }

    using var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
    {
        ExportPolicy = CngExportPolicies.None,
        KeyCreationOptions = CngKeyCreationOptions.None,
        Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
    });
    return PrintPublic(name);
}

int PrintPublic(string name)
{
    using var key = new ECDsaCng(CngKey.Open(name));
    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}

int WriteManifest(string msi, string version, string output)
{
    if (!Version.TryParse(version, out _))
    {
        throw new ArgumentException("--version must look like 1.2.3");
    }

    using var stream = File.OpenRead(msi);
    var manifest = new
    {
        product = "IrisAura.Island",
        version,
        file = Path.GetFileName(msi),
        sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)),
        size = new FileInfo(msi).Length,
    };
    File.WriteAllText(output, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Wrote {output}");
    return 0;
}

int SignManifest(string manifest, string name)
{
    using var key = new ECDsaCng(CngKey.Open(name));
    var signature = key.SignData(File.ReadAllBytes(manifest), HashAlgorithmName.SHA256);
    File.WriteAllText(manifest + ".sig", Convert.ToBase64String(signature));
    Console.WriteLine($"Wrote {manifest}.sig");
    return 0;
}
