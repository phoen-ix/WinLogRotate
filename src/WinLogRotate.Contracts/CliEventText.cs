using System.Text.Json;

namespace WinLogRotate.Contracts;

/// <summary>
/// Turns an event into a sentence. The only place that does.
/// </summary>
/// <remarks>
/// <para>
/// It lived in the CLI's text sink, where seven of its nine arms were unreachable - the only
/// events that ever reached a sink were the notification phase's two, both hooks - while
/// <c>RunCommand</c> hand-printed the same facts in a different format a few lines away and the
/// GUI showed the operator <c>{"ts":"...","operation":"delete",...}</c> verbatim. Three
/// renderings of one truth, of which the one a person was most likely to see was the raw JSON.
/// </para>
/// <para>
/// In Contracts because that is where the type is, and because both sides of the wire need the
/// same answer. A rendering that lives on one side is a rendering the other side reimplements.
/// </para>
/// </remarks>
public static class CliEventText
{
    /// <summary>
    /// One line of NDJSON as a sentence, or <c>null</c> if the line is not an event.
    /// </summary>
    /// <remarks>
    /// Null rather than a placeholder because the stream is not only events: the envelope shares
    /// the file, and an envelope rendered as "unknown operation" would be a line the operator has
    /// to learn to ignore. <see cref="CliEvent"/>'s four required members are what separates
    /// them, so this asks the deserializer rather than sniffing for a property name.
    /// </remarks>
    public static string? Describe(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(line, CliEventJson.Default.CliEvent) is { } e
                ? Describe(e)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What was done, or would be, or could not be - without saying to what.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="Describe"/> because a grid wants fields and a console wants a
    /// sentence, and the alternative is a second mapping on the GUI's side that says "delete"
    /// where this one says "did delete". That is the shape of defect the previous milestone spent
    /// itself removing; one rendering with two entry points is not.
    /// <para>
    /// Three states, not two. While the only events reaching a sink were the notification
    /// phase's, "would" in the plan phase and "did" otherwise was true; it is not true of an
    /// operation that threw, which was rendered "did delete C:\logs\app.log" with the truth
    /// left to a diagnostic printed underneath it.
    /// </para>
    /// </remarks>
    public static string Action(CliEvent e)
    {
        var verb = e.Result == OpResult.Failed ? "failed to"
            : e.Phase == Phase.Plan ? "would"
            : "did";

        return e.Operation switch
        {
            // The only operation a skip is ever recorded as: PlanExecutor maps every action it
            // can carry out to a named op, and PlannedAction.Skip is the one that is left.
            Op.Plan => "skip",
            Op.Delete => $"{verb} delete",
            Op.Compress => $"{verb} compress",
            Op.Rename => $"{verb} rename",
            Op.CopyTruncate => $"{verb} copytruncate",
            Op.Copy => $"{verb} copy",
            Op.Create => $"{verb} create",
            Op.CreateDir => $"{verb} mkdir",
            Op.Hook => $"{verb} run hook",
            _ => e.Operation,
        };
    }

    /// <summary>
    /// The file the action was about, and where it went when it went somewhere.
    /// </summary>
    /// <remarks>
    /// Empty for the run and job brackets, which are about no file at all - which is why
    /// <see cref="Describe"/> trims: "run.start" is a whole sentence.
    /// </remarks>
    public static string Subject(CliEvent e) => e.Operation switch
    {
        Op.Compress or Op.Rename or Op.CopyTruncate or Op.Copy => $"{e.Src} -> {e.Dst}",
        Op.Create => e.Dst ?? string.Empty,
        _ => e.Src ?? string.Empty,
    };

    /// <summary>What happened, to what, and why - indented under the run that is reporting it.</summary>
    public static string Describe(CliEvent e)
    {
        var job = e.Job is null ? "" : $"[{e.Job}] ";
        var why = e.Reason is null ? "" : $"  ({e.Reason})";

        return $"  {job}{$"{Action(e)} {Subject(e)}".TrimEnd()}{why}";
    }
}
