using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WinLogRotate.Core.Secrets;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The whole failure matrix, on Linux. Splitting the cipher out behind IByteProtector is what
/// makes that possible: DPAPI throws PlatformNotSupportedException here, and it carries no
/// [SupportedOSPlatform] attribute, so CA1416 would not have warned about it either.
/// </summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-secret-");
    private readonly FakeTimeProvider _clock =
        new(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));

    private string Path => System.IO.Path.Combine(_dir.FullName, "secrets.dat");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Reversible, and deliberately not encryption. Naming it "test-insecure" means a file it
    /// wrote can never be mistaken for a real one: a shipping build refuses the scheme outright.
    /// </summary>
    private sealed class FakeProtector(byte key = 0x5A) : IByteProtector
    {
        public string Scheme => ProtectionScheme.Test;

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => Xor(plaintext);

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => Xor(ciphertext);

        private byte[] Xor(ReadOnlySpan<byte> input)
        {
            var output = input.ToArray();
            for (var i = 0; i < output.Length; i++)
            {
                output[i] ^= key;
            }

            return output;
        }
    }

    private SecretStore Load(IByteProtector? protector = null, string? entropyId = null, string? machine = null) =>
        SecretStore.Load(Path, protector ?? new FakeProtector(), entropyId, machine);

    [Fact]
    public void ASecretSurvivesASaveAndLoad()
    {
        Load().Set("ses-smtp", SecretString.From("hunter2"), "CONTOSO\\alice", _clock).Save(_clock);

        var store = Load();
        store.Status.ShouldBe(SecretStoreStatus.Ok);
        store.TryGet("ses-smtp", out var value, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        value.Reveal().ShouldBe("hunter2");
    }

    [Fact]
    public void TheMetadataAnsweredWhereDidThisComeFrom()
    {
        Load().Set("ses-smtp", SecretString.From("x"), "CONTOSO\\alice", _clock).Save(_clock);

        var meta = Load().Meta("ses-smtp").ShouldNotBeNull();
        meta.SetBy.ShouldBe("CONTOSO\\alice");
        meta.Created.ShouldBe(_clock.GetUtcNow());
    }

    [Fact]
    public void UpdatingASecretKeepsWhenItWasFirstCreated()
    {
        Load().Set("k", SecretString.From("one"), "alice", _clock).Save(_clock);
        var created = _clock.GetUtcNow();

        _clock.Advance(TimeSpan.FromDays(30));
        Load().Set("k", SecretString.From("two"), "bob", _clock).Save(_clock);

        var meta = Load().Meta("k").ShouldNotBeNull();
        meta.Created.ShouldBe(created);
        meta.Updated.ShouldBe(_clock.GetUtcNow());
        Load().TryGet("k", out var v, out _).ShouldBeTrue();
        v.Reveal().ShouldBe("two");
    }

    [Fact]
    public void NoFileIsNotAProblem()
    {
        var store = Load();
        store.Status.ShouldBe(SecretStoreStatus.Missing);
        store.Detail.ShouldBeNull();
        store.Names.ShouldBeEmpty();
    }

    [Fact]
    public void ACorruptStoreIsRefusedAndLeftOnDisk()
    {
        // The deliberate divergence from StateStore, which replaces a corrupt file and starts
        // fresh. Silently discarding credentials is unrecoverable; a rotation clock is not.
        File.WriteAllText(Path, "{ this is not json");

        var store = Load();
        store.Status.ShouldBe(SecretStoreStatus.Refused);
        store.Detail.ShouldNotBeNull();
        File.ReadAllText(Path).ShouldBe("{ this is not json");
    }

    [Fact]
    public void AnEmptyStoreIsRefusedRatherThanTreatedAsNoSecrets()
    {
        File.WriteAllText(Path, "null");

        Load().Status.ShouldBe(SecretStoreStatus.Refused);
    }

    [Fact]
    public void AStoreFromTheFutureIsRefused()
    {
        Write(new SecretDocument { Version = 2 });

        var store = Load();
        store.Status.ShouldBe(SecretStoreStatus.Refused);
        store.Detail.ShouldNotBeNull().ShouldContain("version 2");
    }

    [Fact]
    public void AStoreWrittenUnderAnotherSchemeIsRefused()
    {
        Write(new SecretDocument { Protection = ProtectionScheme.DpapiLocalMachine });

        var store = Load();
        store.Status.ShouldBe(SecretStoreStatus.Refused);
        store.Detail.ShouldNotBeNull().ShouldContain(ProtectionScheme.DpapiLocalMachine);
    }

    [Fact]
    public void AStoreFromAnotherMachineSaysSo_AndStillListsItsNames()
    {
        Load(machine: "aaaa").Set("ses-smtp", SecretString.From("x"), null, _clock).Save(_clock);

        var store = Load(machine: "bbbb");

        store.Status.ShouldBe(SecretStoreStatus.WrongMachine);
        store.Detail.ShouldNotBeNull().ShouldContain("different machine");

        // The names are the point: they are how an operator learns what to re-enter.
        store.Names.ShouldContain("ses-smtp");
        store.TryGet("ses-smtp", out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("different machine");
    }

    [Fact]
    public void AChangedMachineKeySaysThatInstead()
    {
        Load(entropyId: "1111", machine: "same").Set("k", SecretString.From("x"), null, _clock).Save(_clock);

        var store = Load(entropyId: "2222", machine: "same");

        store.Status.ShouldBe(SecretStoreStatus.KeyChanged);
        store.Detail.ShouldNotBeNull().ShouldContain("machine key has changed");
        store.Names.ShouldContain("k");
    }

    [Fact]
    public void OneUnreadableEntryDoesNotTakeTheOthersWithIt()
    {
        // The reason ciphertext is per entry rather than one envelope over the file.
        var good = Load().Set("good", SecretString.From("fine"), null, _clock);
        good.Save(_clock);

        var doc = Read();
        doc.Secrets["broken"] = new SecretEntry { Cipher = "!!!! not base64 !!!!" };
        Write(doc);

        var store = Load();
        store.Status.ShouldBe(SecretStoreStatus.Ok);

        store.TryGet("good", out var value, out _).ShouldBeTrue();
        value.Reveal().ShouldBe("fine");

        store.TryGet("broken", out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void AnEntryEncryptedWithADifferentKeyFailsAloneAndSaysNothingAboutTheValue()
    {
        SecretStore.Load(Path, new FakeProtector(0x11))
            .Set("k", SecretString.From("super-secret-value"), null, _clock).Save(_clock);

        var store = SecretStore.Load(Path, new FakeProtector(0x22));
        store.TryGet("k", out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldNotContain("super-secret-value");
    }

    [Fact]
    public void AMissingNameIsReportedByName()
    {
        Load().TryGet("nope", out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("nope");
    }

    [Fact]
    public void RemovingASecretLeavesTheRest()
    {
        Load().Set("a", SecretString.From("1"), null, _clock)
              .Set("b", SecretString.From("2"), null, _clock)
              .Save(_clock);

        Load().Remove("a").Save(_clock);

        var store = Load();
        store.Contains("a").ShouldBeFalse();
        store.Contains("b").ShouldBeTrue();
    }

    [Fact]
    public void NamesAreCaseInsensitive_BecauseNobodyRemembersWhichCaseTheyUsed()
    {
        Load().Set("SES-SMTP", SecretString.From("x"), null, _clock).Save(_clock);

        Load().Set("ses-smtp", SecretString.From("y"), null, _clock).Save(_clock);

        Load().Names.Count.ShouldBe(1);
    }

    [Fact]
    public void TheFileNeverContainsTheValue()
    {
        Load().Set("k", SecretString.From("super-secret-value"), "alice", _clock).Save(_clock);

        File.ReadAllText(Path).ShouldNotContain("super-secret-value");
    }

    [Fact]
    public void NothingIsWrittenIfTheFileCannotBeProtected()
    {
        // An unprotected secrets file is worse than a missing one, so a failure to apply the
        // permissions must leave no file at all - not one anybody can read.
        var store = Load().Set("k", SecretString.From("x"), null, _clock);

        Should.Throw<UnauthorizedAccessException>(() =>
            store.Save(_clock, harden: _ => throw new UnauthorizedAccessException("no")));

        File.Exists(Path).ShouldBeFalse();
        File.Exists(Path + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void TheHardeningIsAppliedBeforeTheFileTakesItsRealName()
    {
        // Order matters: a file created in the configuration directory inherits that
        // directory's "every local user may read" ACE, so permissions have to be set while it
        // is still the temporary file.
        string? hardened = null;
        Load().Set("k", SecretString.From("x"), null, _clock)
              .Save(_clock, harden: p => hardened = p);

        hardened.ShouldNotBeNull();
        hardened.ShouldEndWith(".tmp");
        File.Exists(Path).ShouldBeTrue();
    }

    [Fact]
    public void WithoutAProtectorNothingCanBeReadOrWritten()
    {
        Load().Set("k", SecretString.From("x"), null, _clock).Save(_clock);

        var store = SecretStore.Load(Path, protector: null);

        store.TryGet("k", out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("Windows");
        Should.Throw<PlatformNotSupportedException>(
            () => store.Set("k", SecretString.From("y"), null, _clock));
    }

    // The store's own source-generated context, not JsonSerializer's reflection path: the two
    // disagree about property naming, so a helper using the wrong one would write a file the
    // store cannot read and every assertion here would pass for the wrong reason.
    private SecretDocument Read() =>
        JsonSerializer.Deserialize(File.ReadAllText(Path), SecretJsonContext.Default.SecretDocument)!;

    private void Write(SecretDocument document) =>
        File.WriteAllText(Path, JsonSerializer.Serialize(document, SecretJsonContext.Default.SecretDocument));
}

/// <summary>
/// Padding removes the cheap signal that ciphertext length carries. It is not a serious defence
/// and is not offered as one - the file permissions are - but it costs one function.
/// </summary>
public sealed class PlaintextPaddingTests
{
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("hunter2")]
    [InlineData("a password with spaces and a trailing space ")]
    [InlineData("ünïcodé-påsswörd-中文")]
    public void WhatIsWrappedComesBackExactly(string value)
    {
        PlaintextPadding.TryUnwrap(PlaintextPadding.Wrap(value), out var round).ShouldBeTrue();
        round.ShouldBe(value);
    }

    [Fact]
    public void ShortAndLongSecretsLandInTheSameBucket()
    {
        PlaintextPadding.Wrap("abcd").Length.ShouldBe(PlaintextPadding.Wrap(new string('x', 40)).Length);
    }

    [Fact]
    public void ALongerSecretStillGrows_ItIsPaddingNotHiding()
    {
        PlaintextPadding.Wrap(new string('x', 200)).Length
            .ShouldBeGreaterThan(PlaintextPadding.Wrap("short").Length);
    }

    [Fact]
    public void EveryWrapDiffersEvenForTheSameInput()
    {
        // The padding is random, so identical secrets do not produce identical plaintext blocks.
        PlaintextPadding.Wrap("same").ShouldNotBe(PlaintextPadding.Wrap("same"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(65)]
    public void GarbageIsRejectedRatherThanDecoded(int length)
    {
        PlaintextPadding.TryUnwrap(new byte[length], out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(61)]
    public void AHostileLengthPrefixIsRejectedRatherThanThrowing(int claimed)
    {
        // These bytes come out of a decryption, so they can be anything - including a length
        // whose addition to the prefix size overflows to a negative number, which passes a
        // naive bounds check and then throws inside Slice.
        var evil = new byte[64];
        BitConverter.GetBytes(claimed).CopyTo(evil, 0);

        PlaintextPadding.TryUnwrap(evil, out var value).ShouldBeFalse();
        value.ShouldBe(string.Empty);
    }
}
