using Shouldly;
using WinLogRotate.Contracts;
using WinLogRotate.Core.Configuration;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Writing one value back into a configuration file without disturbing the rest of it.
/// </summary>
/// <remarks>
/// The first test here is the one the README has claimed exists since before there was an API
/// capable of passing it. <c>TomlFile</c>'s own doc comment says "Load then Save is
/// byte-identical and Load, set one value, Save differs in exactly one line. Both are asserted by
/// tests" - only the first was, because nothing could set a value.
/// </remarks>
public sealed class TomlEditorTests
{
    /// <summary>A file with everything a real one has that a naive writer destroys.</summary>
    private const string Realistic =
        "schema = 1\r\n"
        + "\r\n"
        + "# do NOT enable compress - the log shipper can't read .gz.  -- rmk, 2024-03\r\n"
        + "[defaults]\r\n"
        + "daily    = true\r\n"
        + "rotate   = 7\r\n"
        + "\r\n"
        + "[notify.email.relay]\r\n"
        + "host     = \"smtp.example\"    # the internal relay, not M365\r\n"
        + "password = \"hunter2\"\r\n"
        + "\r\n"
        + "[journal]\r\n"
        + "retain = 30\r\n";

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>Every line that differs between two renderings, as "before -> after".</summary>
    private static string[] Diff(string before, string after)
    {
        var a = Lines(before);
        var b = Lines(after);
        var changed = new List<string>();

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : "<absent>";
            var y = i < b.Length ? b[i] : "<absent>";

            if (!string.Equals(x, y, StringComparison.Ordinal))
            {
                changed.Add($"{x} -> {y}");
            }
        }

