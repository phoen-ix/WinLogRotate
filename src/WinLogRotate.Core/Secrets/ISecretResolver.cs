namespace WinLogRotate.Core.Secrets;

/// <summary>The outcome of turning a <see cref="SecretRef"/> into an actual credential.</summary>
/// <remarks>
/// <see cref="Error"/> never contains the value, and never contains enough of it to guess: it is
/// rendered into an <c>LR5001</c> diagnostic that goes to stdout, the Event Log and the journal.
/// </remarks>
public readonly record struct SecretResolution
{
    /// <summary>Nothing was configured, which is not a failure - most providers need no credential.</summary>
    public static SecretResolution Absent { get; } = new() { Ok = true };

    public required bool Ok { get; init; }

    public SecretString Value { get; init; }

    /// <summary>Why not, in one clause, safe to print.</summary>
    public string? Error { get; init; }

    public static SecretResolution Found(SecretString value) => new() { Ok = true, Value = value };

    public static SecretResolution Failed(string error) => new() { Ok = false, Error = error };
}

/// <summary>
/// Turns a credential reference into a credential, at send time.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="ISecretLookup"/>, which only answers "does this exist".
/// Existence is asked at configuration-check time by callers who may not be allowed to read the
/// store; resolution is asked once, by the one thing that is about to authenticate with it.
/// </remarks>
public interface ISecretResolver
{
    SecretResolution Resolve(SecretRef reference);
}
