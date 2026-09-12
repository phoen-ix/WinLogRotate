using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace WinLogRotate.Core.Configuration;

/// <summary>
/// A TOML file kept as a syntax tree, so editing one value does not rewrite the rest.
/// </summary>
/// <remarks>
/// <para>
/// This is the single reason the config format is TOML rather than YAML or JSON. A sysadmin
/// who wrote
/// </para>
/// <code>
/// # do NOT enable compress - the log shipper can't read .gz.  -- rmk, 2024-03
/// </code>
/// <para>
/// and then changes <c>rotate</c> in the GUI must get that comment back. Deserialize-to-object
/// and re-serialize destroys it, and six months later somebody turns compression on and breaks
/// log ingestion. Tomlyn's <see cref="DocumentSyntax"/> preserves every comment, blank line and
/// indent, so <c>Load then Save</c> is byte-identical and <c>Load, set one value, Save</c>
/// differs in exactly one line. Both are asserted by tests.
/// </para>
/// </remarks>
public sealed class TomlFile
{
    private TomlFile(DocumentSyntax document, string path)
    {
        Document = document;
        Path = path;
    }

    public DocumentSyntax Document { get; }

    public string Path { get; }

    public bool HasErrors => Document.HasErrors;

    public IEnumerable<DiagnosticMessage> Errors => Document.Diagnostics;

    public static TomlFile Parse(string text, string path) =>
        new(SyntaxParser.Parse(text, path, true), path);

    public static TomlFile Load(string path) =>
        Parse(File.ReadAllText(path), path);

    /// <summary>The document's exact text, including everything the parser preserved.</summary>
    public override string ToString() => Document.ToString();

    /// <summary>
    /// Writes the file atomically: a temporary sibling, flushed, then moved over the target.
    /// A crash therefore leaves either the old file or the new one, never a half-written
    /// config that the next run refuses to start from.
    /// </summary>
    public void Save(string? path = null)
    {
        var target = path ?? Path;
        var temp = target + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
        {
            writer.Write(ToString());
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// Moves an unparseable file aside so the run can continue without it.
    /// </summary>
    /// <remarks>
    /// One malformed job file must not take the whole configuration down - a typo in an
    /// experimental job should not stop forty healthy ones from rotating, which on a busy
    /// server means a full disk. The bad file is preserved rather than deleted, because the
    /// operator will want to see what they typed.
    /// <para>
    /// Moving it aside is not what delivers that, and for a long time nothing did: the run had
    /// already returned <c>ExitCode.ConfigInvalid</c> before this was reached.
    /// <see cref="ConfigDiagnostic.FileScoped"/> is the part that keeps the promise, and this is
    /// the tidying that follows it.
    /// </para>
    /// </remarks>
    public static string Quarantine(string path)
    {
        var bad = path + ".bad";
        File.Move(path, bad, overwrite: true);
        return bad;
    }
}
