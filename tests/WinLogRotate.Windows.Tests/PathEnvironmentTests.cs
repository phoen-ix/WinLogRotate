using Microsoft.Win32;
using Shouldly;
using WinLogRotate.Hosting.Hosts;
using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// The PATH round trip keeps the registry value's type and its unexpanded text.
/// </summary>
/// <remarks>
/// <para>
/// <c>host path-add --machine</c> used to read the machine <c>Path</c> through
/// <c>Environment.GetEnvironmentVariable</c>, which expands every <c>%VAR%</c>, and write it back
/// through <c>Environment.SetEnvironmentVariable</c>, which writes <c>REG_SZ</c>. Every all-users
/// install therefore turned <c>%SystemRoot%\system32</c> into hard-coded text, and the installer's
/// smoke test could not see it because it compares expanded values.
/// </para>
/// <para>
/// Driven against a scratch value name under <c>HKCU\Environment</c>, never the real
/// <c>Path</c>: a test that edits the developer's own PATH is a test nobody runs twice.
/// </para>
/// </remarks>
public sealed class PathEnvironmentTests : IDisposable
{
    private readonly string _name = $"WinLogRotate-test-{Guid.NewGuid():N}";

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
        key?.DeleteValue(_name, throwOnMissingValue: false);
    }

    private void Seed(string value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
        key.ShouldNotBeNull();
        key.SetValue(_name, value, kind);
    }

    private (string Raw, RegistryValueKind Kind) Stored()
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: false);
        key.ShouldNotBeNull();
        // "as string" rather than a cast: the MultiString case below reads back a string[], and
        // only its kind is asked for.
        var raw = key.GetValue(_name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
        return (raw, key.GetValueKind(_name));
    }

    [Fact]
    public void AnExpandStringComesBackUnexpandedAndGoesBackAsAnExpandString()
    {
        WindowsOnly.Require();
        Seed(@"%SystemRoot%\alpha;C:\beta", RegistryValueKind.ExpandString);

        var read = PathEnvironment.Read(EnvironmentVariableTarget.User, _name);

        read.IsText.ShouldBeTrue();
        read.Kind.ShouldBe(RegistryValueKind.ExpandString);
        read.Value.ShouldBe(@"%SystemRoot%\alpha;C:\beta", "the text as stored, not as expanded");

        var (edited, changed) = PathEdit.Apply(read.Value, @"C:\gamma", add: true);
        changed.ShouldBeTrue();

        PathEnvironment.Write(EnvironmentVariableTarget.User, read with { Value = edited }, _name);

        var (raw, kind) = Stored();
        kind.ShouldBe(RegistryValueKind.ExpandString, "the type that was there is the type that stays");
        raw.ShouldBe(@"%SystemRoot%\alpha;C:\beta;C:\gamma");
    }

    [Fact]
    public void APlainStringStaysAPlainString()
    {
        WindowsOnly.Require();
        Seed(@"C:\alpha", RegistryValueKind.String);

        var read = PathEnvironment.Read(EnvironmentVariableTarget.User, _name);
        read.Kind.ShouldBe(RegistryValueKind.String);

        PathEnvironment.Write(EnvironmentVariableTarget.User, read with { Value = @"C:\alpha;C:\beta" }, _name);

        Stored().ShouldBe((@"C:\alpha;C:\beta", RegistryValueKind.String));
    }

    [Fact]
    public void AValueThatDoesNotExistReadsAsAnEmptyExpandStringAndIsCreatedAsOne()
    {
        WindowsOnly.Require();

        var read = PathEnvironment.Read(EnvironmentVariableTarget.User, _name);

        read.Value.ShouldBe(string.Empty);
        read.Kind.ShouldBe(RegistryValueKind.ExpandString, "the type Windows itself gives a Path");

        PathEnvironment.Write(EnvironmentVariableTarget.User, read with { Value = @"C:\alpha" }, _name);

        Stored().ShouldBe((@"C:\alpha", RegistryValueKind.ExpandString));
    }

    [Fact]
    public void AValueThatIsNotTextIsNamedAndNotWritten()
    {
        WindowsOnly.Require();
        using (var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true))
        {
            key.ShouldNotBeNull();
            key.SetValue(_name, new[] { "a", "b" }, RegistryValueKind.MultiString);
        }

        var read = PathEnvironment.Read(EnvironmentVariableTarget.User, _name);

        read.IsText.ShouldBeFalse();
        read.Kind.ShouldBe(RegistryValueKind.MultiString);

        Should.Throw<ArgumentException>(() =>
            PathEnvironment.Write(EnvironmentVariableTarget.User, read, _name));

        Stored().Kind.ShouldBe(RegistryValueKind.MultiString, "left exactly as it was found");
    }
}
