using System.Text.Json;
using System.Text.Json.Serialization;
using WinLogRotate.Core.State;

namespace WinLogRotate.Core.Secrets;

/// <summary>One stored secret, and enough about it to answer "where did this come from?".</summary>
public sealed record SecretEntry
{
    /// <summary>Base64 ciphertext. Never a value, and never anything derived from one.</summary>
    public required string Cipher { get; init; }

    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Updated { get; init; }

    /// <summary>Who ran <c>secret set</c>. A free audit trail, and the first thing anyone asks.</summary>
    public string? SetBy { get; init; }
}

/// <summary>The whole file.</summary>
public sealed record SecretDocument
{
    public int Version { get; init; } = 1;
    public DateTimeOffset? Written { get; init; }
    public string Protection { get; init; } = ProtectionScheme.DpapiLocalMachine;

    /// <summary>
    /// First eight bytes of SHA-256 over the per-install entropy, hex.
    /// </summary>
    /// <remarks>
    /// Not invertible, and not a secret. It exists so that an undecryptable file produces a
    /// sentence an operator can act on - "the machine key has changed, set them again" - rather
    /// than <c>CryptographicException: Key not valid for use in specified state</c>.
    /// </remarks>
    public string? EntropyId { get; init; }

    /// <summary>The same idea for the machine, so a copied file is diagnosed as a copied file.</summary>
    public string? Machine { get; init; }

    public Dictionary<string, SecretEntry> Secrets { get; init; } = [];
}

/// <summary>Why the store is not usable, or <see cref="Ok"/>.</summary>
public enum SecretStoreStatus
{
    Ok,

    /// <summary>No file yet. Not a problem; it is what a fresh install looks like.</summary>
    Missing,

    /// <summary>Present, but this caller may not read it. Expected for an unelevated GUI.</summary>
    Unreadable,

    /// <summary>Written on another machine. DPAPI ciphertext cannot travel.</summary>
    WrongMachine,

    /// <summary>This machine, but the per-install entropy is gone or has changed.</summary>
    KeyChanged,

    /// <summary>Corrupt, truncated, or from a version this build does not understand.</summary>
    Refused,
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(SecretDocument))]
internal sealed partial class SecretJsonContext : JsonSerializerContext;

/// <summary>
/// Credentials, encrypted, in their own file with their own permissions.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>config.toml</c> for a structural reason rather than a cryptographic one.
/// The configuration directory is readable by every local user <em>by design</em>, because the
/// unelevated read-only GUI is a shipped feature and reading a job file grants nothing. Reading
/// a stored credential grants something, so it does not live there - and the indirection
/// (<c>password = "@secret:name"</c>) is what makes pasting a config into a support ticket or
/// committing it to a deployment repo safe by construction, which are the two leaks that
/// actually happen.
/// </para>
/// <para>
/// Ciphertext is per entry rather than one envelope over the whole file. That is what lets
/// <c>secret list</c> work without decrypting anything, lets a config check ask whether a name
/// exists without handling a plaintext, keeps one dead entry from killing the rest, and means a
/// failed decrypt degrades one provider instead of all notifications.
/// </para>
/// </remarks>
public sealed class SecretStore
{
    private readonly string _path;
    private readonly IByteProtector? _protector;
    private readonly SecretDocument _document;
    private readonly string? _entropyId;
    private readonly string? _machine;

    private SecretStore(
        string path, IByteProtector? protector, SecretDocument document,
        SecretStoreStatus status, string? detail, string? entropyId, string? machine)
    {
        _path = path;
        _protector = protector;
        _document = document;
        _entropyId = entropyId;
        _machine = machine;
        Status = status;
        Detail = detail;
    }

    public SecretStoreStatus Status { get; }

    /// <summary>A sentence for an operator, when <see cref="Status"/> is not Ok.</summary>
    public string? Detail { get; }

    public string Path => _path;

    /// <summary>
    /// The names in the file, whether or not anything can be decrypted.
    /// </summary>
    /// <remarks>
    /// Deliberately available on a store copied from another machine: listing the names is
    /// exactly how an operator learns which credentials they have to enter again.
    /// </remarks>
    public IReadOnlyCollection<string> Names => _document.Secrets.Keys;

    public SecretEntry? Meta(string name) =>
        _document.Secrets.TryGetValue(name, out var e) ? e : null;

    public bool Contains(string name) => _document.Secrets.ContainsKey(name);

