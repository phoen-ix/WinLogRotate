using System.Text;
using System.Text.Json;
using WinLogRotate.Core.Secrets;
using WinLogRotate.Gui.Cli;
using WinLogRotate.Gui.Ui;
using WinLogRotate.Hosting.Security;

namespace WinLogRotate.Gui.Pages;

/// <summary>Who finds out when rotation stops working, and whether they actually would.</summary>
/// <remarks>
/// Read-only apart from two actions, like every other page here: everything is a shell-out to a
/// verb that exists without WinForms, because this tool has to stay usable on Server Core where
/// WinForms cannot run at all.
/// </remarks>
public sealed class NotificationsPage : UserControl
{
    private readonly CliRunner _cli;
    private readonly string? _configDir;

    private readonly Label _summary = new() { Dock = DockStyle.Top, Height = 92 };

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        MultiSelect = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };

    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24 };
    private readonly Button _credential = new() { Text = "Set credential...", Width = 140, FlatStyle = FlatStyle.System };

    public NotificationsPage(CliRunner cli, string? configDir)
    {
        _cli = cli;
        _configDir = configDir;

        var refresh = new Button { Text = "Refresh", Width = 90, FlatStyle = FlatStyle.System };
        refresh.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);

        var test = new Button { Text = "Send test", Width = 110, FlatStyle = FlatStyle.System };
        test.Click += async (_, _) => await TestAsync().ConfigureAwait(true);

        var clear = new Button { Text = "Clear suppressed", Width = 140, FlatStyle = FlatStyle.System };
        clear.Click += async (_, _) => await ResetAsync().ConfigureAwait(true);

        LrDialog.AddShield(_credential);
        _credential.Click += async (_, _) => await CredentialAsync().ConfigureAwait(true);

        _grid.Columns.Add("what", "Provider or target");
        _grid.Columns.Add("kind", "Kind");
        _grid.Columns.Add("where", "Goes to");
        _grid.Columns.Add("credential", "Credential");
        _grid.Columns["what"]!.FillWeight = 30;
        _grid.Columns["kind"]!.FillWeight = 12;
        _grid.Columns["where"]!.FillWeight = 38;
        _grid.Columns["credential"]!.FillWeight = 20;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        toolbar.Controls.AddRange([refresh, test, clear, _credential]);

        Controls.Add(_grid);
        Controls.Add(_status);
        Controls.Add(toolbar);
        Controls.Add(_summary);

        Load += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>The provider named by the selected row, if that row is a provider.</summary>
    private (string Name, string Kind)? Selected()
    {
        if (_grid.CurrentRow?.Tag is not string kind || _grid.CurrentRow.Cells["what"].Value is not string name)
        {
            return null;
        }

        return (name, kind);
    }

    private async Task LoadAsync()
    {
        var result = await _cli.RunAsync(
            CliArgs.For(_configDir, "notify", "show", "--json")).ConfigureAwait(true);

        // Cleared before the failure is reported, not after. Returning early left the previous
        // refresh's rows on screen beside an error status line, so a refresh that failed looked
        // exactly like one that succeeded a minute ago - and the operator reads rows, not the
        // line underneath them.
        _grid.Rows.Clear();

        if (!result.Ok)
        {
            _status.ForeColor = Theme.Current.Danger;
            _status.Text = result.Describe();

            // Exit 4 is the only code that says nothing about what was or was not done can be
            // relied on, so it is the only one worth interrupting for - a configuration error is
            // exit 2 and belongs in the status line underneath.
            if (result.IsDefect)
            {
                LrDialog.Error(this, "Notifications", result.Describe(), result.Details);
            }

            return;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var payload = document.RootElement.GetProperty("result");

            var on = payload.GetProperty("on").GetString();
            var would = payload.GetProperty("wouldSend").GetBoolean();

            var summary = new StringBuilder();
            summary.Append(would ? "Reporting is on" : "Nothing would be sent");
            // Lowercased here, not on the wire. "on" is an enum name in the envelope - the
            // contract's job is to be unambiguous, this sentence's job is to read like English.
            summary.Append(", ").Append(on?.ToLowerInvariant()).Append('.').AppendLine();
            summary.Append("Threshold ").Append(payload.GetProperty("threshold").GetString())
                   .Append(" and above, reminder after ").Append(payload.GetProperty("remindAfter").GetString())
                   .Append('.').AppendLine();
            summary.Append("Proxy: ").Append(payload.GetProperty("proxy").GetString()).AppendLine();
            summary.Append(payload.TryGetProperty("certificatePin", out var pin) && pin.ValueKind == JsonValueKind.String
                ? $"TLS pinned to {pin.GetString()}"
                : "TLS: the machine's certificate store decides");

            _summary.Text = summary.ToString();

            foreach (var target in payload.GetProperty("targets").EnumerateArray())
            {
                var usable = target.GetProperty("usable").GetBoolean();

                var row = _grid.Rows[_grid.Rows.Add(
                    Str(target, "display"),
                    Str(target, "scheme"),
                    usable ? string.Empty : Str(target, "problem"),
                    string.Empty)];

                if (!usable)
                {
                    row.DefaultCellStyle.ForeColor = Theme.Current.Danger;
                }
            }

            foreach (var provider in payload.GetProperty("providers").EnumerateArray())
            {
                var kind = Str(provider, "kind");

                var row = _grid.Rows[_grid.Rows.Add(
                    Str(provider, "name"),
                    kind,
                    Str(provider, "target"),
                    provider.GetProperty("credentialFree").GetBoolean()
                        ? "nothing stored"
                        : Str(provider, "credential"))];

                // Marks the row as one "Set credential..." can act on.
                row.Tag = kind;
            }

            _status.ForeColor = Theme.Current.Muted;
            _status.Text = _grid.Rows.Count == 0
                ? "Nothing is configured, so a failed rotation would tell nobody."
                : "Nothing here proves a target works. Use Send test.";
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Said out loud. Swallowing it leaves an empty grid, which reads as "nothing is
            // configured" - the opposite of the truth, and the one wrong answer this page can give
            // that would stop somebody investigating.
            _status.ForeColor = Theme.Current.Danger;
            _status.Text = "The configuration could not be read. Run 'winlogrotate notify show'.";
        }
    }

    /// <summary>
    /// A property as text, treating absent and null alike.
    /// </summary>
    /// <remarks>
    /// The envelope is hand-walked because <c>CliEnvelope&lt;T&gt;</c>'s result types are
    /// registered only in the CLI's own serializer context, which is internal to it. Not because
    /// Contracts is out of reach: it is referenced, and the run pane renders the event stream
    /// through <see cref="WinLogRotate.Contracts.CliEventText"/>, which is shared on purpose.
    /// </remarks>
    private static string Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private async Task TestAsync()
    {
        _status.ForeColor = Theme.Current.Muted;
        _status.Text = "Sending...";

        var result = await _cli.RunAsync(
            CliArgs.For(_configDir, "notify", "test", "--json")).ConfigureAwait(true);

        // notify test always exits 0 - a webhook outage is not a rotation failure - so the counts
        // are what say whether anything arrived. Scraping stdout for the word FAILED was a text
        // contract nothing pins, and it could not tell "sent to none" from "sent to three".
        var view = NotifyTestProjection.From(result);

        LrDialog.Show(
            this,
            view.Tone switch
            {
                CheckTone.Clean => DialogKind.Info,
                CheckTone.Warning => DialogKind.Warning,
                _ => DialogKind.Error,
            },
            "Send test",
            view.Message,
            view.Details);

        _status.Text = "Test finished.";
    }

    private async Task ResetAsync()
    {
        if (!LrDialog.Confirm(this, "Clear suppressed",
                "Clear the failure counters on every suppressed channel?"))
        {
            return;
        }

        var result = await _cli.RunAsync(
            CliArgs.For(_configDir, "notify", "reset")).ConfigureAwait(true);

        _status.ForeColor = result.Ok ? Theme.Current.Muted : Theme.Current.Danger;
        _status.Text = result.Ok ? "Suppressed channels cleared." : result.Describe();
    }

    /// <summary>
    /// Stores a credential, handing it to an elevated child over a pipe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pipe exists because elevation needs <c>runas</c>, <c>runas</c> needs
    /// <c>UseShellExecute</c>, and that forbids redirecting the child's standard input. The two
    /// easier answers are both permanently excluded: an argument is readable by every local
    /// administrator and is written into audit events, and a temporary file is plaintext on disk.
    /// </para>
    /// <para>
    /// <b>There is no fallback.</b> If the pipe cannot be created or the child never connects,
    /// nothing is stored and the operator is given the command to run themselves. Degrading to a
    /// worse channel because the good one failed is how the feature would quietly stop being one.
    /// </para>
    /// </remarks>
    private async Task CredentialAsync()
    {
        if (Selected() is not { } provider)
        {
            LrDialog.Info(this, "Set credential", "Select a provider row first.");
            return;
        }

        var asked = SecretPrompt.Ask(this, provider.Name, provider.Kind);
        if (asked is null)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            Array.Clear(asked.Value);
            return;
        }

        // Two of these at once would each load config.toml, edit it, and save - and the second
        // save would write back a copy that predates the first, silently undoing it. The button
        // is the only way in, so disabling it is the whole guard.
        _credential.Enabled = false;

        try
        {
            await StoreAsync(asked).ConfigureAwait(true);
        }
        finally
        {
            _credential.Enabled = true;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task StoreAsync(SecretRequest asked)
    {
        using var pipe = SecretPipeServer.Create();

        if (pipe is null)
        {
            LrDialog.Error(this, "Set credential",
                "A private channel to the elevated helper could not be created, so nothing was stored.",
                $"winlogrotate notify set-secret {asked.Provider} {asked.Field}");

            Array.Clear(asked.Value);
            return;
        }

        // One definition of the frame, in Core, which the GUI, the CLI and the pipe server all
        // reference. Two would eventually disagree in the one path no build server can exercise.
        //
        // The char[] overload, not the string one: routing it through a string here would undo
        // the reason SecretPrompt hands back characters in the first place.
        byte[] message;

        try
        {
            message = SecretFrame.Wrap(asked.Value);
        }
        finally
        {
            // Cleared however Wrap ended, so an exception cannot leave the credential in the
            // array that the dialog handed over precisely so it could be cleared.
            Array.Clear(asked.Value);
        }

        var outcome = SecretPipeOutcome.TimedOut;

        var result = await _cli.RunElevatedAsync(
            CliArgs.For(_configDir,
                "notify", "set-secret", asked.Provider, asked.Field, "--from-pipe", pipe.Name),
            onLine: null,
            onStarted: id => outcome = pipe.Deliver(id, message, TimeSpan.FromSeconds(30)))
            .ConfigureAwait(true);

        Array.Clear(message);

        if (result.Failure == CliFailure.UacDeclined)
        {
            _status.ForeColor = Theme.Current.Muted;
            _status.Text = "Elevation was cancelled. Nothing was stored.";
            return;
        }

        // The child's exit code is authoritative, not our view of the pipe: it exits 0 only after
        // the value is stored AND the configuration rewritten, and the drain can report a failure
        // for a child that read the value and closed promptly. Trusting the pipe here would tell
        // an operator nothing was stored about a credential that is.
        if (result.Ok)
        {
            LrDialog.Info(this, "Set credential",
                $"Stored, and {asked.Provider} now refers to it rather than holding it.");

            await LoadAsync().ConfigureAwait(true);
            return;
        }

        // Hoisted: Details is computed from the two output streams, and the expression below
        // used to read it twice.
        var details = result.Details;

        LrDialog.Error(this, "Set credential",
            outcome == SecretPipeOutcome.Delivered
                ? "The value reached the helper, which could not store it."
                : $"The value was not delivered ({outcome}), so nothing was stored.",
            // What the child said, where it said anything. The command line is the fallback it
            // has always shown - useful for running it yourself, but not an explanation.
            details.Length > 0
                ? details
                : $"winlogrotate notify set-secret {asked.Provider} {asked.Field}");
    }
}
