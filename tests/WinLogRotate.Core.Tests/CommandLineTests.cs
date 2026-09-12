using Shouldly;
using WinLogRotate.Core.Hooks;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The rule that decides which program a hook runs.
/// </summary>
/// <remarks>
/// There is no shell, so something has to decide where the program ends and its arguments begin.
/// This runs as SYSTEM from a scheduled task, which is why the answer is "refuse" rather than
/// "guess" and why these tests exist at all.
/// </remarks>
public sealed class CommandLineTests
{
    /// <summary>Nothing exists, so any split that depends on probing has to refuse.</summary>
    private static bool Nothing(string _) => false;

    private static Func<string, bool> Only(params string[] paths) =>
        p => paths.Contains(p, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AQuotedProgramWithSpacesSplitsCleanly()
    {
        CommandLine.TrySplit(
            @"""C:\Program Files\App\reload.exe"" --now --quiet", Nothing,
            out var program, out var arguments, out _, out _).ShouldBeTrue();

        program.ShouldBe(@"C:\Program Files\App\reload.exe");
        arguments.ShouldBe(["--now", "--quiet"]);
    }

    /// <summary>
    /// An unquoted path with spaces is refused, not probed.
    /// </summary>
    /// <remarks>
    /// The named defect. Windows' own <c>CreateProcess</c>, handed this, tries
    /// <c>C:\Program.exe</c> first - so anyone who can write the root of the system drive gets
    /// SYSTEM on the next rotation. An operator who quotes the program gets a working hook; one
    /// who does not gets a refusal naming the fix. Neither gets a surprise.
    /// </remarks>
    [Fact]
    public void AnAmbiguousCommandLineIsRefusedNotGuessed()
    {
        CommandLine.TrySplit(
            @"C:\Program Files\App\reload.exe --now", Nothing,
            out _, out _, out var error, out var detail).ShouldBeFalse();

        error.ShouldBe(CommandLineError.Ambiguous);
        detail.ShouldNotBeNull().ShouldContain(@"C:\Program");
        CommandLine.Remedy(error, "").ShouldContain("Quote the program");
    }

    /// <summary>
    /// Nothing on disk can decide where the program ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>C:\Program</c> is the string the implementation actually looks up, and it is the one the
    /// test this replaces did not plant. That test planted <c>C:\Program.exe</c> - which the code
    /// never constructs, because it appends nothing - and <c>C:\Program Files\App\reload.exe</c>
    /// without the trailing <c> --now</c> the second probe carries with it. Neither string was ever queried, so it
    /// passed without the probe returning true once, while the probe it was named after was live
    /// underneath it.
    /// </para>
    /// <para>
    /// What the probe did, when asked the question this test asks: accepted
    /// <c>program = C:\Program</c> with the rest as arguments, and started it as SYSTEM. That is
    /// CreateProcess' first leg, which is the behaviour <c>CommandLine</c>'s own remarks and
    /// <c>docs/hooks.md</c> both say it exists to reject.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoFileOnDiskCanDecideWhereTheProgramEnds()
    {
        var planted = Only(@"C:\Program");

        CommandLine.TrySplit(
            @"C:\Program Files\App\reload.exe --now", planted,
            out var program, out var arguments, out var error, out _).ShouldBeFalse();

        program.ShouldBeEmpty();
        arguments.ShouldBeEmpty();
        error.ShouldBe(CommandLineError.Ambiguous);
    }

    /// <summary>An unquoted path with spaces and no arguments is a path, and is accepted.</summary>
    /// <remarks>
    /// The whole string names a file, so nothing is being guessed - there is exactly one reading.
    /// Refusing it too would be pedantry rather than safety.
    /// </remarks>
    [Fact]
    public void AnUnquotedPathWithSpacesAndNoArgumentsIsAccepted()
    {
        CommandLine.TrySplit(
            @"C:\Program Files\App\reload.exe", Only(@"C:\Program Files\App\reload.exe"),
            out var program, out var arguments, out _, out _).ShouldBeTrue();

        program.ShouldBe(@"C:\Program Files\App\reload.exe");
        arguments.ShouldBeEmpty();
    }

    /// <summary>
    /// A bare program name is refused, because PATH would decide what runs.
    /// </summary>
    /// <remarks>
    /// <c>TaskRunHost</c> already carries the reason in its own words: resolving a bare name
    /// through PATH would let a planted executable in a writable directory run as SYSTEM.
    /// </remarks>
    [Fact]
    public void ABareProgramNameIsRefused()
    {
        // Nothing, not Only("net"). The planted file used to be what carried this to the
        // NotAbsolute arm at all: without it the bare name fell through to Ambiguous, whose
        // remedy - quote the program - produces `"net" stop MyService` and the same refusal.
        CommandLine.TrySplit("net stop MyService", Nothing,
            out _, out _, out var error, out _).ShouldBeFalse();

        error.ShouldBe(CommandLineError.NotAbsolute);
        CommandLine.Remedy(error, "").ShouldContain("System32");
    }

    [Fact]
    public void AnAbsoluteProgramWithArgumentsNeedsNoProbe()
    {
        CommandLine.TrySplit(@"C:\Windows\System32\net.exe stop MyService", Nothing,
            out var program, out var arguments, out _, out _).ShouldBeTrue();

        program.ShouldBe(@"C:\Windows\System32\net.exe");
        arguments.ShouldBe(["stop", "MyService"]);
    }

    /// <summary>Quotes group an argument, so a path with spaces stays one argument.</summary>
    [Fact]
    public void AQuotedArgumentStaysOneArgument()
    {
        CommandLine.TrySplit(
            @"C:\tools\copy.exe ""C:\Program Files\a b.txt"" D:\dest", Nothing,
            out _, out var arguments, out _, out _).ShouldBeTrue();

        arguments.ShouldBe([@"C:\Program Files\a b.txt", @"D:\dest"]);
    }

    /// <summary>An empty quoted argument survives, because it was written deliberately.</summary>
    [Fact]
    public void AnEmptyQuotedArgumentIsKept()
    {
        CommandLine.TrySplit(@"C:\tools\x.exe """" --flag", Nothing,
            out _, out var arguments, out _, out _).ShouldBeTrue();

        arguments.ShouldBe(["", "--flag"]);
    }

    /// <summary>
    /// A batch file is refused at plan time rather than failing at 03:00.
    /// </summary>
    /// <remarks>
    /// <c>CreateProcess</c> cannot start one; only <c>cmd.exe</c> can. Accepting it here would
    /// trade a message naming the fix for ERROR_BAD_EXE_FORMAT out of Process.Start on the night
    /// it mattered.
    /// </remarks>
    [Fact]
    public void ABatchFileIsRefusedWithTheInterpreterNamed()
    {
        CommandLine.TrySplit(@"C:\tools\reload.bat --now", Nothing,
            out _, out _, out var error, out var detail).ShouldBeFalse();

        error.ShouldBe(CommandLineError.NotExecutable);
        detail.ShouldNotBeNull().ShouldContain("batch file");
        CommandLine.Remedy(error, @"C:\tools\reload.bat --now").ShouldContain("cmd.exe /c");
    }

    /// <summary>
    /// The extension rule asks the disk nothing, so a hook behaves the same everywhere.
    /// </summary>
    /// <remarks>
    /// A split that depended on what happened to be installed would accept a configuration on the
    /// machine it was written on and refuse the identical file on the next one - and the failure
    /// would arrive as a rotation that stopped, in the middle of the night, on a server nobody had
    /// touched.
    /// </remarks>
    [Fact]
    public void TheSplitDoesNotDependOnWhatIsOnDisk()
    {
        CommandLine.TrySplit(@"C:\tools\reload.exe --now", Nothing,
            out var absent, out _, out _, out _).ShouldBeTrue();

        CommandLine.TrySplit(@"C:\tools\reload.exe --now", Only(@"C:\tools\reload.exe"),
            out var present, out _, out _, out _).ShouldBeTrue();

        absent.ShouldBe(present);
    }

    [Fact]
    public void AnUnterminatedQuoteIsRefused()
    {
        CommandLine.TrySplit(@"""C:\tools\x.exe --now", Nothing,
            out _, out _, out var error, out _).ShouldBeFalse();

        error.ShouldBe(CommandLineError.Unterminated);
    }

    [Fact]
    public void BlankIsRefused()
    {
        CommandLine.TrySplit("   ", Nothing, out _, out _, out var error, out _).ShouldBeFalse();
        error.ShouldBe(CommandLineError.Empty);
    }

    /// <summary>A UNC program is absolute, and is accepted.</summary>
    [Fact]
    public void AUncProgramIsAbsolute()
    {
        CommandLine.TrySplit(@"\\server\share\reload.exe -q", Nothing,
            out var program, out var arguments, out _, out _).ShouldBeTrue();

        program.ShouldBe(@"\\server\share\reload.exe");
        arguments.ShouldBe(["-q"]);
    }
}
