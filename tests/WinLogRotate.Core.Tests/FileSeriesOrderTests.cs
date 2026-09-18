using Shouldly;
using WinLogRotate.Core.Engine;
using WinLogRotate.Core.Globbing;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// The order that decides which files die is one order, whatever mix of files it is given.
/// </summary>
/// <remarks>
/// The comparator used to consult a date stamp only when both files carried one and fall through
/// to the index or the modification time otherwise, so a stamped file, an indexed file and a
/// plain one could each rank ahead of the next in a cycle. <c>List.Sort</c> then mis-ordered a
/// small set and threw "IComparer.Compare() method returns inconsistent results" on a larger one,
/// which was exit 4 - and a manage job deletes by this order.
/// </remarks>
public sealed class FileSeriesOrderTests
{
    private static MatchedFile At(string name, int month, int day) => new()
    {
        Path = @"C:\logs\" + name,
        Length = 1,
        LastWriteUtc = new DateTimeOffset(2026, month, day, 12, 0, 0, TimeSpan.Zero),
    };

    /// <summary>
    /// The cycle: B before A by stamp, A before C by modification time, C before B by it too.
    /// </summary>
    [Fact]
    public void AStampedAnIndexedAndAPlainFileHaveOneOrderWhateverTheInput()
    {
        var a = At("app.log-20260901", 12, 1);  // stamped 1 Sep, written in December
        var b = At("app.log-20260905", 10, 1);  // stamped 5 Sep, written in October
        var c = At("app.log.1", 11, 1);         // no stamp, written in November

        // Dated by stamp where there is one and by modification time where there is not:
        // November, then 5 September, then 1 September.
        string[] expected = [c.Path, b.Path, a.Path];

        foreach (var input in new[] { new[] { a, b, c }, new[] { b, c, a }, new[] { c, a, b }, new[] { c, b, a } })
        {
            FileSeries.Order(input, "-yyyyMMdd").Select(f => f.Path)
                .ShouldBe(expected, $"input order {string.Join(", ", input.Select(f => f.Path))}");
        }
    }

    [Fact]
    public void ALargeMixedSetSortsWithoutThrowingAndNewestFirst()
    {
        var files = new List<MatchedFile>();
        for (var i = 0; i < 20; i++)
        {
            var name = (i % 3) switch
            {
                0 => $"app.log-202609{(i % 9) + 1:00}",
                1 => $"app.log.{i}",
                _ => $"other{i}.log",
            };
            files.Add(At(name, 1 + (i * 7) % 12, 1 + (i * 5) % 28));
        }

        var ordered = Should.NotThrow(() => FileSeries.Order(files, "-yyyyMMdd"));

        ordered.Count.ShouldBe(20);
        var dates = ordered.Select(f => f.Stamp ?? f.LastWriteUtc).ToArray();
        dates.ShouldBe(dates.OrderByDescending(d => d).ToArray(), "newest first, by the one key every file has");
    }

    /// <summary>Within one date the index still says which generation is which.</summary>
    [Fact]
    public void TheIndexBreaksATieAndTheLiveLogComesFirst()
    {
        var live = At("app.log", 9, 1);
        var one = At("app.log.1", 9, 1);
        var two = At("app.log.2", 9, 1);

        FileSeries.Order([two, live, one], stamps: false).Select(f => f.Path)
            .ShouldBe([live.Path, one.Path, two.Path]);
    }
}
