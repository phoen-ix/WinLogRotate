using System.Text.Json;
using WinLogRotate.Contracts;
using WinLogRotate.Core;

namespace WinLogRotate.Gui.Cli;

/// <summary>What the window found when it asked the executable beside it who it was.</summary>
public enum CliIdentityVerdict
{
    /// <summary>Ours, and we agree about the wire format.</summary>
    Ok,

    /// <summary>Nothing ran. No executable was found to ask.</summary>
    Missing,

    /// <summary>Something ran and said nothing this can make sense of.</summary>
    Unreadable,

    /// <summary>Something ran, identified itself, and is not us.</summary>
    Foreign,

    /// <summary>Ours, and built against a different version of the envelope.</summary>
    SchemaMismatch,
}

/// <summary>
/// Asks the CLI beside this window whether it is one this window understands.
/// </summary>
/// <remarks>
/// <para>
/// Four places have said the GUI does this. <c>ProductInfo.ContractSchema</c> said it "refuses to
/// talk to a mismatched CLI"; <c>CliEnvelope</c> said the schema "is compared on GUI start";
/// <c>VersionResult</c> said "the GUI reads this on start"; and <c>CommandTree</c> removes
/// System.CommandLine's built-in version option so that <c>--version --json</c> emits an
/// envelope, justifying that real code by a reader which did not exist. This is the reader.
/// </para>
/// <para>
/// It reports and never refuses. A mismatch can only make what this window <i>displays</i> wrong -
/// every action shells out and the child validates its own arguments - so refusing to open would
/// protect nothing and would cost the operator the one surface that could tell them which two
/// builds are on the machine. And if the check is itself wrong, an unnecessary banner is a far
/// better failure than a window that will not start.
/// </para>
/// </remarks>
public sealed record CliIdentity
{
    public required CliIdentityVerdict Verdict { get; init; }

    public string? Product { get; init; }
    public string? Version { get; init; }
    public int? Schema { get; init; }

    public bool Ok => Verdict == CliIdentityVerdict.Ok;

    /// <summary>
    /// The command line that asks the question.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built here and never through <see cref="CliArgs"/>. The root command declares no
    /// <c>--config-dir</c> - <c>CommandTree.Build</c> calls
    /// <c>GlobalOptions.AddTo(root, configDir: false)</c> - so appending one turns this into a
    /// parse error: the invocation short-circuits to <c>ParseErrorReporter</c>, which writes to
    /// stderr and exits 2, and no envelope is produced at all. The GUI is launched with a
    /// directory often enough that going through the ordinary builder would report a broken CLI
    /// to exactly the people least able to explain it.
    /// </para>
    /// <para>
    /// A property rather than a static array, so that no caller can mutate the list this window
    /// launches a process with - and so a test can name it.
    /// </para>
    /// </remarks>
    public static string[] Arguments => ["--version", "--json"];

    /// <summary>
    /// Reads the answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the whole <see cref="CliResult"/> because that is the only input that can tell
    /// "nothing ran" from "something ran and said nothing useful" - the first is
    /// <see cref="CliFailure.NotFound"/> and nothing else reports it.
    /// </para>
    /// <para>
    /// The verdict follows what the child <i>said</i>, not how it exited. A well-formed envelope
    /// identifies itself whatever the exit code, and a check that believed only successful runs
    /// would go blind exactly when something is wrong.
    /// </para>
    /// <para>
    /// Product and schema are read off the envelope <b>root</b> rather than the <c>result</c>
    /// payload. Schema 1 puts both on every envelope, including the one
    /// <c>CommandContext.Guarded</c> emits when a verb throws - which carries no payload at all
    /// and still says who it is. Reading the payload would report that as unreadable, which is
    /// the one case this most needs to work.
    /// </para>
    /// </remarks>
    public static CliIdentity Inspect(CliResult result)
    {
        if (result.Failure == CliFailure.NotFound)
        {
            return new CliIdentity { Verdict = CliIdentityVerdict.Missing };
        }

        if (Envelope(result.StdOut) is not { } root)
        {
            return new CliIdentity { Verdict = CliIdentityVerdict.Unreadable };
        }

        var product = root.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

        var schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number
            && s.TryGetInt32(out var number)
                ? number
                : (int?)null;

        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

        if (product is null || schema is null)
        {
            return new CliIdentity { Verdict = CliIdentityVerdict.Unreadable };
        }

        // Ordinal-ignore-case, because the question is "is this some other program" and not
        // "is this spelled the way we spell it". CliRunner.Resolve's last fallback is the bare
        // name resolved against PATH, so being handed someone else's executable is a real case.
        if (!string.Equals(product, ProductInfo.Name, StringComparison.OrdinalIgnoreCase))
        {
            return new CliIdentity
            {
                Verdict = CliIdentityVerdict.Foreign,
                Product = product,
                Version = version,
                Schema = schema,
            };
        }

        return new CliIdentity
        {
            Verdict = schema == ProductInfo.ContractSchema
                ? CliIdentityVerdict.Ok
                : CliIdentityVerdict.SchemaMismatch,
            Product = product,
            Version = version,
            Schema = schema,
        };
    }

