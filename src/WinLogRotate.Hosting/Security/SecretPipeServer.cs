using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinLogRotate.Hosting.Security;

/// <summary>Why a credential was not handed over.</summary>
public enum SecretPipeOutcome
{
    Delivered,

    /// <summary>Nothing connected before the deadline.</summary>
    TimedOut,

    /// <summary>Something connected, and it was not the process we launched.</summary>
    WrongClient,

    /// <summary>The name was already taken, or the pipe could not be created.</summary>
    Unavailable,
}

/// <summary>
/// Hands one credential to one elevated child, over a pipe only it can open.
/// </summary>
/// <remarks>
/// <para>
/// The GUI cannot pipe a password to an elevated child's standard input - <c>Verb = "runas"</c>
/// requires <c>UseShellExecute = true</c>, which forbids redirecting pipes - and the two easier
/// answers are both disqualified: a command line is readable through <c>Win32_Process</c> and is
/// recorded in 4688 audit events, and a temporary file puts plaintext on disk, which is the thing
/// the encrypted store exists to prevent.
/// </para>
/// <para>
/// <b>Three things protect the value, and all three are load-bearing.</b>
/// </para>
/// <list type="number">
/// <item><see cref="PipeOptions.FirstPipeInstance"/>. Without it a server happily joins a name
/// somebody else already owns, and looks entirely healthy while doing so. With it, creation fails
/// rather than succeeding into an impostor's pipe.</item>
/// <item>A DACL naming this user and Administrators, and nobody else. Not
/// <c>Everyone</c> - <c>RotationGate</c>'s world-readable mutex is justified by guarding "file
/// operations, not secrets", and that reasoning does not transfer. Administrators is present
/// because over-the-shoulder elevation runs the child as a different account entirely.</item>
/// <item>The connected client's process id must be the one we started, checked <b>before</b>
/// anything is written. That is what keeps "Administrators may open it" from meaning "any
/// administrator process may read it".</item>
/// </list>
/// <para>
/// It lives in Hosting rather than in the GUI because no test project references the GUI, and the
/// Windows suite - which exists for exactly this, "real DPAPI, real ACLs, real registry" - already
/// references this assembly.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class SecretPipeServer : IDisposable
{
    private readonly NamedPipeServerStream _pipe;

    private SecretPipeServer(NamedPipeServerStream pipe, string name)
    {
        _pipe = pipe;
        Name = name;
    }

    /// <summary>The name to pass to the child. Not a secret; the ACL is what protects the value.</summary>
    public string Name { get; }

    /// <summary>
    /// Creates the pipe, or returns null if the name could not be claimed.
    /// </summary>
    /// <remarks>
    /// Must be called <b>before</b> the child is launched. Creating it afterwards leaves a window
    /// in which the name exists in the child's command line and not yet in the pipe namespace,
    /// which is the one moment a squatter could win.
    /// </remarks>
    public static SecretPipeServer? Create()
    {
        // Fresh per operation. A fixed name would be a name an attacker can wait on.
        var name = $"WinLogRotate-secret-{Guid.NewGuid():N}";

        try
        {
            var security = new PipeSecurity();

            // Numeric SIDs, never names: "Administrators" is localised and the check would
            // silently pass nobody on a German machine. Same rule as Sddl.WellKnown.
            security.AddAccessRule(new PipeAccessRule(
                WindowsIdentity.GetCurrent().User!,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                AccessControlType.Allow));

            var pipe = NamedPipeServerStreamAcl.Create(
                name,
                PipeDirection.Out,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,

                // FirstPipeInstance is the whole guarantee that this name is ours.
                PipeOptions.FirstPipeInstance | PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 4096,
                security);

            return new SecretPipeServer(pipe, name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Waits for the expected process, then writes the value and closes.
    /// </summary>
    /// <param name="expectedProcessId">The child we started. Any other caller gets nothing.</param>
    /// <param name="message">Already framed by <c>SecretInput.Frame.Wrap</c>.</param>
    public SecretPipeOutcome Deliver(int expectedProcessId, byte[] message, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        try
        {
            var waiting = _pipe.WaitForConnectionAsync();

            if (!waiting.Wait(timeout))
            {
                // Abandoned, not awaited - Dispose will cancel it. Observed so that its
                // cancellation does not surface later as an unobserved task exception in a
                // process that has already moved on.
                _ = waiting.ContinueWith(
                    t => _ = t.Exception, TaskScheduler.Default);

                return SecretPipeOutcome.TimedOut;
            }

            // Before the write, never after. Checking who read the secret once they have it is
            // not a check.
            //
            // The SafeHandle is passed to the import rather than DangerousGetHandle(): the
            // marshaller ref-counts it, so it cannot be closed underneath the call. RotationGate
            // carries a GC.KeepAlive for the same class of bug, described there as "classic and
            // very quiet".
            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var client)
                || client != (uint)expectedProcessId)
            {
                return SecretPipeOutcome.WrongClient;
            }

            _pipe.Write(message, 0, message.Length);
            _pipe.Flush();

            // Waits for the child to read it, so the value is not discarded by a close racing the
            // read. Bounded, because WaitForPipeDrain itself is not: a child that connects and
            // then never reads would otherwise hold this thread for ever, and this runs on the
            // GUI's thread pool.
            var remaining = deadline - DateTime.UtcNow;

            if (remaining <= TimeSpan.Zero)
            {
                return SecretPipeOutcome.TimedOut;
            }

            try
            {
                if (!Task.Run(_pipe.WaitForPipeDrain).Wait(remaining))
                {
                    return SecretPipeOutcome.TimedOut;
                }
            }
            catch (AggregateException e) when (e.InnerException is IOException)
            {
                // The child read the value and closed before the drain returned. That is a
                // delivery, and reporting it as a failure would tell an operator nothing was
                // stored about a credential that now is - so they would store it again, and the
                // second attempt would look like the broken one.
            }

            return SecretPipeOutcome.Delivered;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or AggregateException)
        {
            return SecretPipeOutcome.Unavailable;
        }
        finally
        {
            // Closed here rather than when the caller's `using` unwinds, and that is the whole
            // point. On any path but Delivered this instance is still listening, and the caller
            // only disposes it after the child exits - so a child whose startup ran past our
            // deadline would connect to a live pipe nobody will write to, block in a read with no
            // timeout, and never exit, while the caller blocks for ever waiting for it to. A
            // circular wait between two processes. Closing breaks it: a late connect is refused,
            // and a client already connected reads zero and says so.
            //
            // Deliver is called once per server and WaitForPipeDrain has already confirmed the
            // child took the value on the path that matters, so there is nothing left to lose.
            // Dispose is idempotent, so the caller's `using` is unaffected.
            _pipe.Dispose();
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    public void Dispose() => _pipe.Dispose();
}
