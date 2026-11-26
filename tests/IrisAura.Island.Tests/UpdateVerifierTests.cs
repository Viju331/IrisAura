using System.IO;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IrisAura.Island.Update;

namespace IrisAura.Island.Tests;

/// <summary>Update verification (BLUEPRINT §12.5, Release 1): signed manifest, product, version, hash, installer signer.</summary>
public sealed class UpdateVerifierTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("irisaura-update-").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _msi;

    public UpdateVerifierTests()
    {
        _msi = Path.Combine(_dir, "IrisAura.Island-1.2.0-x64.msi");
        File.WriteAllBytes(_msi, Encoding.ASCII.GetBytes("pretend this is an MSI"));
    }

    private byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private byte[] Manifest(string version = "1.2.0", string product = UpdateVerifier.ProductName, string? sha = null, long? size = null)
    {
        var hash = sha ?? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_msi)));
        var json = $$"""{"product":"{{product}}","version":"{{version}}","file":"{{Path.GetFileName(_msi)}}","sha256":"{{hash}}","size":{{size ?? new FileInfo(_msi).Length}}}""";
        return Encoding.UTF8.GetBytes(json);
    }

    private UpdateCheck Verify(byte[] manifest, byte[]? signature = null, string? authenticodeProblem = null, Version? installed = null) =>
        UpdateVerifier.Verify(manifest, signature ?? _key.SignData(manifest, HashAlgorithmName.SHA256), _msi, installed ?? new Version(1, 0, 0),
            [PublicKey], _ => authenticodeProblem);

    [Fact]
    public void A_signed_newer_matching_and_trusted_update_is_valid()
    {
        var check = Verify(Manifest());
        check.Verdict.Should().Be(UpdateVerdict.Valid, check.Detail);
        check.Manifest!.Version.Should().Be("1.2.0");
    }

    [Fact]
    public void Manifest_signed_by_another_key_or_edited_after_signing_is_rejected()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Manifest();
        Verify(manifest, other.SignData(manifest, HashAlgorithmName.SHA256)).Verdict.Should().Be(UpdateVerdict.BadManifestSignature);

        var signature = _key.SignData(manifest, HashAlgorithmName.SHA256);
        var edited = Manifest(version: "9.9.9");
        Verify(edited, signature).Verdict.Should().Be(UpdateVerdict.BadManifestSignature);
        Verify(manifest, [1, 2, 3]).Verdict.Should().Be(UpdateVerdict.BadManifestSignature);
    }

    [Fact]
    public void Wrong_product_older_or_same_version_and_broken_manifests_are_rejected()
    {
        Verify(Manifest(product: "Something.Else")).Verdict.Should().Be(UpdateVerdict.WrongProduct);
        Verify(Manifest(version: "1.0.0")).Verdict.Should().Be(UpdateVerdict.NotNewer);
        Verify(Manifest(version: "0.9.0")).Verdict.Should().Be(UpdateVerdict.NotNewer, "no downgrades");
        Verify(Encoding.UTF8.GetBytes("{\"product\":\"IrisAura.Island\"}")).Verdict.Should().Be(UpdateVerdict.BadManifest);
        Verify(Encoding.UTF8.GetBytes("not json")).Verdict.Should().Be(UpdateVerdict.BadManifest);
    }

    [Fact]
    public void A_changed_installer_is_rejected_by_hash_or_size()
    {
        var manifest = Manifest();
        File.AppendAllText(_msi, "!");
        Verify(manifest).Verdict.Should().Be(UpdateVerdict.HashMismatch, "size differs");

        var sameSize = Manifest(sha: new string('0', 64));
        Verify(sameSize).Verdict.Should().Be(UpdateVerdict.HashMismatch, "hash differs");
    }

    [Fact]
    public void An_installer_with_an_untrusted_signature_is_rejected_last()
    {
        var check = Verify(Manifest(), authenticodeProblem: "The installer is not signed.");
        check.Verdict.Should().Be(UpdateVerdict.UntrustedInstaller);
        check.Detail.Should().Contain("not signed");
    }

    [Fact]
    public void Unsigned_installer_fails_the_authenticode_check()
    {
        Authenticode.Check(_msi, []).Should().Be("The installer is not signed.");
    }

    [Fact]
    public void Release_builds_do_not_trust_the_development_update_key()
    {
#if IRISAURA_DEV_TRUST
        UpdateKeys.All.Should().NotBeEmpty();
#else
        UpdateKeys.All.Should().BeEmpty("the development key is trusted only in builds made with TrustDevelopmentKeys");
#endif
        UpdateVerifier.VerifySignature(Convert.FromBase64String(UpdateKeys.Development), [1], [2]).Should().BeFalse();
    }
}
