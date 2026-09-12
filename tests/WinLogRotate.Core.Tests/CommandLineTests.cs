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
    [Fact]
    public void AQuotedProgramWithSpacesSplitsCleanly()
    {
        CommandLine.TrySplit(
            @"""C:\Program Files\App\reload.exe"" --now --quiet",
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
            @"C:\Program Files\App\reload.exe --now",
            out _, out _, out var error, out var detail).ShouldBeFalse();

        error.ShouldBe(CommandLineError.Ambiguous);
        detail.ShouldNotBeNull().ShouldContain(@"C:\Program");
        CommandLine.Remedy(error, "").ShouldContain("Quote the program");
    }

    /// <summary>
    /// An unquoted path with spaces and arguments is refused, whatever is on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lexical half. <see cref="CommandLineWitnessTests"/> carries the behavioural half,
    /// because once the probe parameter is gone this test cannot plant anything and a refusal is
    /// all any implementation would give.
    /// </para>
    /// <para>
    /// The test this replaces could plant, and planted the wrong strings: <c>C:\Program.exe</c>,
    /// which the code never constructs because it appends nothing, and
    /// <c>C:\Program Files\App\reload.exe</c> without the trailing <c> --now</c> the second probe
    /// carried with it. Neither was ever queried, so it passed without the probe returning true
    /// once, while the probe it was named after was live underneath it - accepting
    /// <c>program = C:\Program</c> with the rest as arguments, started as SYSTEM.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnAmbiguousCommandLineIsRefusedWhateverIsOnDisk()
    {
        CommandLine.TrySplit(
            @"C:\Program Files\App\reload.exe --now",
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
            @"C:\Program Files\App\reload.exe",
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
        CommandLine.TrySplit("net stop MyService",
            out _, out _, out var error, out _).ShouldBeFalse();

        error.ShouldBe(CommandLineError.NotAbsolute);
        CommandLine.Remedy(error, "").ShouldContain("System32");
    }

    [Fact]
    public void AnAbsoluteProgramWithArgumentsNeedsNoProbe()
    {
        CommandLine.TrySplit(@"C:\Windows\System32\net.exe stop MyService",
            out var program, out var arguments, out _, out _).ShouldBeTrue();

        program.ShouldBe(@"C:\Windows\System32\net.exe");
        arguments.ShouldBe(["stop", "MyService"]);
    }

    /// <summary>Quotes group an argument, so a path with spaces stays one argument.</summary>
    [Fact]
    public void AQuotedArgumentStaysOneArgument()
    {
        CommandLine.TrySplit(
            @"C:\tools\copy.exe ""C:\Program Files\a b.txt"" D:\dest",
            out _, out var arguments, out _, out _).ShouldBeTrue();

        arguments.ShouldBe([@"C:\Program Files\a b.txt", @"D:\dest"]);
    }

    /// <summary>An empty quoted argument survives, because it was written deliberately.</summary>
    [Fact]
    public void AnEmptyQuotedArgumentIsKept()
    {
        CommandLine.TrySplit(@"C:\tools\x.exe """" --flag",
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
        CommandLine.TrySplit(@"C:\tools\reload.bat --now",
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
        CommandLine.TrySplit(@"C:\tools\reload.exe --now",
            out var absent, out _, out _, out _).ShouldBeTrue();

        CommandLine.TrySplit(@"C:\tools\reload.exe --now",
            out var present, out _, out _, out _).ShouldBeTrue();

        absent.ShouldBe(present);
    }

    [Fact]
    public void AnUnterminatedQuoteIsRefused()
    {
        CommandLine.TrySplit(@"""C:\tools\x.exe --now",
            out _, out _, out var error, out _).ShouldBeFalse();

        error.ShouldBe(CommandLineError.Unterminated);
    }

    [Fact]
    public void BlankIsRefused()
    {
        CommandLine.TrySplit("   ", out _, out _, out var error, out _).ShouldBeFalse();
        error.ShouldBe(CommandLineError.Empty);
    }

    /// <summary>A UNC program is absolute, and is accepted.</summary>
    [Fact]
    public void AUncProgramIsAbsolute()
    {
        CommandLine.TrySplit(@"\\server\share\reload.exe -q",
            out var program, out var arguments, out _, out _).ShouldBeTrue();

        program.ShouldBe(@"\\server\share\reload.exe");
        arguments.ShouldBe(["-q"]);
    }
}
