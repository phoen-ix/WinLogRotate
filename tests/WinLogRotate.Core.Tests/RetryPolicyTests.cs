using System.Diagnostics;
using Shouldly;
using WinLogRotate.Core.Io;
using Xunit;

namespace WinLogRotate.Core.Tests;

/// <summary>
/// Which failures are worth trying again, and which are simply the answer.
/// </summary>
/// <remarks>
/// Pure, so all of it runs on the Linux leg: nothing here opens a file, it only classifies
/// exceptions and counts attempts.
/// </remarks>
public sealed class RetryPolicyTests
{
    private static IOException Win32(int code) =>
        new("simulated", unchecked((int)(0x80070000 | (uint)code)));

    private static int AttemptsFor(Exception thrown, int attempts = 5, int intervalMs = 1)
    {
        var seen = 0;

        try
        {
            RetryPolicy.Execute<int>(
                () => { seen++; throw thrown; },
                attempts,

                // One millisecond by default: the sleep is deliberately real, so a test that wants
                // to count attempts rather than measure them must not ask for a long one.
                intervalMs);
        }
        catch (Exception e) when (e == thrown)
        {
            // Expected - every attempt failed.
        }

        return seen;
    }

    /// <summary>
    /// A permissions failure is the answer, not a delay.
    /// </summary>
    /// <remarks>
    /// UnauthorizedAccessException was unconditionally transient, in contrast to the IOException
    /// arm beside it which is narrowed to two codes. It went unnoticed while every caller was a
    /// file operation on a directory the service had just written to; milestone 16 added a
    /// planned CreateDirectory for olddir, where an ACL denial is the expected failure. At
    /// defaults that cost five attempts and 1500 ms of real sleeping per directory.
    /// </remarks>
    [Fact]
    public void AnAccessDeniedFailureIsNotRetried() =>
        AttemptsFor(new UnauthorizedAccessException("the account cannot write there"))
            .ShouldBe(1, "permissions do not change while we wait");

    /// <summary>The failure this class exists for is still retried.</summary>
    /// <remarks>
    /// Antivirus and the Search Indexer hold a file open for a few hundred milliseconds, and a
    /// rotation that gives up on the first sharing violation fails unpredictably on healthy
    /// machines. Removing the access-denied arm must not have reached this one.
    /// </remarks>
    [Theory]
    [InlineData(Win32Error.SharingViolation)]
    [InlineData(Win32Error.LockViolation)]
    public void AContendedFileIsStillRetried(int code) =>
        AttemptsFor(Win32(code), attempts: 4).ShouldBe(4);

    /// <summary>An IO failure that is not contention is not retried either.</summary>
    [Theory]
    [InlineData(Win32Error.FileNotFound)]
    [InlineData(Win32Error.PathNotFound)]
    [InlineData(Win32Error.AccessDenied)]
    public void AnIoFailureThatIsNotContentionIsNotRetried(int code) =>
        AttemptsFor(Win32(code)).ShouldBe(1);

    /// <summary>A success on the second attempt is a success, and returns its value.</summary>
    [Fact]
    public void AContendedFileThatFreesUpSucceeds()
    {
        var seen = 0;

        var result = RetryPolicy.Execute(
            () =>
            {
                if (++seen == 1)
                {
                    throw Win32(Win32Error.SharingViolation);
                }

                return 42;
            },
            attempts: 3, intervalMs: 1);

        result.ShouldBe(42);
        seen.ShouldBe(2);
    }

    /// <summary>
    /// The backoff really doubles, measured rather than asserted as arithmetic.
    /// </summary>
    /// <remarks>
    /// README lists this as untested: "that the delay doubles and caps at five seconds is
    /// asserted as arithmetic, never as elapsed time." The sleep is deliberately real - it is
    /// waiting for another process to release a handle, which no test clock can hurry along - so
    /// this measures it.
    /// <para>
    /// Four attempts at 30 ms sleep 30 + 60 + 120 = 210 ms; a delay that did not double would
    /// total 90. The floor sits between the two and well clear of the lower one, so a loaded
    /// runner makes this slow rather than flaky, and there is deliberately no ceiling.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheBackoffDoubles()
    {
        var started = Stopwatch.StartNew();

        AttemptsFor(Win32(Win32Error.SharingViolation), attempts: 4, intervalMs: 30).ShouldBe(4);

        started.Stop();
        started.Elapsed.ShouldBeGreaterThan(
            TimeSpan.FromMilliseconds(150),
            "a fixed 30 ms delay would have totalled 90 ms; doubling totals 210");
    }

    /// <summary>Asking for no attempts at all is a programming error, not a silent no-op.</summary>
    [Fact]
    public void ZeroAttemptsIsRefused() =>
        Should.Throw<ArgumentOutOfRangeException>(
            () => RetryPolicy.Execute(() => 1, attempts: 0, intervalMs: 1));
}
