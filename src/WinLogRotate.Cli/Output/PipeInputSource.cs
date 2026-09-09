using System.IO.Pipes;
using WinLogRotate.Core.Secrets;

namespace WinLogRotate.Cli.Output;

/// <summary>
/// Reads a secret from a named pipe, for the elevated child the GUI launches.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of one Win32 rule with no way round it: elevation needs
/// <c>Verb = "runas"</c>, which needs <c>UseShellExecute = true</c>, which forbids redirecting the
/// child's standard input. So the GUI cannot pipe a password to the process that stores it, and
/// <c>secret set</c> needs elevation.
/// </para>
/// <para>
/// The two easier answers are both wrong and both permanently unavailable here. A
/// <c>--value</c> option puts the credential on a command line, which every local administrator
/// can read through <c>Win32_Process</c>, which is written verbatim into 4688 audit events, and
/// which essentially every EDR agent captures. A temporary file puts it on disk in plaintext,
/// which is the thing the DPAPI store exists to avoid. <b>There is deliberately no fallback to
/// either if the pipe fails</b> - a failed pipe is a failed operation, and the caller says so.
/// </para>
/// <para>
/// The pipe's name is on the command line and is not a secret. What protects the value is the
/// server's ACL, its <c>FirstPipeInstance</c> claim on the name, and its check that the process
/// connecting is the one it launched - all of which live on the other end, in
/// <c>WinLogRotate.Hosting.Security.SecretPipeServer</c>.
/// </para>
/// </remarks>
internal sealed class PipeInputSource(string pipeName, TimeSpan timeout) : IInputSource
{
    /// <summary>
    /// Long enough for a UAC prompt to be answered, short enough not to wedge a rotation.
    /// </summary>
    /// <remarks>
    /// The child is started by the GUI only after the operator has already accepted the prompt,
    /// so this covers process start rather than human hesitation.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>True: there is no console here, and nothing may prompt.</summary>
    public bool IsRedirected => true;

    public bool TryReadSecret(string prompt, out SecretString value, out string? error)
    {
        value = SecretString.None;

        var text = Read(out error);
        return text is not null && SecretInput.Validate(text, out value, out error);
    }

    /// <summary>The whole message, for <c>secret import</c>.</summary>
    public string ReadAllText() => Read(out _) ?? string.Empty;

    private string? Read(out string? error)
    {
        error = null;

        try
        {
            // Anonymous, and this matters more than it looks. The pipe SERVER here is the
            // unelevated GUI and the client is the elevated child; without an explicit
            // impersonation level Windows lets a server impersonate its client, which would hand
            // an unelevated process an administrator token. The user consented to elevating
            // winlogrotate.exe, not to granting its parent everything that token can do.
            using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.In, PipeOptions.None,
                System.Security.Principal.TokenImpersonationLevel.Anonymous);

            pipe.Connect((int)timeout.TotalMilliseconds);

            Span<byte> prefix = stackalloc byte[SecretFrame.PrefixBytes];
            if (!Fill(pipe, prefix))
            {
                error = "The value was not delivered.";
                return null;
            }

            var length = SecretFrame.PayloadLength(prefix);
            if (length < 0)
            {
                // Validated before it is used to size anything: this number came from another
                // process, and the ACL is what makes that process trustworthy, not this prefix.
                error = "The value was not delivered in a form this understands.";
                return null;
            }

            var payload = new byte[length];
            if (!Fill(pipe, payload) || !PlaintextPadding.TryUnwrap(payload, out var text))
            {
                error = "The value was not delivered.";
                return null;
            }

            return SecretInput.StripOneNewline(text);
        }
        catch (TimeoutException)
        {
            error = "Timed out waiting for the value.";
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = "The value could not be read.";
            return null;
        }
    }

    /// <summary>
    /// Reads exactly as many bytes as the buffer holds.
    /// </summary>
    /// <remarks>
    /// A pipe read returns what happens to be available, not what was asked for. Treating one
    /// short read as the whole message would truncate a long credential into a shorter one that
    /// stores perfectly well and never authenticates.
    /// </remarks>
    private static bool Fill(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = stream.Read(buffer[read..]);
            if (got <= 0)
            {
                return false;
            }

            read += got;
        }

        return true;
    }
}
