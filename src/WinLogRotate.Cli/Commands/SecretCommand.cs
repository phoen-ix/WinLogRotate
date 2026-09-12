using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Configuration;
using WinLogRotate.Core.Journaling;
using WinLogRotate.Core.Notify;
using WinLogRotate.Core.Secrets;

namespace WinLogRotate.Cli.Commands;

/// <summary>
/// Managing stored credentials.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no <c>--value</c> on any verb here. A command line is readable by every
/// local administrator through <c>Win32_Process</c>, is recorded verbatim in 4688
/// process-creation audit events wherever command-line auditing is enabled, and is captured by
/// essentially every EDR agent on the market. The value arrives on standard input, which none of
/// those observe.
/// </para>
/// <para>
/// Every verb takes its platform through <see cref="ISecretPlatform"/> rather than reaching for
/// DPAPI and the registry directly, so all of this - the argument handling, the refusals, the
/// diagnostics - is exercised on the Linux leg against the same code that ships.
/// </para>
/// </remarks>
internal static class SecretCommand
{
    public static int Set(
        CommandContext ctx, IInputSource input, ISecretPlatform platform,
        string name, FileInfo? fromFile, string? configDir)
    {
        if (Refuse(ctx, platform, "secret set", needsElevation: true) is { } refusal)
        {
            return refusal;
        }

        if (InvalidName(ctx, "secret set", name) is { } bad)
        {
            return bad;
        }

        SecretString value;
        if (fromFile is not null)
        {
            if (!ReadFile(ctx, "secret set", fromFile, out var text))
            {
                return ctx.Output.Complete<SecretResult>("secret set", ExitCode.Errors, null);
            }

            value = SecretString.From(text);
        }
        else if (!input.TryReadSecret($"Value for '{name}'", out value, out var error))
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.ConfigInvalid,
                Message = error ?? "No value was given.",
                // Not "'value' | winlogrotate secret set": that puts the credential on the
                // shell's own command line, which Win32_Process exposes and 4688 records - the
                // exact reason there is no --value. Run it and type at the prompt, or use a file
                // you then delete.
                Remedy = $"Run 'winlogrotate secret set {name}' and type the value when asked, "
                       + "or pass --from-file.",
            });
            return ctx.Output.Complete<SecretResult>("secret set", ExitCode.Errors, null);
        }

        var paths = InstallPaths.Resolve(configDir);
        var protector = platform.CreateProtector(provision: true);
        if (protector is null)
        {
            return NoProtector(ctx, "secret set");
        }

        var store = SecretStore.Load(
            paths.SecretsFile, protector, platform.EntropyId, platform.MachineFingerprint);

        if (Unusable(ctx, "secret set", store) is { } unusable)
        {
            return unusable;
        }

        if (!Stored(ctx, store.Set(name, value, WhoAmI(), TimeProvider.System), paths, platform))
        {
            return ctx.Output.Complete<SecretResult>("secret set", ExitCode.Errors, null);
        }

        Journal(paths, name, "set");

        var rehardened = ReportEntropy(ctx, platform);
        ctx.Output.Line($"Stored '{name}' in {paths.SecretsFile}.");
        ctx.Output.Line($"Reference it from a job or provider as: @secret:{name}");

        return ctx.Output.Complete("secret set", ExitCode.Ok, new SecretResult
        {
            Verb = "set",
            Path = paths.SecretsFile,
            Names = [name],
            EntropyRehardened = rehardened,
        });
    }

    /// <summary>
    /// Stores a provider's credential and rewrites the configuration to reference it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <c>LR9006</c> remedy as one command. That warning currently tells an operator
    /// to run <c>secret set NAME</c> and <i>then</i> edit their config to say
    /// <c>@secret:NAME</c>; the second step is the one people forget, and forgetting it leaves
    /// the password sitting in the file exactly as before while the warning stops looking urgent.
    /// The name is derived by <see cref="ConfigLoader.SuggestSecretName"/> - the same helper that
    /// writes that remedy - so the advice and the automation cannot disagree.
    /// </para>
    /// <para>
    /// <b>The value is stored before the configuration is rewritten, and the order matters.</b>
    /// A failed rewrite leaves a stored secret nothing references, which is inert. The other way
    /// round leaves a configuration referencing a secret that does not exist, which is
    /// <c>LR9005</c> and a provider that has silently stopped authenticating.
    /// </para>
    /// </remarks>
    public static int SetForProvider(
        CommandContext ctx, IInputSource input, ISecretPlatform platform,
        string provider, string field, string? configDir)
    {
        const string Verb = "notify set-secret";

        if (Refuse(ctx, platform, Verb, needsElevation: true) is { } refusal)
        {
            return refusal;
        }

        var paths = InstallPaths.Resolve(configDir);

        if (!File.Exists(paths.ConfigFile))
        {
            return Fail(ctx, Verb, DiagnosticCode.ConfigUnreadable,
                $"There is no configuration at {paths.ConfigFile}.", paths.ConfigFile);
        }

        TomlFile file;

        try
        {
            file = TomlFile.Load(paths.ConfigFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A configuration open in an editor, or on a share that went away. A stack trace
            // helps nobody, and nothing has been stored yet.
            return Fail(ctx, Verb, DiagnosticCode.ConfigUnreadable,
                $"{paths.ConfigFile} could not be read: {e.Message}", paths.ConfigFile);
        }

        if (file.HasErrors)
        {
            // Never write to a file we could not read. A rewrite driven by a partial parse is how
            // a typo becomes data loss.
            return Fail(ctx, Verb, DiagnosticCode.ConfigInvalid,
                $"{paths.ConfigFile} does not parse, so it will not be edited.", paths.ConfigFile,
                "Run 'winlogrotate config check' and fix it first.");
        }

        var bag = new DiagnosticBag();
        var providers = ConfigBinder.BindProviders(file, bag);

        // Surfaced rather than discarded. Binding a provider table can produce warnings - a
        // credential in the clear, an array-of-tables spelling - and reporting success on a
        // configuration we have just complained about is how those go unread.
        foreach (var d in bag.Items)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = d.Severity,
                Code = d.Code,
                Message = d.Message,
                Path = d.File,
                Line = d.Line == 0 ? null : d.Line,
                Column = d.Column == 0 ? null : d.Column,
                Remedy = d.Remedy,
            });
        }

        var target = providers.FirstOrDefault(
            p => string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            return Fail(ctx, Verb, DiagnosticCode.NotifyMisconfigured,
                $"There is no [notify.{provider}] provider.", paths.ConfigFile,
                providers.Count == 0
                    ? "Define one first, e.g. [notify.email.relay]."
                    : $"Defined providers: {string.Join(", ", providers.Select(p => p.Name))}.");
        }

        if (FieldFor(target.Kind, field) is not { } key)
        {
            return Fail(ctx, Verb, DiagnosticCode.NotifyMisconfigured,
                $"A {target.Kind.ToString().ToLowerInvariant()} provider has no '{field}'.",
                paths.ConfigFile,
                $"Try: {string.Join(", ", FieldsFor(target.Kind))}.");
        }

        var name = ConfigLoader.SuggestSecretName(target.Name, key);

        if (InvalidName(ctx, Verb, name) is { } bad)
        {
            return bad;
        }

        if (!input.TryReadSecret($"Value for '{name}'", out var value, out var error))
        {
            return Fail(ctx, Verb, DiagnosticCode.ConfigInvalid,
                error ?? "No value was given.", null,
                $"Run 'winlogrotate notify set-secret {provider} {field}' and type the value "
                + "when asked. Do not put it on a command line.");
        }

        var protector = platform.CreateProtector(provision: true);
        if (protector is null)
        {
            return NoProtector(ctx, Verb);
        }

        var store = SecretStore.Load(
            paths.SecretsFile, protector, platform.EntropyId, platform.MachineFingerprint);

        if (Unusable(ctx, Verb, store) is { } unusable)
        {
            return unusable;
        }

        if (!Stored(ctx, store.Set(name, value, WhoAmI(), TimeProvider.System), paths, platform))
        {
            return ctx.Output.Complete<SecretResult>(Verb, ExitCode.Errors, null);
        }

        Journal(paths, name, "set");

        // Only now the config. The temporary file TomlFile.Save writes is a sibling of
        // config.toml, so it inherits the configuration directory's descriptor exactly as the
        // original did - there is nothing extra to harden here.
        // Split once: NotifyProvider.Name is "kind.name", and the name half may itself contain a
        // dot if it was quoted in the header. Concatenating into a dotted string and splitting it
        // again turns [notify.email."my.relay"] into a four-part path that matches no table - so
        // the secret would be stored and the configuration silently never updated.
        var kind = target.Name.Split('.', 2);

        if (!TomlEditor.TrySet(file, ["notify", .. kind], key, $"@secret:{name}", out _, out var detail))
        {
            return Stored(ctx, Verb, paths, name, DiagnosticCode.NotifyMisconfigured,
                detail ?? "The configuration could not be updated.", key);
        }

        try
        {
            file.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Stored(ctx, Verb, paths, name, DiagnosticCode.ConfigUnwritable,
                $"{paths.ConfigFile} could not be written: {e.Message}", key);
        }

        var rehardened = ReportEntropy(ctx, platform);
        ctx.Output.Line($"Stored '{name}' and set {key} = \"@secret:{name}\" on [notify.{target.Name}].");

        return ctx.Output.Complete(Verb, ExitCode.Ok, new SecretResult
        {
            Verb = "set-secret",
            Path = paths.SecretsFile,
            Names = [name],
            EntropyRehardened = rehardened,
        });
    }

    /// <summary>The TOML key a field names, or null if this kind has no such credential.</summary>
    /// <remarks>
    /// Checked against the kind rather than accepted generally: a <c>token</c> on an email
    /// provider is a typo, and binding it would produce a key the binder ignores and a secret
    /// nothing reads.
    /// </remarks>
    private static string? FieldFor(NotifyProviderKind kind, string field)
    {
        var normalised = field.Trim().ToLowerInvariant().Replace('-', '_');
        return FieldsFor(kind).Contains(normalised, StringComparer.Ordinal) ? normalised : null;
    }

    private static string[] FieldsFor(NotifyProviderKind kind) => kind switch
    {
        NotifyProviderKind.Email => ["password"],
        NotifyProviderKind.Pushover => ["token", "user_key"],
        _ => ["url"],
    };

    /// <summary>
    /// Failed, but the secret IS stored - which the caller has to be able to tell.
    /// </summary>
    /// <remarks>
    /// Everything after the store succeeds is a half-completion, and reporting it the same way as
    /// "nothing happened" is what makes a caller - the GUI most of all - offer to try again when
    /// the credential is already safely in the store. The payload names it, so <c>--json</c> can
    /// see it, and the remedy is the one manual step that remains.
    /// </remarks>
    private static int Stored(
        CommandContext ctx, string verb, InstallPaths paths, string name, string code,
        string message, string key)
    {
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = code,
            Message = message,
            Path = paths.ConfigFile,
            Remedy = $"'{name}' IS stored - do not enter it again. Set "
                   + $"{key} = \"@secret:{name}\" by hand to start using it.",
        });

        return ctx.Output.Complete(verb, ExitCode.Errors, new SecretResult
        {
            Verb = "set-secret",
            Path = paths.SecretsFile,
            Names = [name],
        });
    }

    /// <summary>
    /// Writes the store, or says why it could not be. True when it was written.
    /// </summary>
    /// <remarks>
    /// Four call sites saved with nothing around them, so a full disk or a locked file reached
    /// CommandContext.Guarded and came out as LR1006: "a defect in the product, not a problem
    /// with the machine", above a remedy saying nothing about what was done can be relied on.
    /// Wrong twice - AtomicJson writes a temporary sibling and moves it, so a failure leaves the
    /// previous contents exactly as they were.
    /// </remarks>
    private static bool Stored(
        CommandContext ctx, SecretStore store, InstallPaths paths, ISecretPlatform platform)
    {
        var owner = paths.Scope == InstallScope.PerUser ? platform.CurrentUserSid : null;

        try
        {
            store.Save(TimeProvider.System, harden: temp => platform.Harden(temp, owner));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.SecretStoreUnwritable,
                Message = $"The secret store could not be written: {e.Message}",
                Path = paths.SecretsFile,
                Remedy = "Nothing was stored, and what was there is unchanged. "
                       + "Check free space and the permissions on that file.",
            });

            return false;
        }
    }

    /// <summary>One diagnostic and an error exit, which this verb does in eight places.</summary>
    private static int Fail(
        CommandContext ctx, string verb, string code, string message,
        string? path = null, string? remedy = null)
    {
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = code,
            Message = message,
            Path = path,
            Remedy = remedy,
        });

        return ctx.Output.Complete<SecretResult>(verb, ExitCode.Errors, null);
    }

    public static int List(CommandContext ctx, ISecretPlatform platform, string? configDir)
    {
        if (Refuse(ctx, platform, "secret list", needsElevation: false) is { } refusal)
        {
            return refusal;
        }

        var paths = InstallPaths.Resolve(configDir);
        var path = paths.SecretsFile;

        // provision: false - listing must never mint a key, and a reader has no business
        // holding write access to the one that protects everything already stored.
        var protector = platform.CreateProtector(provision: false);
        var store = SecretStore.Load(path, protector, platform.EntropyId, platform.MachineFingerprint);

        var owner = paths.Scope == InstallScope.PerUser ? platform.CurrentUserSid : null;
        var protection = platform.Verify(path, owner);

        if (store.Detail is { } detail)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                // Not an error: a store this account may not read is what a correctly protected
                // one looks like from an ordinary account, and a GUI polling this verb should
                // not light up red because of it.
                Severity = store.Status == SecretStoreStatus.Unreadable ? Severity.Info : Severity.Warning,
                Code = DiagnosticCode.SecretStoreUnreadable,
                Message = detail,
                Path = path,
            });
        }

        var entries = new List<SecretEntryDto>();
        foreach (var name in store.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var meta = store.Meta(name);
            entries.Add(new SecretEntryDto
            {
                Name = name,
                Created = meta?.Created,
                Updated = meta?.Updated,
                SetBy = meta?.SetBy,
                Status = Status(store, name, platform),
            });
        }

        ctx.Output.Line($"{path}   {store.Status}, {entries.Count} secret(s)");
        if (protection is SecretsProtection.TooOpen)
        {
            ctx.Output.Line("  WARNING: this file is readable by accounts that are not administrators.");
        }

        foreach (var e in entries)
        {
            var updated = e.Updated is { } u ? u.ToString("u") : "";
            ctx.Output.Line($"  {e.Name,-24} {updated,-22} {e.SetBy,-24} {e.Status}");
        }

        return ctx.Output.Complete("secret list", ExitCode.Ok, new SecretListResult
        {
            Path = path,
            Protection = store.Status.ToString(),
            FileProtection = protection.ToString(),
            Secrets = entries,
        });
    }

    public static int Remove(CommandContext ctx, ISecretPlatform platform, string name, string? configDir)
    {
        if (Refuse(ctx, platform, "secret remove", needsElevation: true) is { } refusal)
        {
            return refusal;
        }

        var paths = InstallPaths.Resolve(configDir);
        var protector = platform.CreateProtector(provision: false);
        var store = SecretStore.Load(
            paths.SecretsFile, protector, platform.EntropyId, platform.MachineFingerprint);

        if (!store.Contains(name))
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.SecretMissing,
                Message = $"There is no secret called '{name}'.",
                Path = paths.SecretsFile,
                Remedy = "Run 'winlogrotate secret list' to see what is stored.",
            });
            return ctx.Output.Complete<SecretResult>("secret remove", ExitCode.Errors, null);
        }

        if (!Stored(ctx, store.Remove(name), paths, platform))
        {
            return ctx.Output.Complete<SecretResult>("secret remove", ExitCode.Errors, null);
        }

        Journal(paths, name, "remove");
        ctx.Output.Line($"Removed '{name}'.");

        return ctx.Output.Complete("secret remove", ExitCode.Ok, new SecretResult
        {
            Verb = "remove",
            Path = paths.SecretsFile,
            Names = [name],
        });
    }

    public static int Test(CommandContext ctx, ISecretPlatform platform, string name, string? configDir)
    {
        if (Refuse(ctx, platform, "secret test", needsElevation: false) is { } refusal)
        {
            return refusal;
        }

        var paths = InstallPaths.Resolve(configDir);
        var protector = platform.CreateProtector(provision: false);
        var store = SecretStore.Load(
            paths.SecretsFile, protector, platform.EntropyId, platform.MachineFingerprint);

        if (!store.TryGet(name, out var value, out var error))
        {
            // The one place a store this account cannot read is an error rather than a note:
            // the operator asked a direct question and we could not answer it.
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                // Missing counts with Ok, not with the failures: "there is no store yet" and
                // "there is a store but no such name" are both "that secret is not there",
                // and telling somebody their store is unreadable when it simply does not
                // exist sends them looking for a problem that is not there.
                Code = store.Status is SecretStoreStatus.Ok or SecretStoreStatus.Missing
                    ? DiagnosticCode.SecretMissing
                    : DiagnosticCode.SecretStoreUnreadable,
                Message = error ?? $"'{name}' could not be read.",
                Path = paths.SecretsFile,
            });
            return ctx.Output.Complete<SecretResult>("secret test", ExitCode.Errors, null);
        }

        // Length, and nothing else. No hash and not even a prefix: a few bytes of SHA-256 over a
        // low-entropy password confirms an offline guess. The length is reported because the
        // reason this verb exists is spotting the trailing newline a shell appended.
        ctx.Output.Line($"ok - {value.Length} character(s), decrypted with {store.Status}.");

        return ctx.Output.Complete("secret test", ExitCode.Ok, new SecretResult
        {
            Verb = "test",
            Path = paths.SecretsFile,
            Names = [name],
            Length = value.Length,
        });
    }

    public static int Import(
        CommandContext ctx, IInputSource input, ISecretPlatform platform,
        FileInfo? fromFile, string? configDir)
    {
        if (Refuse(ctx, platform, "secret import", needsElevation: true) is { } refusal)
        {
            return refusal;
        }

        string text;
        if (fromFile is not null)
        {
            if (!ReadFile(ctx, "secret import", fromFile, out text))
            {
                return ctx.Output.Complete<SecretResult>("secret import", ExitCode.Errors, null);
            }

            WarnAboutPlaintextFile(ctx, platform, fromFile);
        }
        else
        {
            text = input.ReadAllText();
        }

        var paths = InstallPaths.Resolve(configDir);
        var protector = platform.CreateProtector(provision: true);
        if (protector is null)
        {
            return NoProtector(ctx, "secret import");
        }

        var store = SecretStore.Load(
            paths.SecretsFile, protector, platform.EntropyId, platform.MachineFingerprint);

        if (Unusable(ctx, "secret import", store) is { } unusable)
        {
            return unusable;
        }

        var names = new List<string>();
        var line = 0;
        foreach (var raw in text.Split('\n'))
        {
            line++;
            var entry = raw.TrimEnd('\r');

            // Blank lines and comments, so a deployment file can explain itself.
            if (entry.Length == 0 || entry.StartsWith('#'))
            {
                continue;
            }

            var split = entry.IndexOf('=');
            if (split <= 0)
            {
                ctx.Output.Diagnostic(new CliDiagnostic
                {
                    Severity = Severity.Error,
                    Code = DiagnosticCode.ConfigInvalid,
                    Message = $"Line {line} is not 'name=value'.",
                    Path = fromFile?.FullName,
                    Line = line,
                });
                return ctx.Output.Complete<SecretResult>("secret import", ExitCode.Errors, null);
            }

            // Split on the FIRST '=' only: a base64 secret ends in them, and a token that lost
            // its padding is a token that does not work.
            var name = entry[..split].Trim();
            var value = entry[(split + 1)..];

            if (InvalidName(ctx, "secret import", name) is not null)
            {
                return ctx.Output.Complete<SecretResult>("secret import", ExitCode.Errors, null);
            }

            store = store.Set(name, SecretString.From(value), WhoAmI(), TimeProvider.System);
            names.Add(name);
        }

        if (!Stored(ctx, store, paths, platform))
        {
            return ctx.Output.Complete<SecretResult>("secret import", ExitCode.Errors, null);
        }

        foreach (var name in names)
        {
            Journal(paths, name, "set");
        }

        var rehardened = ReportEntropy(ctx, platform);
        ctx.Output.Line($"Imported {names.Count} secret(s) into {paths.SecretsFile}.");

        return ctx.Output.Complete("secret import", ExitCode.Ok, new SecretResult
        {
            Verb = "import",
            Path = paths.SecretsFile,
            Names = names,
            EntropyRehardened = rehardened,
        });
    }

    // ---------------------------------------------------------------------------------------

    private static string? WhoAmI()
    {
        try
        {
            return $"{Environment.UserDomainName}\\{Environment.UserName}";
        }
        catch (PlatformNotSupportedException)
        {
            return Environment.UserName;
        }
    }

    /// <summary>Refuses politely when the platform or the token is wrong. Null to continue.</summary>
    private static int? Refuse(CommandContext ctx, ISecretPlatform platform, string verb, bool needsElevation)
    {
        if (!platform.IsSupported)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.NotSupportedHere,
                Message = "Stored secrets need Windows: they are protected with DPAPI and a "
                        + "machine key in the registry.",
            });
            return ctx.Output.Complete<SecretResult>(verb, ExitCode.Errors, null);
        }

        if (needsElevation && !platform.IsElevated)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.NeedsAdministrator,
                Message = "Changing stored secrets needs administrator rights.",
                Remedy = "The secret store grants SYSTEM and Administrators only, which is what "
                       + "keeps an ordinary account from reading a stored credential.",
            });
            return ctx.Output.Complete<SecretResult>(verb, ExitCode.Errors, null);
        }

        return null;
    }

    private static int? InvalidName(CommandContext ctx, string verb, string name)
    {
        // Referenced from TOML as @secret:NAME, so the name has to survive being written there
        // and read back without quoting rules getting involved.
        var ok = name.Length is > 0 and <= 64
                 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

        if (ok)
        {
            return null;
        }

        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.ConfigInvalid,
            Message = $"'{name}' is not a usable secret name.",
            Remedy = "Use up to 64 letters, digits, dot, dash or underscore - it has to be "
                   + "written into a config file as @secret:NAME.",
        });
        return ctx.Output.Complete<SecretResult>(verb, ExitCode.Errors, null);
    }

    private static int NoProtector(CommandContext ctx, string verb)
    {
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.SecretStoreUnreadable,
            Message = "The machine key could not be created or read.",
            Remedy = "It lives in HKLM\\SOFTWARE\\WinLogRotate and needs administrator rights.",
        });
        return ctx.Output.Complete<SecretResult>(verb, ExitCode.Errors, null);
    }

    /// <summary>Stops a write that would discard secrets we cannot read. Null to continue.</summary>
    private static int? Unusable(CommandContext ctx, string verb, SecretStore store)
    {
        // Missing is fine - that is a first run. The rest mean the file holds entries this
        // machine cannot decrypt, and saving over it would destroy them silently.
        if (store.Status is SecretStoreStatus.Ok or SecretStoreStatus.Missing)
        {
            return null;
        }

        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Error,
            Code = DiagnosticCode.SecretStoreUnreadable,
            Message = store.Detail ?? "The secret store cannot be used.",
            Path = store.Path,
            Remedy = "Nothing was written. Move the file aside to start over, having noted which "
                   + "names it holds - 'secret list' still shows them.",
        });
        return ctx.Output.Complete<SecretResult>(verb, ExitCode.Errors, null);
    }

    private static bool ReadFile(CommandContext ctx, string verb, FileInfo file, out string text)
    {
        text = string.Empty;
        try
        {
            text = File.ReadAllText(file.FullName).TrimEnd('\r', '\n');
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Error,
                Code = DiagnosticCode.ArgumentUnusable,
                Message = $"'{file.FullName}' is not a file that could be read: {e.Message}.",
                Remedy = "Check the path and that this account may read it.",
            });
            return false;
        }
    }

    /// <summary>
    /// Warns that a plaintext credential file is sitting on disk, and proceeds anyway.
    /// </summary>
    /// <remarks>
    /// Deliberately a warning rather than a refusal. This is the unattended-rollout path, and
    /// refusing drives people to worse workarounds - typing values into scripts, or leaving the
    /// file somewhere even less private. We never delete it either: it is the operator's file
    /// and possibly their only copy.
    /// </remarks>
    private static void WarnAboutPlaintextFile(CommandContext ctx, ISecretPlatform platform, FileInfo file)
    {
        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Warning,
            Code = DiagnosticCode.SecretInPlainConfig,
            Message = $"{file.FullName} holds credentials in plain text.",
            Path = file.FullName,
            Remedy = "Delete it once the import has been verified. The values are now stored "
                   + "encrypted and machine-bound; the file is not.",
        });
    }

    private static bool ReportEntropy(CommandContext ctx, ISecretPlatform platform)
    {
        if (!platform.RepairedKeyProtection)
        {
            return false;
        }

        ctx.Output.Diagnostic(new CliDiagnostic
        {
            Severity = Severity.Warning,
            Code = DiagnosticCode.SecretStoreUnreadable,
            Message = "The machine key was readable by accounts that are not administrators, "
                    + "and its permissions have been tightened.",
            Remedy = "Stored secrets are unaffected and did not need re-entering. Versions before "
                   + "this one created that key without a descriptor, so it inherited "
                   + "HKLM\\SOFTWARE - where ordinary users have read.",
        });
        return true;
    }

    /// <summary>
    /// Records the change. The name only - never a value, and never anything derived from one.
    /// </summary>
    private static void Journal(InstallPaths paths, string name, string action)
    {
        try
        {
            using var journal = JournalWriter.Open(paths.JournalDirectory, TimeProvider.System);
            journal.Write(new CliEvent
            {
                Ts = string.Empty,
                Run = string.Empty,
                Operation = Op.Secret,
                Phase = Phase.Apply,
                Result = OpResult.Ok,
                Src = name,
                Reason = action,
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The secret was stored. Failing to record that is not a reason to report failure.
        }
    }

    private static string Status(SecretStore store, string name, ISecretPlatform platform) =>
        store.Status switch
        {
            SecretStoreStatus.Ok when platform.IsElevated =>
                store.TryGet(name, out _, out _) ? "ok" : "UNREADABLE",

            // Not decrypted, so not claimed either. A "?" is honest where "ok" would be a guess.
            SecretStoreStatus.Ok => "?",
            _ => store.Status.ToString(),
        };
}
