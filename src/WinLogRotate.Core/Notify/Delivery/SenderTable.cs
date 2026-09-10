namespace WinLogRotate.Core.Notify.Delivery;

/// <summary>
/// Which transport handles which scheme.
/// </summary>
/// <remarks>
/// A table rather than a switch because one entry comes from a different assembly:
/// <c>eventlog:</c> is served from Hosting, beside the P/Invoke that writes the record, and Core
/// cannot reference Hosting. It also means a scheme with no transport is a missing key rather than
/// a fallthrough, which is why <c>command:</c> produces a refusal instead of silence.
/// </remarks>
public sealed class SenderTable
{
    private readonly Dictionary<HookScheme, INotifySender> _senders;
    private readonly Dictionary<HookScheme, string> _reasons;

    /// <param name="reasons">
    /// Why a scheme this build understands is nonetheless unavailable here - <c>eventlog:</c> on a
    /// machine that is not Windows, most of all. Without it every absence reads as "not
    /// implemented", which sends an operator looking for a version that has it.
    /// </param>
    public SenderTable(
        IReadOnlyDictionary<HookScheme, INotifySender> senders,
        IReadOnlyDictionary<HookScheme, string>? reasons = null)
    {
        _senders = new Dictionary<HookScheme, INotifySender>(senders);
        _reasons = reasons is null ? [] : new Dictionary<HookScheme, string>(reasons);
    }

    public bool Handles(HookScheme scheme) => _senders.ContainsKey(scheme);

    /// <summary>Why this scheme is unavailable, in a clause, or null for the general answer.</summary>
    public string? WhyNot(HookScheme scheme) => _reasons.GetValueOrDefault(scheme);

    public INotifySender For(ResolvedChannel channel) => For(channel.Action.Scheme);

    public INotifySender For(HookScheme scheme) =>
        _senders.TryGetValue(scheme, out var sender)
            ? sender
            : new UndeliveredScheme(
                $"{HookSchemes.Name(scheme)}: is a hook, not a notification target");
}