    /// <summary>
    /// Reads the file. Never throws for bad content.
    /// </summary>
    /// <param name="protector">Null off Windows, or when no protector could be created.</param>
    /// <param name="entropyId">Fingerprint of this machine's current entropy, if known.</param>
    /// <param name="machine">Fingerprint of this machine, if known.</param>
    public static SecretStore Load(
        string path, IByteProtector? protector, string? entropyId = null, string? machine = null)
    {
        if (!File.Exists(path))
        {
            return new SecretStore(
                path, protector, new SecretDocument(), SecretStoreStatus.Missing, null, entropyId, machine);
        }

        SecretDocument? document;
        try
        {
            var json = File.ReadAllText(path);
            document = JsonSerializer.Deserialize(json, SecretJsonContext.Default.SecretDocument);
        }
        catch (UnauthorizedAccessException)
        {
            // The normal state for an unelevated caller: the file grants SYSTEM and
            // Administrators and nobody else. Not an error - see the three-valued Exists check.
            return Refuse(path, protector, SecretStoreStatus.Unreadable,
                "The secret store is not readable from this account. Run elevated to manage secrets.",
                entropyId, machine);
        }
        catch (IOException e)
        {
            return Refuse(path, protector, SecretStoreStatus.Unreadable,
                $"The secret store could not be read ({e.Message}).", entropyId, machine);
        }
        catch (JsonException)
        {
            // Deliberately unlike StateStore, which replaces a corrupt file and starts fresh.
            // A lost rotation clock costs one interval; silently discarding a file full of
            // credentials because one byte was wrong costs a support call and a password
            // rotation, and it is unrecoverable.
            return Refuse(path, protector, SecretStoreStatus.Refused,
                "The secret store is not readable JSON. It has been left untouched.",
                entropyId, machine);
        }

        if (document is null)
        {
            return Refuse(path, protector, SecretStoreStatus.Refused,
                "The secret store is empty. It has been left untouched.", entropyId, machine);
        }

        if (document.Version > 1)
        {
            return Refuse(path, protector, SecretStoreStatus.Refused,
                $"The secret store declares version {document.Version}, but this build understands 1. "
                + "Upgrade WinLogRotate.", entropyId, machine);
        }

        if (protector is not null && document.Protection != protector.Scheme)
        {
            return Refuse(path, protector, SecretStoreStatus.Refused,
                $"The secret store was written with '{document.Protection}', but this build uses "
                + $"'{protector.Scheme}'.", entropyId, machine);
        }

        // Machine first: it is the more specific diagnosis, and a file from another machine also
        // has different entropy, so testing entropy first would give the vaguer answer.
        if (machine is not null && document.Machine is not null && document.Machine != machine)
        {
            return new SecretStore(path, protector, document, SecretStoreStatus.WrongMachine,
                "This secret store was written on a different machine. The values are bound to "
                + "that machine and cannot be copied - set them again here.", entropyId, machine);
        }

        if (entropyId is not null && document.EntropyId is not null && document.EntropyId != entropyId)
        {
            return new SecretStore(path, protector, document, SecretStoreStatus.KeyChanged,
                "The machine key has changed since these secrets were stored, so they cannot be "
                + "recovered. Set them again.", entropyId, machine);
        }

        return new SecretStore(path, protector, document, SecretStoreStatus.Ok, null, entropyId, machine);
    }

    private static SecretStore Refuse(
        string path, IByteProtector? protector, SecretStoreStatus status, string detail,
        string? entropyId, string? machine) =>
        new(path, protector, new SecretDocument(), status, detail, entropyId, machine);

    /// <summary>
    /// Decrypts one secret. Never throws, and never puts the value in an error.
    /// </summary>
    public bool TryGet(string name, out SecretString value, out string? error)
    {
        value = SecretString.None;

        if (Status is SecretStoreStatus.WrongMachine or SecretStoreStatus.KeyChanged
            or SecretStoreStatus.Unreadable or SecretStoreStatus.Refused)
        {
            error = Detail;
            return false;
        }

        if (!_document.Secrets.TryGetValue(name, out var entry))
        {
            error = $"There is no secret called '{name}'.";
            return false;
        }

        if (_protector is null)
        {
            error = "The secret store needs Windows.";
            return false;
        }

        try
        {
            var plain = _protector.Unprotect(Convert.FromBase64String(entry.Cipher));
            if (!PlaintextPadding.TryUnwrap(plain, out var text))
            {
                error = $"The stored value for '{name}' did not decrypt to anything usable.";
                return false;
            }

            value = SecretString.From(text);
            error = null;
            return true;
        }
        catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException)
        {
            // One unreadable entry degrades one provider. The others still work, which is the
            // whole reason ciphertext is per entry.
            error = $"The stored value for '{name}' could not be decrypted on this machine.";
            return false;
        }
    }

    /// <summary>Adds or replaces a secret. The store must be writable.</summary>
    public SecretStore Set(string name, SecretString value, string? setBy, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_protector is null)
        {
            throw new PlatformNotSupportedException("Storing a secret needs Windows.");
        }

        var now = clock.GetUtcNow();
        var existing = Meta(name);

        var secrets = new Dictionary<string, SecretEntry>(_document.Secrets, StringComparer.OrdinalIgnoreCase)
        {
            [name] = new SecretEntry
            {
                Cipher = Convert.ToBase64String(_protector.Protect(PlaintextPadding.Wrap(value.Reveal()))),
                Created = existing?.Created ?? now,
                Updated = now,
                SetBy = setBy,
            },
        };

        return With(secrets);
    }

    public SecretStore Remove(string name) =>
        !_document.Secrets.ContainsKey(name)
            ? this
            : With(_document.Secrets
                .Where(kv => !string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));

    private SecretStore With(Dictionary<string, SecretEntry> secrets) =>
        new(_path, _protector,
            _document with
            {
                Secrets = secrets,
                Protection = _protector?.Scheme ?? _document.Protection,
                EntropyId = _entropyId ?? _document.EntropyId,
                Machine = _machine ?? _document.Machine,
            },
            SecretStoreStatus.Ok, null, _entropyId, _machine);

    /// <summary>
    /// Writes the file atomically, applying <paramref name="harden"/> before it takes its name.
    /// </summary>
    public void Save(TimeProvider clock, Action<string>? harden = null)
    {
        var document = _document with { Written = clock.GetUtcNow() };
        AtomicJson.Write(
            _path, JsonSerializer.Serialize(document, SecretJsonContext.Default.SecretDocument), harden);
    }
}
