using WinLogRotate.Core.Compression;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Globbing;

namespace WinLogRotate.Core.Engine;

/// <summary>
/// Plans a <see cref="JobKind.Manage"/> job: compress and retain what a producer has already
/// rotated, and never touch what it is still writing.
/// </summary>
/// <remarks>
/// <para>
/// This is the mode most Windows installations actually need, and the reason is that the
/// biggest source of Windows log pain is not rotation at all. IIS, http.sys, NSSM, WinSW,
/// Apache's rotatelogs, Serilog, NLog, log4net and Tomcat all roll their own logs perfectly
/// well - and then never delete or compress any of them. Disks fill with correctly-rotated
/// files.
/// </para>
/// <para>
/// It is also the safest code in the product, and deliberately so. It never renames, never
/// truncates, never opens the live file for writing, and needs no lock probing, no state clock
/// and no scheduling decision. The only judgement it makes is which files the producer has
/// finished with - and it errs by leaving more alone, not fewer.
/// </para>
/// </remarks>
public static class ManageJobPlanner
{
    public static JobPlan Plan(EffectiveJob job, IReadOnlyList<MatchedFile> matched, DateTimeOffset now)
    {
        var operations = new List<PlannedOp>();
        var ordered = FileSeries.Order(matched, job.DateExt ? job.DateFormat : null);

        // The newest N are the producer's business, not ours. Compressing the file IIS is
        // currently appending to is precisely what this job kind exists to avoid, so the
        // reason is recorded explicitly rather than the files being silently omitted - a dry
        // run has to be able to answer "why was that one left alone?".
        var live = ordered.Take(job.LiveFiles).ToArray();
        foreach (var file in live)
        {
            operations.Add(new PlannedOp
            {
                Action = PlannedAction.Skip,
                Source = file.Path,
                Reason = job.LiveFiles == 1
                    ? "newest file - the application is still writing it"
                    : $"one of the {job.LiveFiles} newest files, which the application may still be writing",
                Bytes = file.Length,
            });
        }

        var finished = ordered.Skip(job.LiveFiles).ToArray();

        // Retention first, so a file about to be deleted is never compressed on the way out.
        // Doing it the other way round burns CPU on a multi-gigabyte log and then throws the
        // result away, which is a surprisingly easy mistake to make and a slow one to notice.
        var condemned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (job.Rotate >= 0 && finished.Length > job.Rotate)
        {
            foreach (var file in finished.Skip(job.Rotate))
            {
                condemned.Add(file.Path);
                operations.Add(new PlannedOp
                {
                    Action = PlannedAction.Delete,
                    Source = file.Path,
                    Reason = $"rotate = {job.Rotate} keeps {job.Rotate} archive(s); this is number {Array.IndexOf(finished, file) + 1}",
                    Bytes = file.Length,
                });
            }
        }

        if (job.MaxAge is { } maxAge)
        {
            var cutoff = now.AddDays(-maxAge);
            foreach (var file in finished.Where(f => !condemned.Contains(f.Path)))
            {
                // Prefer the date embedded in the name over the file's mtime: a copy or a
                // restore rewrites mtime, and a log named for the 3rd of June is from the 3rd
                // of June whatever the filesystem now believes.
                var age = file.Stamp ?? file.LastWriteUtc;
                if (age < cutoff)
                {
                    condemned.Add(file.Path);
                    operations.Add(new PlannedOp
                    {
                        Action = PlannedAction.Delete,
                        Source = file.Path,
                        Reason = $"maxage = {maxAge} days; this one is from {age:yyyy-MM-dd}",
                        Bytes = file.Length,
                    });
                }
            }
        }

        if (job.CompressType != CompressType.None)
        {
            foreach (var file in finished)
            {
                if (condemned.Contains(file.Path))
                {
                    continue;
                }

                if (file.IsCompressed)
                {
                    continue;
                }

                operations.Add(new PlannedOp
                {
                    Action = PlannedAction.Compress,
                    Source = file.Path,
                    Destination = file.Path + Compressor.Extension(job.CompressType),
                    Reason = $"compress = {job.CompressType.ToString().ToLowerInvariant()}",
                    Bytes = file.Length,
                });
            }
        }

        return new JobPlan
        {
            JobName = job.Name,
            Operations = operations,
            MatchedFiles = matched.Count,
        };
    }
}
