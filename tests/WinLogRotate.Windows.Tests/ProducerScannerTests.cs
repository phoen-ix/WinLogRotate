using Microsoft.Win32;
using Shouldly;
using WinLogRotate.Hosting.Discovery;
using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// The producer scan survives a registry it may not read.
/// </summary>
/// <remarks>
/// The SQL Server branch walked <c>HKLM\SOFTWARE\Microsoft\Microsoft SQL Server</c> unguarded,
/// while the IIS and HTTPERR branches beside it caught what their reads could throw. A key with
/// a tightened ACL - which is what a hardened database server has - made <c>scan</c> exit 4 with
/// <c>LR1006</c>, "a defect in the product", on a machine with nothing wrong. The registry is
/// handed in so the refusal can be staged without changing an ACL on the runner.
/// </remarks>
public sealed class ProducerScannerTests
{
    public static TheoryData<Exception> Refusals =>
    [
        new System.Security.SecurityException("Requested registry access is not allowed."),
        new UnauthorizedAccessException("Access to the registry key is denied."),
        new IOException("The specified registry key does not exist."),
    ];

    [Theory]
    [MemberData(nameof(Refusals))]
    public void ARegistryThisAccountMayNotReadProducesNoFindingAndNoCrash(Exception refusal)
    {
        WindowsOnly.Require();

        RegistryKey? Refuse(string path) => throw refusal;

        ProducerScanner.ScanSqlServer(Refuse).ToList().ShouldBeEmpty();
    }

    /// <summary>A key that is simply not there is not a finding either.</summary>
    [Fact]
    public void NoSqlServerIsNoFinding()
    {
        WindowsOnly.Require();

        ProducerScanner.ScanSqlServer(_ => null).ToList().ShouldBeEmpty();
    }
}
