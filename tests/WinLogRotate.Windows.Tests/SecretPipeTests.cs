using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Shouldly;
using WinLogRotate.Hosting.Security;
using Xunit;

namespace WinLogRotate.Windows.Tests;

/// <summary>
/// The channel the GUI uses to hand a credential to an elevated child.
/// </summary>
/// <remarks>
/// Three things protect the value and none of them is the pipe's name, which travels on a command
/// line: the exclusive claim on that name, the descriptor, and the check on who connected. Each
/// gets a test, because each is one line that would silently degrade if it were dropped.
/// </remarks>
public sealed class SecretPipeTests
{
    private static byte[] Message(string value) =>
        [.. System.Text.Encoding.UTF8.GetBytes(value)];

    /// <summary>
    /// The name cannot be joined by anybody else.
    /// </summary>
    /// <remarks>
    /// Two things make it true - <c>FirstPipeInstance</c> and <c>maxNumberOfServerInstances: 1</c>
    /// - and this asserts the property rather than either mechanism, because it is the property
    /// that matters: a second server attaching to a live name is how a credential ends up
    /// delivered to somebody else's pipe, and it looks entirely healthy from both ends.
    /// </remarks>
    [Fact]
    public void TheNameIsClaimedExclusively()
    {
        WindowsOnly.Require();

        using var server = SecretPipeServer.Create();
        server.ShouldNotBeNull();

        Should.Throw<IOException>(() => new NamedPipeServerStream(
            server.Name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.FirstPipeInstance));
    }

    [Fact]
    public void EveryPipeGetsItsOwnName()
    {
        WindowsOnly.Require();

        // A fixed name is a name an attacker can be waiting on before the GUI even starts.
        using var a = SecretPipeServer.Create();
        using var b = SecretPipeServer.Create();

        a!.Name.ShouldNotBe(b!.Name);
    }

    /// <summary>
    /// The descriptor names this user and Administrators, and nobody else at all.
    /// </summary>
    /// <remarks>
    /// Deliberately not Everyone. RotationGate's world-readable mutex is justified in its own
    /// remarks by guarding "file operations, not secrets", and that reasoning does not carry over
    /// to a channel whose entire payload is a password.
    /// </remarks>
    [Fact]
    public void ThePipeGrantsNobodyElse()
    {
        WindowsOnly.Require();

        using var server = SecretPipeServer.Create();

        using var client = new NamedPipeClientStream(".", server!.Name, PipeDirection.In);
        client.Connect(5000);

        var rules = new NamedPipeServerStream(PipeDirection.In, false, false, client.SafePipeHandle)
            .GetAccessControl()
            .GetAccessRules(true, false, typeof(SecurityIdentifier));

        var granted = rules.Cast<PipeAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => r.IdentityReference.Value)
            .ToArray();

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        var me = WindowsIdentity.GetCurrent().User!.Value;

        // Both halves. Subset alone passes for a descriptor granting nobody at all, which would
        // be secure and useless; Administrators alone passes for one that locks out the very user
        // who created the pipe, which is the common case.
        granted.ShouldBeSubsetOf([administrators, me]);
        granted.ShouldContain(administrators);
        granted.ShouldContain(me);
    }

    /// <summary>
    /// A process that is not the one we launched gets nothing.
    /// </summary>
    /// <remarks>
    /// This is what keeps "Administrators may open it" from meaning "any administrator process may
    /// read it". The check runs before the write, because checking who read a secret after they
    /// have it is not a check.
    /// </remarks>
    [Fact]
    public async Task OnlyTheProcessWeExpectMayReadTheSecret()
    {
        WindowsOnly.Require();

        using var server = SecretPipeServer.Create();
        var connecting = Task.Run(() =>
        {
            using var client = new NamedPipeClientStream(".", server!.Name, PipeDirection.In);
            client.Connect(5000);

            var buffer = new byte[16];
            return client.Read(buffer);
        });

        // This process connects, but we claim to be waiting for a different one.
        server!.Deliver(Environment.ProcessId + 1, Message("hunter2"), TimeSpan.FromSeconds(5))
              .ShouldBe(SecretPipeOutcome.WrongClient);

        (await connecting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            .ShouldBe(0, "the client must read nothing at all");
    }

    [Fact]
    public async Task TheExpectedProcessGetsTheValue()
    {
        WindowsOnly.Require();

        using var server = SecretPipeServer.Create();
        var payload = Message("hunter2");

        var reading = Task.Run(() =>
        {
            using var client = new NamedPipeClientStream(".", server!.Name, PipeDirection.In);
            client.Connect(5000);

            var buffer = new byte[payload.Length];
            var read = 0;
            while (read < buffer.Length)
            {
                var got = client.Read(buffer.AsSpan(read));
                if (got <= 0) { break; }
                read += got;
            }

            return buffer;
        });

        server!.Deliver(Environment.ProcessId, payload, TimeSpan.FromSeconds(5))
              .ShouldBe(SecretPipeOutcome.Delivered);

        (await reading.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            .ShouldBe(payload);
    }

    /// <summary>
    /// The quit event can be created by an ordinary account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The GUI runs asInvoker and creates this event on startup so the installer's CloseGui can
    /// ask it to close. If creating it needed rights the GUI does not have, that would land in a
    /// catch and the installer would be back to overwriting a running executable - silently, and
    /// exactly as before, because the test pinning the two spellings of the name cannot tell
    /// whether anybody listens.
    /// </para>
    /// <para>
    /// <b>What this does not cover:</b> that Program.ListenForQuit is the thing doing it. No test
    /// project references the GUI, so this proves the name is usable from an ordinary account and
    /// that signalling it wakes a waiter - the two things that would have made the old no-op a
    /// silent failure - and nothing about the GUI's own wiring.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheQuitEventCanBeCreatedWithoutElevation()
    {
        WindowsOnly.Require();

        using var quit = new EventWaitHandle(
            false, EventResetMode.AutoReset, WinLogRotate.Hosting.Names.GuiQuitEvent);

        var fired = new ManualResetEventSlim();
        var registered = ThreadPool.RegisterWaitForSingleObject(
            quit, (_, _) => fired.Set(), null, Timeout.InfiniteTimeSpan, executeOnlyOnce: true);

        try
        {
            quit.Set();
            fired.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .ShouldBeTrue("signalling it must wake the waiter");
        }
        finally
        {
            registered.Unregister(null);
        }
    }

    [Fact]
    public void NothingConnectingTimesOutRatherThanHanging()
    {
        WindowsOnly.Require();

        // The GUI must not sit on a dead pipe if the child never starts - the operator would have
        // a window that looks frozen and no idea why.
        using var server = SecretPipeServer.Create();

        server!.Deliver(Environment.ProcessId, Message("x"), TimeSpan.FromMilliseconds(250))
              .ShouldBe(SecretPipeOutcome.TimedOut);
    }
}
