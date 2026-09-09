using WinLogRotate.Cli.Output;
using WinLogRotate.Contracts;
using WinLogRotate.Core;
using WinLogRotate.Core.Journaling;
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
                Remedy = "Pipe it in: 'value' | winlogrotate secret set " + name,
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
            SecretsPath(paths), protector, platform.EntropyId, platform.MachineFingerprint);

        if (Unusable(ctx, "secret set", store) is { } unusable)
        {
            return unusable;
        }

        var owner = paths.Scope == InstallScope.PerUser ? platform.CurrentUserSid : null;

        store.Set(name, value, WhoAmI(), TimeProvider.System)
             .Save(TimeProvider.System, harden: temp => platform.Harden(temp, owner));

        Journal(paths, name, "set");

        var rehardened = ReportEntropy(ctx, platform);
        ctx.Output.Line($"Stored '{name}' in {SecretsPath(paths)}.");
        ctx.Output.Line($"Reference it from a job or provider as: @secret:{name}");

        return ctx.Output.Complete("secret set", ExitCode.Ok, new SecretResult
        {
            Verb = "set",
            Path = SecretsPath(paths),
            Names = [name],
            EntropyRehardened = rehardened,
        });
    }

    public static int List(CommandContext ctx, ISecretPlatform platform, string? configDir)
    {
        if (Refuse(ctx, platform, "secret list", needsElevation: false) is { } refusal)
        {
            return refusal;
        }

        var paths = InstallPaths.Resolve(configDir);
        var path = SecretsPath(paths);

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
            SecretsPath(paths), protector, platform.EntropyId, platform.MachineFingerprint);

        if (!store.Contains(name))
        {
            ctx.Output.Diagnostic(new CliDiagnostic
            {
                Severity = Severity.Warning,
                Code = DiagnosticCode.SecretMissing,
                Message = $"There is no secret called '{name}'.",
                Path = SecretsPath(paths),
                Remedy = "Run 'winlogrotate secret list' to see what is stored.",
            });
            return ctx.Output.Complete<SecretResult>("secret remove", ExitCode.Errors, null);
        }

        var owner = paths.Scope == InstallScope.PerUser ? platform.CurrentUserSid : null;
        store.Remove(name).Save(TimeProvider.System, harden: temp => platform.Harden(temp, owner));

        Journal(paths, name, "remove");
        ctx.Output.Line($"Removed '{name}'.");

        return ctx.Output.Complete("secret remove", ExitCode.Ok, new SecretResult
        {
            Verb = "remove",
            Path = SecretsPath(paths),
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
            SecretsPath(paths), protector, platform.EntropyId, platform.MachineFingerprint);

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
                Path = SecretsPath(paths),
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
            Path = SecretsPath(paths),
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
            SecretsPath(paths), protector, platform.EntropyId, platform.MachineFingerprint);

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

        var owner = paths.Scope == InstallScope.PerUser ? platform.CurrentUserSid : null;
        store.Save(TimeProvider.System, harden: temp => platform.Harden(temp, owner));

        foreach (var name in names)
        {
            Journal(paths, name, "set");
        }

        var rehardened = ReportEntropy(ctx, platform);
        ctx.Output.Line($"Imported {names.Count} secret(s) into {SecretsPath(paths)}.");

        return ctx.Output.Complete("secret import", ExitCode.Ok, new SecretResult
        {
            Verb = "import",
            Path = SecretsPath(paths),
            Names = names,
            EntropyRehardened = rehardened,
        });
    }

    // ---------------------------------------------------------------------------------------

    private static string SecretsPath(InstallPaths paths) =>
        Path.Combine(paths.Root, "secrets.dat");

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
                Code = DiagnosticCode.ConfigUnreadable,
                Message = $"{file.FullName} could not be read: {e.Message}",
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
