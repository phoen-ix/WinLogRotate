namespace WinLogRotate.Contracts;

/// <summary>
/// The single shape every <c>--json</c> response takes. This is the contract the GUI binds
/// to, so it is versioned: <see cref="Schema"/> is compared on GUI start, and a mismatch
/// (a stale GUI beside a freshly-updated CLI after a partial upgrade) is reported rather
/// than allowed to produce confusing failures deeper in.
/// </summary>
/// <typeparam name="T">The verb's own result payload.</typeparam>
public sealed record CliEnvelope<T>
{
    /// <summary>Wire-format version. See <c>ProductInfo.ContractSchema</c>.</summary>
    public required int Schema { get; init; }

    /// <summary>Always "WinLogRotate". Guards against a GUI parsing some other tool's output.</summary>
    public required string Product { get; init; }

    /// <summary>Three-part version of the CLI that produced this.</summary>
    public required string Version { get; init; }

    /// <summary>The verb that ran, e.g. "run", "probe", "host status".</summary>
    public required string Verb { get; init; }

    /// <summary>True when <see cref="ExitCode"/> is 0. Redundant on purpose - it makes
    /// consuming scripts read better than comparing an integer.</summary>
    public required bool Ok { get; init; }

    /// <summary>The process exit code this run will return. See <c>ExitCode</c>.</summary>
    public required int ExitCode { get; init; }

    /// <summary>The verb's payload, absent when the verb produced no data.</summary>
    public T? Result { get; init; }

    /// <summary>Everything worth telling the caller, in order. Never empty on a failure.</summary>
    public IReadOnlyList<CliDiagnostic> Diagnostics { get; init; } = [];
}