        return [.. changed];
    }

    // ---- the claim the README makes ----------------------------------------------------------

    /// <summary>
    /// Setting one value changes exactly one line, and nothing else at all.
    /// </summary>
    /// <remarks>
    /// This is the entire justification for choosing TOML over YAML or JSON, written down in the
    /// README and in TomlFile's own remarks. A sysadmin who wrote "do NOT enable compress - the
    /// log shipper can't read .gz" must get that comment back after the GUI changes a password
    /// three tables away.
    /// </remarks>
    [Fact]
    public void SettingOneValueChangesExactlyOneLine()
    {
        var file = TomlFile.Parse(Realistic, "config.toml");

        TomlEditor.TrySet(file, "notify.email.relay", "password", "@secret:relay", out _, out _)
            .ShouldBeTrue();

        Diff(Realistic, file.ToString()).ShouldBe([
            "password = \"hunter2\" -> password = \"@secret:relay\"",
        ]);
    }

    [Fact]
    public void TheCommentThatExplainsASettingSurvives()
    {
        var file = TomlFile.Parse(Realistic, "config.toml");
        TomlEditor.TrySet(file, "notify.email.relay", "password", "@secret:relay", out _, out _);

        var after = file.ToString();
        after.ShouldContain("do NOT enable compress");
        after.ShouldContain("# the internal relay, not M365");
    }

    /// <summary>
    /// A CRLF file stays a CRLF file, whichever machine did the editing.
    /// </summary>
    /// <remarks>
    /// The adding case is the one that bites: Tomlyn emits a constructed key-value with a bare
    /// <c>\n</c>, and the obvious fix - Environment.NewLine - is itself <c>\n</c> on the Linux
    /// test leg, so the bug survives the fix and only appears on a Windows operator's file. The
    /// line ending has to come from the document.
    /// </remarks>
    [Theory]
    // The replace case is a control: it writes no newline at all, so it cannot fail. It is here so
    // that a regression in the shared path shows up as both cases going red rather than one.
    [InlineData("retain", "an existing key is replaced")]
    [InlineData("notify", "a new key is added")]
    public void LineEndingsAreNotConvertedUnderneathSomebody(string key, string why)
    {
        var file = TomlFile.Parse(Realistic, "config.toml");
        TomlEditor.TrySet(file, "journal", key, "7", out _, out _).ShouldBeTrue();

        file.ToString().Replace("\r\n", "").Contains('\n').ShouldBeFalse(why);
    }

    [Fact]
    public void AnLfOnlyFileIsNotConvertedToCrlfEither()
    {
        // The same rule in the other direction, which a hard-coded "\r\n" would break.
        var lf = "[journal]\nretain = 30\n";
        var file = TomlFile.Parse(lf, "config.toml");

        TomlEditor.TrySet(file, "journal", "notify", "yes", out _, out _).ShouldBeTrue();

        file.ToString().ShouldNotContain("\r");
    }

    [Fact]
    public void WhatIsWrittenParsesBackThroughTheRealBinder()
    {
        // The same check ImportTests makes of generated TOML: it is not enough that it looks
        // right, it has to be readable by the thing that will read it.
        var file = TomlFile.Parse(Realistic, "config.toml");
        TomlEditor.TrySet(file, "notify.email.relay", "password", "@secret:relay", out _, out _);

        var reparsed = TomlFile.Parse(file.ToString(), "config.toml");
        reparsed.HasErrors.ShouldBeFalse();

        var bag = new DiagnosticBag();
        var providers = ConfigBinder.BindProviders(reparsed, bag);

        providers.ShouldHaveSingleItem().Password.Describe().ShouldBe("secret:relay");
    }

    /// <summary>
    /// The comment on the very line being edited survives.
    /// </summary>
    /// <remarks>
    /// The one an operator most cared about - they annotated that exact setting. Replacing a
    /// KeyValueSyntax's Value node takes the node's trailing trivia with it, so a naive
    /// assignment deletes the inline comment while leaving every other comment in the file
    /// untouched, which is exactly the shape of bug a "comments survive" test edits around.
    /// </remarks>
    [Fact]
    public void TheCommentOnTheEditedLineItselfSurvives()
    {
        const string Annotated =
            "[notify.email.relay]\r\n"
            + "password = \"hunter2\"  # rotated 2024-05, expires Nov  -- rmk\r\n";

        var file = TomlFile.Parse(Annotated, "config.toml");
        TomlEditor.TrySet(file, "notify.email.relay", "password", "@secret:relay", out _, out _);

        file.ToString().ShouldContain("rotated 2024-05");
        Diff(Annotated, file.ToString()).Length.ShouldBe(1);
    }

    /// <summary>
    /// The same, through the fixture that has a comment on a key other than the obvious one.
    /// </summary>
    /// <remarks>
    /// A review of this code claimed the inline comment was destroyed, on the grounds that
    /// assigning over a KeyValueSyntax's Value takes the node's trailing trivia with it. It is
    /// not - Tomlyn keeps an inline comment outside the value node - but the claim was worth a
    /// test, because the original one only ever edited the one key in its fixture that carried
    /// no comment, and would have gone green either way.
    /// </remarks>
    [Fact]
    public void EditingAnAnnotatedKeyKeepsItsAnnotation()
    {
        var file = TomlFile.Parse(Realistic, "config.toml");

        TomlEditor.TrySet(file, "notify.email.relay", "host", "smtp.internal", out _, out _)
            .ShouldBeTrue();

        var after = file.ToString();
        after.ShouldContain("# the internal relay, not M365");
        after.ShouldContain("smtp.internal");
        Diff(Realistic, after).Length.ShouldBe(1);
    }

    /// <summary>
    /// A file whose last line has no newline does not gain a concatenated one.
    /// </summary>
    /// <remarks>
    /// Editors and git both produce these. With no end-of-line token to copy, appending writes
    /// straight onto the end of the previous line - <c>retain = 30notify = "yes"</c> - which is
    /// either a parse error or, worse, a different setting.
    /// </remarks>
    [Fact]
    public void AKeyAddedToAFileWithNoTrailingNewlineStartsItsOwnLine()
    {
        var file = TomlFile.Parse("[journal]\r\nretain = 30", "config.toml");

        TomlEditor.TrySet(file, "journal", "notify", "yes", out _, out _).ShouldBeTrue();

        var after = file.ToString();
        after.ShouldNotContain("30notify");
        TomlFile.Parse(after, "config.toml").HasErrors.ShouldBeFalse();
    }

    /// <summary>
    /// A quoted key is the same key, so it is replaced rather than duplicated.
    /// </summary>
    /// <remarks>
    /// The consequence of getting this wrong is specific and bad: set-secret appends a second
    /// <c>password</c>, TOML rejects the duplicate or the binder takes the first, and the
    /// plaintext credential the whole command exists to remove is still sitting in the file.
    /// </remarks>
    [Fact]
    public void AQuotedKeyIsTheSameKey()
    {
        var file = TomlFile.Parse("[notify.email.relay]\r\n\"password\" = \"hunter2\"\r\n", "config.toml");

        TomlEditor.TrySet(file, "notify.email.relay", "password", "@secret:x", out _, out _)
            .ShouldBeTrue();

        var after = file.ToString();
        after.ShouldNotContain("hunter2");
        after.Split("password").Length.ShouldBe(2, "there must still be exactly one password key");
    }

    /// <summary>An array of tables is not a table, and must not be edited as one.</summary>
    /// <remarks>
    /// ConfigBinder already refuses <c>[[notify.*]]</c> outright. Silently writing into the first
    /// element of one would be editing something the product does not read.
    /// </remarks>
    [Fact]
    public void AnArrayOfTablesIsNotMatched()
    {
        var file = TomlFile.Parse("[[notify.email.relay]]\r\npassword = \"hunter2\"\r\n", "config.toml");

        TomlEditor.TrySet(file, "notify.email.relay", "password", "@secret:x", out var error, out _)
            .ShouldBeFalse();

        error.ShouldBe(TomlEditError.NoSuchTable);
    }

    /// <summary>Appending a key that a dotted key already defines part of is refused.</summary>
    /// <remarks>
    /// <c>b.c = 1</c> defines <c>b</c> as a table. Appending <c>b = "x"</c> after it is a
    /// redefinition, and the file stops parsing.
    /// </remarks>
    [Fact]
    public void AKeyAlreadyDefinedByADottedKeyIsRefused()
    {
        var file = TomlFile.Parse("[a]\r\nb.c = 1\r\n", "config.toml");

        TomlEditor.TrySet(file, "a", "b", "x", out var error, out _).ShouldBeFalse();

        error.ShouldBe(TomlEditError.UnusableKey);
        TomlFile.Parse(file.ToString(), "config.toml").HasErrors.ShouldBeFalse();
    }

    // ---- escaping ----------------------------------------------------------------------------

    /// <summary>
    /// A value carrying a quote or a backslash is escaped, not pasted in.
    /// </summary>
    /// <remarks>
    /// LogrotateImporter interpolates values straight into $"key = \"{value}\"" with no escaping
    /// at all. That is survivable there - an imported job is written disabled and reviewed - and
    /// is not survivable here, where the value arrives from a text box.
    /// </remarks>
    [Theory]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("C:\\logs\\a.log")]
    [InlineData("line\nbreak")]
    [InlineData("tab\there")]
    public void AValueThatWouldBreakTheFileIsEscaped(string nasty)
    {
        var file = TomlFile.Parse(Realistic, "config.toml");
        TomlEditor.TrySet(file, "journal", "retain", nasty, out _, out _).ShouldBeTrue();

        var reparsed = TomlFile.Parse(file.ToString(), "config.toml");
        reparsed.HasErrors.ShouldBeFalse("whatever was written has to still be TOML");
    }

    [Fact]
    public void AnEscapedValueRoundTripsToWhatWasAskedFor()
    {
        var file = TomlFile.Parse("[a]\r\nk = \"x\"\r\n", "config.toml");
        TomlEditor.TrySet(file, "a", "k", "he said \"no\"\\ever", out _, out _);

        var table = TomlFile.Parse(file.ToString(), "config.toml")
            .Document.Tables.Single();

        var value = table.Items.OfType<Tomlyn.Syntax.KeyValueSyntax>().Single().Value;
        ((Tomlyn.Syntax.StringValueSyntax)value!).Value.ShouldBe("he said \"no\"\\ever");
    }

    // ---- adding a key ------------------------------------------------------------------------

    /// <summary>
    /// A key added to a table that is not the last one still leaves a valid file.
    /// </summary>
    /// <remarks>
    /// Tomlyn emits a constructed key-value with no end-of-line token, so without an explicit one
    /// the result is literally `added = "v"[nexttable]` - a corrupted file, produced silently and
    /// only in the middle of a document.
    /// </remarks>
    [Fact]
    public void AKeyAddedToATableInTheMiddleDoesNotRunIntoTheNextOne()
    {
        var file = TomlFile.Parse(Realistic, "config.toml");

        TomlEditor.TrySet(file, "defaults", "compresstype", "gzip", out _, out _).ShouldBeTrue();

        var after = file.ToString();
        after.ShouldNotContain("\"gzip\"[");
        TomlFile.Parse(after, "config.toml").HasErrors.ShouldBeFalse();
    }

    [Fact]
    public void AnAddedKeyLandsInsideItsOwnTableAndKeepsTheBlankLineAfterIt()
    {
        // The blank line separating two tables is trailing trivia on the last key, so a naive
        // append puts the new key after it - the tables run together and a one-line addition
        // reads as a two-line diff.
        var file = TomlFile.Parse(Realistic, "config.toml");
        TomlEditor.TrySet(file, "defaults", "compresstype", "gzip", out _, out _);

        var lines = Lines(file.ToString());
        var added = Array.FindIndex(lines, l => l.StartsWith("compresstype", StringComparison.Ordinal));

        added.ShouldBeGreaterThan(0);
        lines[added - 1].ShouldBe("rotate   = 7");
        lines[added + 1].ShouldBe("");
        lines[added + 2].ShouldBe("[notify.email.relay]");
    }

    [Fact]
    public void AddingAKeyAddsExactlyOneLine()
    {
        var file = TomlFile.Parse(Realistic, "config.toml");
        TomlEditor.TrySet(file, "defaults", "compresstype", "gzip", out _, out _);

        Lines(file.ToString()).Length.ShouldBe(Lines(Realistic).Length + 1);
    }

    // ---- what it refuses ---------------------------------------------------------------------

    /// <summary>
    /// A table that is not there is refused, never created.
    /// </summary>
    /// <remarks>
    /// Deciding where a new table belongs in somebody's file - which blank lines, which comments
    /// travel with it - is where a comment-preserving editor turns into a formatter. The caller
    /// tells the operator what to add instead.
    /// </remarks>
    [Fact]
    public void AnAbsentTableIsRefusedRatherThanCreated()
    {
        var file = TomlFile.Parse(Realistic, "config.toml");

        TomlEditor.TrySet(file, "notify.webhook.slack", "url", "x", out var error, out var detail)
            .ShouldBeFalse();

        error.ShouldBe(TomlEditError.NoSuchTable);
        detail.ShouldNotBeNullOrWhiteSpace();
        file.ToString().ShouldBe(Realistic, "a refusal must change nothing at all");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has\"quote")]
    [InlineData("")]
    public void AKeyThatWouldNeedQuotingIsRefused(string key)
    {
        var file = TomlFile.Parse(Realistic, "config.toml");

        TomlEditor.TrySet(file, "journal", key, "v", out var error, out _).ShouldBeFalse();

        error.ShouldBe(TomlEditError.UnusableKey);
        file.ToString().ShouldBe(Realistic);
    }

    // ---- how a table header may be spelled ---------------------------------------------------

    /// <summary>
    /// The writer finds a table the same way the binder does, however its header is written.
    /// </summary>
    /// <remarks>
    /// [notify . email . relay] and ["notify".email.relay] are both legal TOML for one table, and
    /// neither survives a string comparison on the header text. Both go through
    /// ConfigBinder.KeyParts, so the reader and the writer cannot disagree about which table they
    /// are looking at.
    /// </remarks>
    [Theory]
    [InlineData("[notify.email.relay]")]
    [InlineData("[notify . email . relay]")]
    [InlineData("[\"notify\".email.relay]")]
    [InlineData("[notify.email.RELAY]")]
    public void ATableIsFoundHoweverItsHeaderIsSpelled(string header)
    {
        var file = TomlFile.Parse($"{header}\r\npassword = \"old\"\r\n", "config.toml");

        TomlEditor.TrySet(file, "notify.email.relay", "password", "new", out _, out _)
            .ShouldBeTrue();

        file.ToString().ShouldContain("\"new\"");
    }

    // ---- typed values ---------------------------------------------------------------------
    //
    // This editor could write only quoted strings, and every caller it had wanted one. The first
    // caller that did not - `job disable`, writing `enabled = false` - would have written
    // `enabled = "false"`, which ConfigBinder.GetBool rejects. That error is raised against the
    // whole configuration rather than against one file, so LoadedConfig.HasErrors is true and a
    // run exits 2 having rotated nothing on the machine. The job is not disabled either.
    //
    // WhatIsWrittenParsesBackThroughTheRealBinder sets `password`, a string key, through a
    // string-only writer: its name promises the general property and it exercises the one case
    // that cannot fail. These are the rows it was missing.

    /// <summary>An integer is written as an integer, not as a quoted number.</summary>
    [Fact]
    public void AnIntegerIsWrittenAsAnIntegerAndNotAsAQuotedNumber()
    {
        var file = TomlFile.Parse("schema = 1\n\n[job]\nname = \"iis\"\n", "iis.toml");

        TomlEditor.TrySet(file, ["job"], "rotate", TomlValue.Of(30), out _, out _).ShouldBeTrue();

        file.ToString().ShouldContain("rotate = 30");
        file.ToString().ShouldNotContain("\"30\"");
    }

    /// <summary>A bool is written bare, which is the difference between disabled and not.</summary>
    [Fact]
    public void ABoolIsWrittenBare()
    {
        var file = TomlFile.Parse("schema = 1\n\n[job]\nname = \"iis\"\n", "iis.toml");

        TomlEditor.TrySet(file, ["job"], "enabled", TomlValue.Of(false), out _, out _).ShouldBeTrue();

        file.ToString().ShouldContain("enabled = false");
        file.ToString().ShouldNotContain("\"false\"");
    }

    /// <summary>A list is written as an array of quoted strings.</summary>
    [Fact]
    public void AListIsWrittenAsAnArrayOfQuotedStrings()
    {
        var file = TomlFile.Parse("schema = 1\n\n[job]\nname = \"iis\"\n", "iis.toml");

        TomlEditor.TrySet(
            file, ["job"], "paths", TomlValue.List(["C:/logs/*.log", "D:/logs/*.log"]),
            out _, out _).ShouldBeTrue();

        file.ToString().ShouldContain("""paths = ["C:/logs/*.log", "D:/logs/*.log"]""");
    }

    /// <summary>A single-item list is still an array, and carries no trailing comma.</summary>
    [Fact]
    public void AOneItemListIsStillAnArray()
    {
        var file = TomlFile.Parse("schema = 1\n\n[job]\nname = \"iis\"\n", "iis.toml");

        TomlEditor.TrySet(file, ["job"], "paths", TomlValue.List(["C:/logs/*.log"]), out _, out _)
            .ShouldBeTrue();

        file.ToString().ShouldContain("""paths = ["C:/logs/*.log"]""");
    }

    /// <summary>
    /// What is written is read back as what was asked for, for every one of the four shapes.
    /// </summary>
    /// <remarks>
    /// Through the real binder, because the question is never "is this valid TOML" - it always
    /// was - but "does <c>ConfigBinder</c> accept the type". A quoted number is valid TOML and an
    /// outage.
    /// </remarks>
    [Fact]
    public void EveryShapeIsReadBackAsWhatWasAskedFor()
    {
        var file = TomlFile.Parse("schema = 1\n\n[job]\nname = \"iis\"\n", "iis.toml");

        TomlEditor.TrySet(file, ["job"], "paths", TomlValue.List(["C:/logs/*.log"]), out _, out _);
        TomlEditor.TrySet(file, ["job"], "rotate", TomlValue.Of(30), out _, out _);
        TomlEditor.TrySet(file, ["job"], "enabled", TomlValue.Of(false), out _, out _);
        TomlEditor.TrySet(file, ["job"], "compresstype", TomlValue.Of("gzip"), out _, out _);

        var bag = new DiagnosticBag();
        var job = ConfigBinder.BindJob(TomlFile.Parse(file.ToString(), "iis.toml"), bag);

        bag.Items.Where(d => d.Severity >= Severity.Error).ShouldBeEmpty();

        job.ShouldNotBeNull();
        job.Paths.ShouldBe(["C:/logs/*.log"]);
        job.Rotate.ShouldBe(30);
        job.Enabled.ShouldBeFalse("this is the one that was an outage");
        job.CompressType.ShouldBe(CompressType.Gzip);
    }

    /// <summary>A quote or a backslash in a list item is escaped, as it is in a plain string.</summary>
    [Fact]
    public void AListItemIsEscapedToo()
    {
        var file = TomlFile.Parse("schema = 1\n\n[job]\nname = \"iis\"\n", "iis.toml");

        TomlEditor.TrySet(
            file, ["job"], "paths", TomlValue.List([@"C:\logs\a""b\*.log"]), out _, out _);

        var bag = new DiagnosticBag();
        ConfigBinder.BindJob(TomlFile.Parse(file.ToString(), "iis.toml"), bag)
            .ShouldNotBeNull()
            .Paths.ShouldBe([@"C:\logs\a""b\*.log"], "what went in comes back out");
    }

    // ---- removal --------------------------------------------------------------------------

    /// <summary>Removing a key changes exactly one line.</summary>
    [Fact]
    public void RemovingAKeyChangesExactlyOneLine()
    {
        const string before = "schema = 1\n\n[job]\nname = \"iis\"\nrotate = 7\ncompress = true\n";
        var file = TomlFile.Parse(before, "iis.toml");

        TomlEditor.TryRemove(file, ["job"], "rotate", out _, out _).ShouldBeTrue();

        file.ToString().ShouldBe("schema = 1\n\n[job]\nname = \"iis\"\ncompress = true\n");
    }

    /// <summary>
    /// Removing the last key in a table does not join it to the next one.
    /// </summary>
    /// <remarks>
    /// The blank line between two tables is trailing trivia on the last key's end-of-line token,
    /// so removing that key takes the blank line with it - the mirror image of what appending is
    /// careful about, and a one-line edit that reads as a two-line diff.
    /// </remarks>
    [Fact]
    public void RemovingTheLastKeyDoesNotJoinTwoTables()
    {
        const string before = "[job]\nname = \"iis\"\nrotate = 7\n\n[notify]\nto = [\"eventlog:\"]\n";
        var file = TomlFile.Parse(before, "config.toml");

        TomlEditor.TryRemove(file, ["job"], "rotate", out _, out _).ShouldBeTrue();

        file.ToString().ShouldBe("[job]\nname = \"iis\"\n\n[notify]\nto = [\"eventlog:\"]\n");
    }

    /// <summary>A key that is not there is said so, rather than silently succeeding.</summary>
    [Fact]
    public void RemovingAKeyThatIsNotThereSaysSo()
    {
        var file = TomlFile.Parse("[job]\nname = \"iis\"\n", "iis.toml");

        TomlEditor.TryRemove(file, ["job"], "rotate", out var error, out var detail).ShouldBeFalse();

        error.ShouldBe(TomlEditError.NoSuchKey);
        detail.ShouldNotBeNull().ShouldContain("rotate");
    }

    /// <summary>
    /// A comment above a removed key stays, deliberately.
    /// </summary>
    /// <remarks>
    /// Deleting a line somebody wrote, because of a different line you removed, is worse than
    /// leaving a comment with nothing under it.
    /// </remarks>
    [Fact]
    public void ACommentAboveARemovedKeyStays()
    {
        const string before = "[job]\nname = \"iis\"\n# keep a week\nrotate = 7\n";
        var file = TomlFile.Parse(before, "iis.toml");

        TomlEditor.TryRemove(file, ["job"], "rotate", out _, out _).ShouldBeTrue();

        file.ToString().ShouldContain("# keep a week");
    }

    // ---- reading --------------------------------------------------------------------------

    /// <summary>
    /// A value is read back as the file writes it, not as a re-rendering of it.
    /// </summary>
    /// <remarks>
    /// An editor showing an operator what their file says has to show what it says: <c>"100M"</c>
    /// is not <c>104857600</c>, even though the binder reads them the same.
    /// </remarks>
    [Fact]
    public void AValueIsReadBackAsTheFileWritesIt()
    {
        var file = TomlFile.Parse("[job]\nname = \"iis\"\nmaxsize = \"100M\"\n", "iis.toml");

        TomlEditor.TryRead(file, ["job"], "maxsize", out var source, out var line).ShouldBeTrue();

        source.ShouldBe("\"100M\"");
        line.ShouldBe(3);
    }

    /// <summary>A key that is not written is not read.</summary>
    [Fact]
    public void AKeyThatIsNotWrittenIsNotRead() =>
        TomlEditor.TryRead(
                TomlFile.Parse("[job]\nname = \"iis\"\n", "iis.toml"),
                ["job"], "rotate", out _, out _)
            .ShouldBeFalse();

    /// <summary>
    /// A value read back is the value, not the value and whatever follows it on the line.
    /// </summary>
    /// <remarks>
    /// A node renders with its trailing trivia attached, so <c>rotate = 7   # two weeks</c> read
    /// back as <c>7   # two weeks</c>. Two things went wrong with that. An editor comparing "what
    /// it says now" against "what was asked for" never found them equal, so setting a key to the
    /// value it already had rewrote the file - identical bytes, new timestamp, new descriptor,
    /// and a change reported to whatever watches the directory. And the comment would be
    /// published as part of the value, to be fed back in as one.
    /// </remarks>
    [Fact]
    public void AValueReadBackDoesNotCarryTheCommentAfterIt()
    {
        var file = TomlFile.Parse(
            "schema = 1\n\n[job]\n"
            + "rotate = 7   # two weeks was too much\n"
            + "paths = [\"C:/logs/*.log\"]  # just the one\n"
            + "dateformat = \"%Y#%m\"\n",
            "/tmp/t.toml");

        TomlEditor.TryRead(file, ["job"], "rotate", out var rotate, out _).ShouldBeTrue();
        rotate.ShouldBe("7");

        TomlEditor.TryRead(file, ["job"], "paths", out var paths, out _).ShouldBeTrue();
        paths.ShouldBe("[\"C:/logs/*.log\"]");

        // Not cut at the first '#', because a string may contain one.
        TomlEditor.TryRead(file, ["job"], "dateformat", out var format, out _).ShouldBeTrue();
        format.ShouldBe("\"%Y#%m\"");
    }
}
