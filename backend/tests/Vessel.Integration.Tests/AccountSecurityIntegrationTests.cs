using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vessel.Application.Credentials;
using Vessel.Domain.Credentials;
using Vessel.Infrastructure.Credentials;

namespace Vessel.Tests;

public sealed class AccountSecurityIntegrationTests
{
    [Fact]
    public void Aes_round_trip_random_nonce_and_row_binding()
    {
        using var vault = SecurityFixture.Vault();
        var owner = Guid.NewGuid(); var account = Guid.NewGuid();
        var first = vault.Seal(owner, account, "lighter-read-token", SecurityFixture.Token);
        var second = vault.Seal(owner, account, "lighter-read-token", SecurityFixture.Token);
        Assert.Equal(SecurityFixture.Token, vault.Open(owner, account, "lighter-read-token", first));
        Assert.Equal(12, first.Nonce.Length); Assert.Equal(16, first.Tag.Length);
        Assert.NotEqual(first.Nonce, second.Nonce); Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.DoesNotContain(SecurityFixture.Token, Encoding.UTF8.GetString(first.Ciphertext));
        Assert.Throws<CredentialStorageException>(() => vault.Open(Guid.NewGuid(), account, "lighter-read-token", first));
        Assert.Throws<CredentialStorageException>(() => vault.Open(owner, Guid.NewGuid(), "lighter-read-token", first));
        Assert.Throws<CredentialStorageException>(() => vault.Open(owner, account, "other-purpose", first));
        first.Tag[0] ^= 1;
        var error = Assert.Throws<CredentialStorageException>(() => vault.Open(owner, account, "lighter-read-token", first));
        Assert.Null(error.InnerException); Assert.DoesNotContain(SecurityFixture.Token, error.ToString());
    }

    [Fact]
    public void Rotation_keeps_old_keys_decrypt_only_and_unknown_keys_are_safe()
    {
        using var old = SecurityFixture.Vault();
        var owner = Guid.NewGuid(); var id = Guid.NewGuid();
        var envelope = old.Seal(owner, id, "lighter-read-token", SecurityFixture.Token);
        using var rotated = new AesGcmCredentialVault(new CredentialSettings
        {
            ActiveKeyId = "v2", Keys = new() { ["v1"] = SecurityFixture.Key, ["v2"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
        });
        Assert.Equal(SecurityFixture.Token, rotated.Open(owner, id, "lighter-read-token", envelope));
        Assert.Equal("v2", rotated.Seal(owner, id, "lighter-read-token", SecurityFixture.Token).KeyId);
        Assert.Throws<CredentialStorageException>(() => rotated.Open(owner, id, "lighter-read-token",
            new("lost", envelope.Nonce, envelope.Ciphertext, envelope.Tag)));
    }

    [Fact]
    public void Absent_key_is_unconfigured_but_partial_and_invalid_keys_fail_safely()
    {
        using var absent = new AesGcmCredentialVault(new());
        Assert.False(absent.IsConfigured);
        Assert.Throws<CredentialStorageException>(() => absent.Seal(Guid.NewGuid(), Guid.NewGuid(), "purpose", SecurityFixture.Token));
        foreach (var settings in new[]
        {
            new CredentialSettings { ActiveKeyId = "missing" },
            new CredentialSettings { Keys = new() { ["v1"] = SecurityFixture.Key } },
            new CredentialSettings { ActiveKeyId = "v1", Keys = new() { ["v1"] = SecurityFixture.Token } },
            new CredentialSettings { ActiveKeyId = "v1", Keys = new() { ["v1"] = Convert.ToBase64String(new byte[16]) } }
        })
        {
            var error = Assert.Throws<CredentialStorageException>(() => new AesGcmCredentialVault(settings));
            Assert.DoesNotContain(SecurityFixture.Token, error.ToString()); Assert.DoesNotContain(SecurityFixture.Key, error.ToString());
        }
    }

    [Fact]
    public void Startup_validation_does_not_echo_malformed_key()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Credentials:ActiveKeyId"] = "v1", ["Vessel:Credentials:Keys:v1"] = SecurityFixture.Token
        }).Build();
        var services = new ServiceCollection().AddVesselCredentials(config);
        using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.DoesNotContain(SecurityFixture.Token, error.ToString());
    }

    [Fact]
    public void Mode_600_file_works_and_loose_permissions_and_symlinks_fail()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "vessel-key-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "keys.json");
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new { ActiveKeyId = "v1", Keys = new Dictionary<string, string> { ["v1"] = SecurityFixture.Key } }));
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using (var vault = new AesGcmCredentialVault(new() { KeyFile = file })) Assert.True(vault.IsConfigured);
            var link = Path.Combine(directory, "link.json"); File.CreateSymbolicLink(link, file);
            Assert.Throws<CredentialStorageException>(() => new AesGcmCredentialVault(new() { KeyFile = link }));
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Throws<CredentialStorageException>(() => new AesGcmCredentialVault(new() { KeyFile = file }));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Key_file_restart_and_additive_rotation_preserve_existing_ciphertext()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "vessel-key-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "keys.json");
        var owner = Guid.NewGuid(); var account = Guid.NewGuid();
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                ActiveKeyId = "current", Keys = new Dictionary<string, string> { ["current"] = SecurityFixture.Key }
            }));
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            SealedCredential sealedToken;
            using (var initial = new AesGcmCredentialVault(new() { KeyFile = file }))
                sealedToken = initial.Seal(owner, account, "lighter-read-token", SecurityFixture.Token);
            using (var restarted = new AesGcmCredentialVault(new() { KeyFile = file }))
                Assert.Equal(SecurityFixture.Token, restarted.Open(owner, account, "lighter-read-token", sealedToken));

            // The provisioning source can change; only key IDs and bytes determine decryption.
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                ActiveKeyId = "next", Keys = new Dictionary<string, string>
                {
                    ["current"] = SecurityFixture.Key,
                    ["next"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                }
            }));
            using (var rotated = new AesGcmCredentialVault(new() { KeyFile = file }))
            {
                Assert.Equal(SecurityFixture.Token, rotated.Open(owner, account, "lighter-read-token", sealedToken));
                Assert.Equal("next", rotated.Seal(owner, account, "lighter-read-token", SecurityFixture.Token).KeyId);
            }
            File.Delete(file);
            Assert.Throws<CredentialStorageException>(() => new AesGcmCredentialVault(new() { KeyFile = file }));
            Assert.False(File.Exists(file)); // Startup must never replace a missing key.
        }
        finally { Directory.Delete(directory, true); }
    }
}
