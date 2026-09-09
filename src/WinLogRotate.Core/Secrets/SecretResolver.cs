namespace WinLogRotate.Core.Secrets;

/// <summary>
/// The resolver the senders use: the store, the environment, or a literal.
/// </summary>
/// <remarks>
/// <para>
/// The store is opened lazily and at most once per process. A notification phase resolves one
/// credential per configured provider, and re-reading, re-parsing and re-decrypting the file for
/// each of them would be three DPAPI round trips to answer the same question.
/// </para>
/// <para>
/// <see cref="SecretSource.Command"/> is refused rather than ignored. Running a program named in a
/// configuration file to fetch a password is the same risk class as a <c>command:</c> hook - it
/// needs the configuration directory to be one no ordinary account can write, and that gate lands
/// with the hooks it was written for. Refusing loudly is the honest state in the meantime; a
/// reference that silently resolves to nothing would look like a broken relay.
/// </para>
/// </remarks>
public sealed class SecretResolver(ISecretPlatform platform, string storePath) : ISecretResolver
{
    private SecretStore? _store;
    private bool _opened;

    public SecretResolution Resolve(SecretRef reference) => reference.Source switch
    {
        SecretSource.None => SecretResolution.Absent,
        SecretSource.Literal => SecretResolution.Found(reference.Literal),
        SecretSource.Environment => FromEnvironment(reference.Key),
        SecretSource.Store => FromStore(reference.Key),

        SecretSource.Command => SecretResolution.Failed(
            "fetching a credential by running a command is not supported in this build"),

        _ => SecretResolution.Failed($"unknown credential source '{reference.Source}'"),
    };

    private static SecretResolution FromEnvironment(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return SecretResolution.Failed("@env: needs a variable name");
        }

        var value = Environment.GetEnvironmentVariable(name);

        // Empty is treated as absent, not as an empty password. A variable that exists but is
        // blank is what a deployment that forgot to substitute it looks like, and authenticating
        // with "" against a relay that accepts anonymous mail would silently succeed.
        return string.IsNullOrEmpty(value)
            ? SecretResolution.Failed($"the environment variable {name} is not set")
            : SecretResolution.Found(SecretString.From(value));
    }

    private SecretResolution FromStore(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return SecretResolution.Failed("@secret: needs a name");
        }

        var store = Open();

        if (store is null)
        {
            return SecretResolution.Failed("the secret store needs Windows");
        }

        return store.TryGet(name, out var value, out var error)
            ? SecretResolution.Found(value)
            : SecretResolution.Failed(error ?? $"the secret '{name}' could not be read");
    }

    private SecretStore? Open()
    {
        if (_opened)
        {
            return _store;
        }

        _opened = true;

        // provision: false - this is a reader. Minting a new key here would strand every secret
        // already stored under the old one, on a machine whose only symptom would be that
        // notifications stopped authenticating.
        var protector = platform.CreateProtector(provision: false);

        _store = protector is null
            ? null
            : SecretStore.Load(storePath, protector, platform.EntropyId, platform.MachineFingerprint);

        return _store;
    }
}

/// <summary>
/// <see cref="ISecretLookup"/> over the real store, so the configuration check can finally answer.
/// </summary>
/// <remarks>
/// <c>ConfigLoader.Load</c> has taken an optional lookup since milestone 7, and every production
/// caller omitted it - so <c>LR9005</c> was wired and never armed. It answers null rather than
/// false whenever the store could not be opened, because an unelevated caller cannot distinguish
/// "no such secret" from "not allowed to look", and reporting the first would be a false alarm.
/// </remarks>
public sealed class StoreSecretLookup(ISecretPlatform platform, string storePath) : ISecretLookup
{
    private SecretStore? _store;
    private bool _opened;

    public bool? Exists(string name)
    {
        if (!_opened)
        {
            _opened = true;
            var protector = platform.CreateProtector(provision: false);
            _store = protector is null
                ? null
                : SecretStore.Load(storePath, protector, platform.EntropyId, platform.MachineFingerprint);
        }

        return _store is null || _store.Status != SecretStoreStatus.Ok ? null : _store.Contains(name);
    }
}