    /// <summary>
    /// What to put in front of the operator, or empty when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// A mismatch is worded with its direction, because "which of these two do I update" is the
    /// only actionable thing about it - and a partial upgrade, which is how this happens, leaves
    /// the operator holding both halves.
    /// </remarks>
    public string Describe() => Verdict switch
    {
        CliIdentityVerdict.Missing =>
            "winlogrotate.exe could not be found, so nothing on these pages will work. "
            + "Reinstall, or place the two executables side by side.",

        CliIdentityVerdict.Foreign =>
            $"The winlogrotate.exe beside this window reports itself as '{Product}'. "
            + "Something else of that name is being found first; check PATH and the install "
            + "location.",

        CliIdentityVerdict.Unreadable =>
            "winlogrotate.exe did not answer in a way this window understands, so what you see "
            + "here may be incomplete.",

        CliIdentityVerdict.SchemaMismatch when Schema < ProductInfo.ContractSchema =>
            $"winlogrotate.exe {Version} is older than this window expects "
            + $"(it speaks version {Schema} of the response format, this window speaks "
            + $"{ProductInfo.ContractSchema}). Update winlogrotate.exe; what you see here may be "
            + "incomplete until you do.",

        CliIdentityVerdict.SchemaMismatch =>
            $"winlogrotate.exe {Version} is newer than this window expects "
            + $"(it speaks version {Schema} of the response format, this window speaks "
            + $"{ProductInfo.ContractSchema}). Update this window; what you see here may be "
            + "incomplete until you do.",

        _ => string.Empty,
    };

    /// <summary>
    /// How loudly to say it.
    /// </summary>
    /// <remarks>
    /// <see cref="Contracts.Severity"/> rather than a colour: this project must not acquire a
    /// drawing dependency, and the product already has a vocabulary for how bad something is.
    /// The mapping to a theme colour happens once, on the drawing side.
    /// <para>
    /// A mismatch is a Warning and not an Error because every action still works - the child
    /// validates its own arguments - and only what is displayed can be wrong.
    /// </para>
    /// </remarks>
    public Severity Severity => Verdict switch
    {
        CliIdentityVerdict.Missing or CliIdentityVerdict.Foreign => Contracts.Severity.Error,
        CliIdentityVerdict.Unreadable or CliIdentityVerdict.SchemaMismatch => Contracts.Severity.Warning,
        _ => Contracts.Severity.Info,
    };

    /// <summary>
    /// The last envelope in the output, or null if there is none.
    /// </summary>
    /// <remarks>
    /// The last, and keyed on a numeric <c>schema</c>, which is the rule <c>JsonStreamTests</c>
    /// already uses. <c>--version --json</c> writes exactly one line today; the rule costs
    /// nothing and means this also reads a file written by <c>--json-stream</c>, which is how the
    /// tests feed it.
    /// </remarks>
    private static JsonElement? Envelope(string output)
    {
        JsonElement? found = null;

        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length == 0 || !text.StartsWith('{'))
            {
                continue;
            }

            try
            {
                var root = JsonDocument.Parse(text).RootElement;

                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("schema", out var schema)
                    && schema.ValueKind == JsonValueKind.Number)
                {
                    found = root.Clone();
                }
            }
            catch (JsonException)
            {
                // Not an envelope. A torn line, or a verb that printed something shaped like one.
            }
        }

        return found;
    }
}
