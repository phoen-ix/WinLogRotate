using System.Diagnostics;
using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Safety;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The junction defence, against real junctions.
/// </summary>
/// <remarks>
/// <para>
/// <c>FileEnumerator</c> walks by hand precisely so it can decline to enter a reparse point, and
/// the anchor was pushed onto that walk unchecked - so a job anchored at a junction was followed
/// wherever it led while the guard compared only the string the operator typed.
/// <see cref="PathGuardTests"/> proves the rule without a file system; only these prove anything
/// calls it.
/// </para>
/// <para>
/// Windows-only, because a junction is. <c>mklink /J</c> needs no privilege at all - which is the
/// entire reason the defence exists - so these run for an ordinary developer as well as in CI.
/// </para>
/// <para>
/// <b>Every junction here points inside the test's own temp tree, and the protected root is
/// synthetic.</b> The revert table runs this file against a deliberately disabled guard, and a
/// fixture that aimed at a real system directory would then be enumerated for deletion. The
/// property proved is identical either way; the blast radius is not.
/// </para>
/// </remarks>
public sealed class JunctionTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("winlogrotate-junction-");
    private readonly List<string> _links = [];

    public void Dispose()
    {
        // Links first, and non-recursively. A recursive delete over a live junction is the one
        // mistake in this suite that would not be recoverable.
        foreach (var link in _links)
        {
            try { Directory.Delete(link); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        try { _dir.Delete(recursive: true); } catch (IOException) { }
    }

    private string Path(params string[] parts) =>
        System.IO.Path.Combine([_dir.FullName, .. parts]);

    private string Dir(params string[] parts)
    {
        var path = Path(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Log(string directory, string name)
    {
        var path = System.IO.Path.Combine(directory, name);
        File.WriteAllText(path, "log line\r\n");
        return path;
    }

    /// <summary>Creates a real directory junction, or skips saying why.</summary>
    private void Junction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("mklink");
        info.ArgumentList.Add("/J");
        info.ArgumentList.Add(link);
        info.ArgumentList.Add(target);

        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEnd();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // Never a silent skip. A suite that quietly stopped creating junctions would stay green
        // while proving nothing, which is the failure this whole milestone is about.
        Assert.SkipWhen(process.ExitCode != 0, $"mklink /J failed: {error}{output}");

        _links.Add(link);
        new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint)
            .ShouldBeTrue("mklink reported success but produced no reparse point");
    }

    /// <summary>A guard whose only protected location is inside this test's own tree.</summary>
    private PathGuard Guard(params string[] protectedRoots) =>
        new(new GuardOptions { ProtectedRoots = protectedRoots, Elevated = true });

    // ---- the defect --------------------------------------------------------------------------

    /// <summary>
    /// A junctioned anchor is not followed into a protected location.
    /// </summary>
    /// <remarks>
    /// The defect this milestone exists for, and the one test that fails if the anchor stops being
    /// checked. It uses the real enumerator and a real junction end to end.
    /// </remarks>
    [Fact]
    public void AJunctionedAnchorIsNotFollowedIntoAProtectedLocation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows thing.");

        var secret = Dir("secret");
        Log(secret, "victim.log");

        var anchor = Path("anchor");
        Junction(anchor, secret);

        var found = new FileEnumerator(Guard(secret)).Resolve(System.IO.Path.Combine(anchor, "*.log"));

        found.Files.ShouldBeEmpty("the junction leads somewhere we refuse to act");

        var refusal = found.Refusals.ShouldHaveSingleItem();
        refusal.Verdict.ShouldBe(GuardVerdict.ReparsePoint);

        var message = refusal.Message.ShouldNotBeNull();
        message.ShouldContain("anchor");
        message.ShouldContain("secret", customMessage: "a refusal must name what was aimed where");
    }

    /// <summary>And that refusal is the product's one Critical, carrying LR9004.</summary>
    [Fact]
    public void AJunctionRefusalIsCriticalAndCarriesItsCode()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows thing.");

        var secret = Dir("secret");
        var anchor = Path("anchor");
        Junction(anchor, secret);

        var found = new FileEnumerator(Guard(secret)).Resolve(System.IO.Path.Combine(anchor, "*.log"));
        var diagnostic = Diagnose.Refusal(found.Refusals.ShouldHaveSingleItem(), "app");

        diagnostic.Code.ShouldBe(DiagnosticCode.ReparsePointRefused);
        diagnostic.Severity.ShouldBe(Severity.Critical);
    }

    /// <summary>
    /// An intermediate component being the junction is caught too.
    /// </summary>
    /// <remarks>
    /// The case the obvious implementation misses: <c>ResolveLinkTarget</c> answers only for the
    /// leaf, so for an ordinary <c>logs</c> inside a junctioned parent it reports "not a link"
    /// while the path resolves somewhere else entirely.
    /// </remarks>
    [Fact]
    public void AJunctionedIntermediateComponentIsCaught()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows thing.");

        var secret = Dir("secret");
        Log(Dir("secret", "logs"), "victim.log");

        var parent = Path("parent");
        Junction(parent, secret);

        var found = new FileEnumerator(Guard(secret))
            .Resolve(System.IO.Path.Combine(parent, "logs", "*.log"));

        found.Files.ShouldBeEmpty();
        found.Refusals.ShouldHaveSingleItem().Verdict.ShouldBe(GuardVerdict.ReparsePoint);
    }

    // ---- the legitimate case -----------------------------------------------------------------

    /// <summary>
    /// A junction to an ordinary directory is walked, and its files found.
    /// </summary>
    /// <remarks>
    /// Relocating a log directory onto another volume when a system drive fills up is ordinary
    /// practice. Refusing every junction would break it, which is what the dead code this replaced
    /// would have done - it refused unconditionally once elevated.
    /// </remarks>
    [Fact]
    public void AJunctionToAnOrdinaryDirectoryIsWalkedNormally()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows thing.");

        var real = Dir("real");
        Log(real, "app.log");

        var anchor = Path("anchor");
        Junction(anchor, real);

        var found = new FileEnumerator(Guard(Dir("protected")))
            .Resolve(System.IO.Path.Combine(anchor, "*.log"));

        found.Files.ShouldHaveSingleItem();
        found.Refusals.ShouldBeEmpty();
        found.ResolvedAnchor.ShouldNotBeNull().ShouldContain("real");
    }

    /// <summary>
    /// A benign junction met while recursing now yields its files.
    /// </summary>
    /// <remarks>
    /// The behaviour change an operator will notice. These logs were silently dropped before -
    /// never rotated, never mentioned, indistinguishable from an empty directory.
    /// </remarks>
    [Fact]
    public void ABenignJunctionInsideAWalkYieldsItsFiles()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows thing.");

        var root = Dir("tree");
        Log(root, "top.log");

        var elsewhere = Dir("elsewhere");
        Log(elsewhere, "moved.log");

        Junction(System.IO.Path.Combine(root, "sub"), elsewhere);

        var found = new FileEnumerator(Guard(Dir("protected")))
            .Resolve(System.IO.Path.Combine(root, "**", "*.log"));

        found.Files.Select(f => System.IO.Path.GetFileName(f.Path))
            .OrderBy(n => n)
            .ShouldBe(["moved.log", "top.log"]);
    }

    /// <summary>A refused junction inside a walk does not stop the rest of the tree.</summary>
    [Fact]
    public void ARefusedJunctionInsideAWalkDoesNotStopTheRest()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows thing.");

        var root = Dir("tree");
        Log(root, "top.log");

        var secret = Dir("secret");
        Log(secret, "victim.log");
        Junction(System.IO.Path.Combine(root, "sub"), secret);

        var found = new FileEnumerator(Guard(secret))
            .Resolve(System.IO.Path.Combine(root, "**", "*.log"));

        found.Files.ShouldHaveSingleItem().Path.ShouldEndWith("top.log");
        found.Refusals.ShouldHaveSingleItem().Verdict.ShouldBe(GuardVerdict.ReparsePoint);
    }

    // ---- the awkward shapes ------------------------------------------------------------------

    /// <summary>A cycle terminates, rather than walking until the path length runs out.</summary>
    /// <remarks>
    /// The old cycle guard keyed on the textual path, so a loop produced an endless run of
    /// distinct strings and the set it kept never fired. Keying on where a directory really is
    /// makes the second visit a repeat.
    /// </remarks>
    [Fact]
    public void ACycleTerminates()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows thing.");

        var root = Dir("tree");
        Log(root, "app.log");
        Junction(System.IO.Path.Combine(root, "loop"), root);

        var found = new FileEnumerator(Guard(Dir("protected")))
            .Resolve(System.IO.Path.Combine(root, "**", "*.log"));

        found.Files.ShouldHaveSingleItem("the same file must not be returned once per lap");
    }

    /// <summary>A dangling junction is reported, not followed and not thrown.</summary>
    [Fact]
    public void ADanglingJunctionIsReportedRatherThanFollowed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows thing.");

        var root = Dir("tree");
        Log(root, "app.log");

        var gone = Dir("gone");
        Junction(System.IO.Path.Combine(root, "sub"), gone);
        Directory.Delete(gone);

        var found = new FileEnumerator(Guard(Dir("protected")))
            .Resolve(System.IO.Path.Combine(root, "**", "*.log"));

        found.Files.ShouldHaveSingleItem();
        found.Refusals.ShouldHaveSingleItem().Verdict.ShouldBe(GuardVerdict.Unverifiable);
    }

    /// <summary>The same junction reached twice is reported once.</summary>
    [Fact]
    public void AJunctionMetTwiceIsReportedOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows thing.");

        var root = Dir("tree");
        var secret = Dir("secret");

        Junction(System.IO.Path.Combine(Dir("tree", "a"), "link"), secret);
        Junction(System.IO.Path.Combine(Dir("tree", "b"), "link"), secret);

        var enumerator = new FileEnumerator(Guard(secret));
        var found = enumerator.Resolve(System.IO.Path.Combine(root, "**", "*.log"));

        found.Refusals.Count.ShouldBe(2, "two distinct links, each named once");

        // And asking again on the same instance adds nothing new: the run remembers its verdicts.
        enumerator.Resolve(System.IO.Path.Combine(root, "**", "*.log"))
            .Refusals.Count.ShouldBe(2);
    }

    /// <summary>
    /// An 8.3 short name no longer walks past the guard.
    /// </summary>
    /// <remarks>
    /// A second hole the same change closes. The guard compares strings, and <c>PROGRA~1</c> is a
    /// different string for the same directory - so a pattern written that way passed a check it
    /// should have failed. Resolving normalises it.
    /// </remarks>
    [Fact]
    public void AnEightDotThreeNameIsResolvedBeforeTheGuardSeesIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Short names are a Windows thing.");

        var longName = Dir("protected-logs");
        Log(longName, "app.log");

        var shortName = ShortNameOf(longName);
        Assert.SkipWhen(shortName is null, "This volume has 8.3 name creation disabled.");

        var found = new FileEnumerator(Guard(longName))
            .Resolve(System.IO.Path.Combine(shortName!, "*.log"));

        found.Files.ShouldBeEmpty("the short name names the protected directory");
    }

    private static string? ShortNameOf(string path)
    {
        var parent = System.IO.Path.GetDirectoryName(path)!;
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        info.ArgumentList.Add("/c");
        info.ArgumentList.Add($"for %I in (\"{path}\") do @echo %~sI");

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        return process.ExitCode == 0
               && output.Length > 0
               && !output.Equals(path, StringComparison.OrdinalIgnoreCase)
               && output.Contains('~', StringComparison.Ordinal)
            ? output
            : null;
    }
}
