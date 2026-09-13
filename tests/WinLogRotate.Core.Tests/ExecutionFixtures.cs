using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Globbing;
using WinLogRotate.Core.Io;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// A plan applier over a <see cref="FakeFiles"/> directory, so the executor's rules about failure
/// can be exercised where no operation can really fail.
/// </summary>
/// <remarks>
/// <para>
/// Every action does to the dictionary what <c>FileOps</c> and <c>Compressor</c> do to a disk,
/// including the two habits the executor has to be defended against: a rename replaces an existing
/// destination silently, and a source that is not there is an <see cref="IOException"/>. A test
/// names the operations that should fail and asserts on the directory that is left.
/// </para>
/// <para>
/// This is the other half of <see cref="Outcome"/>. That replays a plan as written, to judge the
/// planner; this replays it through the executor, to judge what the executor does when the plan
/// meets a disk that will not cooperate.
/// </para>
/// </remarks>
internal sealed class FakeApplier(FakeFiles files) : IPlanApplier
{
    private readonly List<(Func<PlannedOp, bool> When, string Because)> _failures = [];

    /// <summary>Every operation the executor asked for, in order, whether or not it succeeded.</summary>
    public List<PlannedOp> Attempted { get; } = [];

    /// <summary>Destinations that already held a file when an operation wrote over them.</summary>
    public List<string> Clobbered { get; } = [];

    private readonly List<(Func<PlannedOp, bool> When, Func<PlannedOp, Exception> Make)> _surprises = [];

    /// <summary>Makes every operation matching <paramref name="when"/> throw, as a locked file would.</summary>
    public FakeApplier Fail(Func<PlannedOp, bool> when, string because = "held by another process")
    {
        _failures.Add((when, because));
        return this;
    }

    /// <summary>
    /// Makes every operation matching <paramref name="when"/> throw something the executor does not
    /// expect - anything but the IO exceptions its filter names.
    /// </summary>
    public FakeApplier Surprise(Func<PlannedOp, bool> when, Func<PlannedOp, Exception> make)
    {
        _surprises.Add((when, make));
        return this;
    }

    public AppliedOp Apply(PlannedOp op, EffectiveJob job, Action<int, Exception> onRetry)
    {
        Attempted.Add(op);

        foreach (var (when, because) in _failures)
        {
            if (when(op))
            {
                throw new IOException($"{op.Action} {op.Source}: {because}");
            }
        }

        foreach (var (when, make) in _surprises)
        {
            if (when(op))
            {
                throw make(op);
            }
        }

        switch (op.Action)
        {
            case PlannedAction.Delete:
                files.Remove(op.Source);
                return new AppliedOp { BytesAfter = 0 };

            case PlannedAction.CreateDirectory:
                return new AppliedOp { BytesAfter = 0 };

            case PlannedAction.Create:
                if (!files.Exists(op.Destination ?? op.Source))
                {
                    files.Add(op.Destination ?? op.Source, bytes: 0);
                }

                return new AppliedOp { BytesAfter = 0 };

            case PlannedAction.Rename:
                Move(Source(op), op.Destination!, remove: true);
                return AppliedOp.Nothing;

            case PlannedAction.Compress:
                var source = Source(op);
                Move(source with { Length = Math.Max(1, source.Length / 4) }, op.Destination!, remove: true);
                return new AppliedOp { BytesAfter = Math.Max(1, source.Length / 4) };

            case PlannedAction.Copy:
            case PlannedAction.CopyTruncate:
                var copied = Source(op);
                Move(copied, op.Destination!, remove: false);

                if (op.Action == PlannedAction.CopyTruncate)
                {
                    files.Put(op.Source, copied with { Length = 0 });
                    return new AppliedOp
                    {
                        BytesAfter = copied.Length,
                        Truncation = new TruncationOutcome { SizeBefore = copied.Length, SizeAfter = 0 },
                    };
                }

                return new AppliedOp { BytesAfter = copied.Length };

            default:
                throw new NotSupportedException($"{op.Action} has no fake.");
        }
    }

    private MatchedFile Source(PlannedOp op) =>
        files.Find(op.Source)
        ?? throw new IOException($"{op.Action} {op.Source}: the file no longer exists");

    private void Move(MatchedFile source, string destination, bool remove)
    {
        if (files.Exists(destination))
        {
            Clobbered.Add(destination);
        }

        if (remove)
        {
            files.Remove(source.Path);
        }

        files.Put(destination, source);
    }
}

/// <summary>The live-log side of a <see cref="FakeFiles"/> directory, for a runner that must not touch a disk.</summary>
internal sealed class FakeFileSource(FakeFiles files) : IFileSource
{
    public EnumerationResult Resolve(string pattern) =>
        new() { Files = files.Glob(pattern), Refusals = [] };
}
