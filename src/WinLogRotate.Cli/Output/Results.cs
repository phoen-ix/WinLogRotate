namespace WinLogRotate.Cli.Output;

/// <summary>Payload of a verb that returns no data. Registering a concrete type rather
/// than using <c>object</c> keeps the JSON source generator able to see everything.</summary>
public sealed record EmptyResult;

/// <summary>Payload of <c>winlogrotate --version --json</c>. The GUI reads this on start to
/// check it is talking to a CLI it understands.</summary>
public sealed record VersionResult
{
    public required string Product { get; init; }
    public required string Version { get; init; }
    public required int Schema { get; init; }
    public required string Runtime { get; init; }
    public required string Architecture { get; init; }

    /// <summary>True when this process holds an elevated token. The GUI uses it to decide
    /// whether a button needs a shield.</summary>
    public required bool Elevated { get; init; }

    /// <summary>True for the NativeAOT build. A framework-dependent CLI would mean someone
    /// built it themselves, which is worth knowing in a bug report.</summary>
    public required bool NativeAot { get; init; }
}
