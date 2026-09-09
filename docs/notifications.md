# Notifications

Rotation is unattended, so the interesting question is not "did it work" but "who finds out when
it stops". The `[notify]` table answers that. It reports **what a whole run did**, once per run,
to whoever is on call - over email, a webhook, Pushover, or the Windows Event Log.

This is not logrotate's `mail`. Upstream posts you the rotated log file itself, per job; we report
the run. See [logrotate compatibility](logrotate-compatibility.md#mail-reports-the-run-not-the-log)
for why.

## The shortest thing that works

```toml
[notify]
to = ["eventlog:"]
```

No credential, no network, nothing to break. Failures land in the Application log under event ID
**155**, and any monitoring already watching that log picks them up. Every other target is a
refinement of this one.

## What decides whether anything is sent

Three questions, in order. `winlogrotate notify show` prints the answers, and
`winlogrotate notify status` prints what it decided last time.

| | Setting | Default |
|---|---|---|
| Is it worth reporting? | `threshold` | `warning` and above |
| Is it new? | `on` | `change` |
| Has it been quiet too long? | `remind_after` | `7d` |

`on = "change"` is the one that makes this usable. A job that fails every night for a month
produces one message, then a reminder each week - not thirty. Set `on = "every"` if you have an
aggregator that wants the raw stream, and `on = "never"` to keep the configuration while turning
delivery off.

Three things are worth knowing because they surprise people:

- **The first run says nothing.** Outcomes are recorded and nothing is sent, so installing this on
  a machine that is already broken does not produce a wall of alerts about problems that predate
  the install. That is how a source gets muted in week one.
- **Recoveries are reported too.** A job going green closes the message already in the inbox.
- **A job can opt out** with `notify = false`, for the flaky NAS share nobody will fix. It silences
  that job's *rotation* failures, not its configuration problems - those are run-scoped.

## Targets

`to` takes a list. Each entry is either a **provider name** - a `[notify.KIND.NAME]` table
elsewhere in the file - or an **inline target**, written as `scheme:destination`.

| Scheme | Example | Needs |
|---|---|---|
| `eventlog:` | `eventlog:` | Windows, and a per-machine install |
| `http:` / `https:` | `https://hooks.example.com/services/T/B/xxxx` | nothing |
| `smtp:` | `smtp:ops@example.com` | a `[notify.email.*]` provider for the relay |
| `pushover:` | `pushover:uQiRzp6twxx` | a `[notify.pushover.*]` provider for the token |

`command:`, `service:` and `event:` parse today and **are not delivered by this build**. They run
code on this machine, so they land together with the configuration-directory check that makes that
safe. A target naming one produces `LR5001` every run rather than silence - a hook that quietly
does nothing is indistinguishable from a hook that ran.

## Webhooks

The general-purpose target. Slack, Teams, Discord and ntfy are all one of these.

```toml
[notify]
to = ["webhook.slack"]

[notify.webhook.slack]
url  = "@secret:slack-hook"
body = '{"text": "{subject}\n\n{body}"}'
```

The URL is stored as a credential, not written in the file, because **a webhook URL *is* the
credential** - its entropy is in its path, and anyone holding it can post. `winlogrotate notify
show` prints `secret:slack-hook`, never the value.

`body` is a template. These placeholders are substituted:

`{subject}` `{body}` `{severity}` `{job}` `{machine}` `{reason}` `{run}` `{fingerprint}`

Every substituted value is escaped for the declared `content_type` before it is placed, so a log
file called `a"b.log` cannot end a JSON string early or add fields. The template supplies the
quotes; you do not escape anything yourself. Omit `body` entirely and the whole set is posted as a
flat JSON object instead.

| Key | Default | Notes |
|---|---|---|
| `url` | — | required; use `@secret:NAME` |
| `method` | `POST` | |
| `content_type` | `application/json` | decides the escaping: JSON, form, XML, or none |
| `body` | unset | unset means a flat JSON object of every field |
| `max_message` | unlimited | see below |

**`max_message` matters for Discord and ntfy**, which discard anything longer *silently* - so an
unset limit does not fail loudly, the message simply never arrives. Discord accepts 2000
characters, ntfy 4096. Truncation drops whole lines from the end and always keeps the head and the
"go and look" footer.

## Email

```toml
[notify]
to = ["email.relay"]

[notify.email.relay]
host = "smtp.internal.example"
port = 25
from = "winlogrotate@example.com"
to   = ["ops@example.com"]
```

That is the common case on a Windows estate: an internal relay that authorises by IP and needs no
credential at all.

| Key | Default | Notes |
|---|---|---|
| `host` / `port` | — / `25` | |
| `auth` | `none` | `none`, `integrated`, `login` |
| `tls` | `opportunistic` | `none`, `opportunistic`, `required` |
| `delivery` | `network` | or `pickup` |
| `pickup_directory` | — | required for `delivery = "pickup"` |
| `from` / `to` | — | `to` combines with the address in an `smtp:` target |
| `username` / `password` | — | `password` should be `@secret:NAME` |
| `subject_prefix` | — | prepended, for inbox rules |

**Prefer the options that store nothing.** `delivery = "pickup"` drops a file into an IIS SMTP or
Exchange pickup directory: no credential, no network call, and therefore no timeout that can delay
a rotation. `auth = "integrated"` presents the machine account (`DOMAIN\HOST$`) on a domain-joined
relay. `winlogrotate notify show` marks providers that need nothing stored.

### TLS

`opportunistic` attempts STARTTLS and, if the relay does not offer it, sends in the clear **and
says so** with an `LR5001` naming the host. A downgrade nobody is told about is not opportunistic
encryption, it is an unencrypted connection with a reassuring setting next to it. Use
`tls = "required"` to refuse instead.

### Microsoft 365 has a deadline

`auth = "login"` against `smtp.office365.com` **stops working at the end of December 2026**, when
Microsoft disables basic authentication for SMTP. The .NET SMTP client has no OAuth2 and cannot be
made to work with it, so this is not something a future release fixes.

Nothing else is affected - internal relays, IP-authorised relays, pickup directories and
`integrated` never used basic auth. If your only mail path is Microsoft 365 with a password, use a
webhook, or relay through a local server that holds the credential itself.

## Pushover

```toml
[notify]
to = ["pushover.oncall"]

[notify.pushover.oncall]
token    = "@secret:pushover-token"
user_key = "@secret:pushover-user"
priority = 1
```

Messages are truncated to Pushover's 1024-character limit before sending, because Pushover discards
longer ones without telling you.

## Credentials

The quickest way to store one, once the provider exists:

```
winlogrotate notify set-secret email.relay password
```

It reads the value from standard input (never from an argument — see below), stores it encrypted,
and rewrites the configuration to say `password = "@secret:email-relay"` in the same step. That is
the `LR9006` remedy as one command: doing it in two is how the second half gets forgotten, leaving
the password in the file with a warning that has stopped looking urgent.

It edits **one line**. Every comment, blank line and indent in `config.toml` survives, which is the
whole reason this product's configuration is TOML rather than YAML or JSON.

It will not create a provider — `[notify.email.relay]` has to exist first — and it refuses a field
the provider kind does not have, so a `token` on an email provider is an error rather than a key
nothing reads.

### The four ways to name one

Four ways to name one. The indirection matters more than the encryption behind it: a configuration
file saying `password = "@secret:relay"` is safe to paste into a support ticket, commit to a
deployment repository, or screenshot - which is how credentials actually escape.

| Written as | Read from | |
|---|---|---|
| `@secret:NAME` | the encrypted store | **preferred**; `winlogrotate secret set NAME` |
| `@env:NAME` | the environment | for containers and CI |
| `anything else` | the file itself | warns every run (`LR9006`) |
| `@command:...` | a vault, by running it | **not delivered by this build** - refused, not ignored |

The store is DPAPI machine-scope with per-install entropy, readable only by SYSTEM and
Administrators, and it does not survive being copied to another machine - by design. `@@` escapes a
literal that really does start with `@`.

**A credential that cannot be resolved drops the whole target**, with an `LR5001` naming the field
and the reason. It is never attempted anonymously: a relay that accepts unauthenticated mail would
let the send succeed, and you would believe authentication was working until the day it tightened.

**Uninstalling keeps the store.** `secrets.dat` survives an uninstall unless `/PURGEDATA` is passed,
so an in-place upgrade does not lose your passwords - and so a decommissioned machine still has them
on disk. Pass `/PURGEDATA`, or delete the data directory, when you retire one.

## Proxies and certificate pinning

```toml
[notify]
proxy    = "http://proxy.example.com:3128"
no_proxy = ["internal.example.com"]
server_cert_thumbprint = "9F:86:D0:81:88:4C:7D:65..."
```

`proxy = "none"` forces direct connections; unset uses the machine's own configuration. Entries in
`no_proxy` match the host and its subdomains. Single-label hosts and hosts in this machine's own
domain bypass the proxy without being listed, as they do everywhere else.

`server_cert_thumbprint` is a SHA-256 of the certificate you expect, in any punctuation. When set,
**only** that certificate is accepted - which is what lets an internal CA or a self-signed relay
work without disabling verification. It applies to HTTPS, Pushover and SMTP alike.

**There is deliberately no `insecure` option**, and there will not be one. Such a flag is set once
during an incident and never unset. Pinning is the supported answer to "my relay's certificate does
not validate".

## Failure, and what it costs you

Delivery never changes a run's exit code. The rotation already happened, and monitoring keyed on
exit codes must not learn about a webhook outage.

**A message counts as reported only when every channel that was attempted accepted it.** With
`to = ["eventlog:", "email.relay"]`, an Event Log write succeeding does not mean anyone was told -
so a mail outage does not silently mark the incident as old news and leave you never emailed.

That would deadlock on a permanently broken channel, so the breaker resolves it: after
`breaker_after` failed runs (5 by default) a channel is suppressed for a cooldown that doubles each
time, and a **suppressed channel no longer blocks**. In practice one broken destination costs about
five duplicate alerts on the healthy ones, then everything converges. `notify status` shows which
channels are suppressed; `notify reset` clears one.

| Setting | Default | |
|---|---|---|
| `budget` | `30s` | wall clock for the **whole** phase, not per target |
| `retries` | `2` | per channel, spent out of that same budget |
| `breaker_after` | `5` | failed runs before a channel is suppressed |
| `breaker_cooldown` | `5` | runs suppressed, doubling each time |
| `redact` | `[]` | strings masked out of every message |

The budget is shared evenly between channels, so one unreachable relay cannot spend it all and
leave the working channels unattempted. It is also clamped by the run's own deadline: the scheduled
task allows one hour, and a rotation that has used 59 of them gets a notification phase short
enough to finish first. Being killed at the limit reports `0x41306`, which is indistinguishable
from an operator pressing **Stop** - so the run's only machine-readable outcome would say it was
terminated when in fact it succeeded. `LR5005` reports the clamp, and nothing is recorded as sent.

Only `4xx`-class rejections and connection failures are treated differently: `400`, `401`, `403`
and `404` are never retried, because repeating a wrong request is a slower way to be wrong and,
against a rate-limited endpoint, is how a misconfiguration becomes a lockout.

## From the GUI

The **Notifications** page shows what is configured, sends a test, clears a suppressed channel,
and stores a credential.

That last one is the only thing the GUI does that the command line cannot do more easily, and it
takes a detour to do it. Storing a secret needs administrator rights, and elevating a child
process on Windows requires `runas`, which makes redirecting that child's standard input
impossible. So the GUI opens a private named pipe, launches the elevated helper, and hands the
value over that — never as an argument, which every local administrator can read out of
`Win32_Process` and which Windows writes verbatim into 4688 audit events, and never through a
temporary file, which would be the plaintext on disk that the encrypted store exists to avoid.

The pipe is created before the helper starts, under a random name it claims exclusively, readable
only by you and by administrators, and the value is written only after the helper's process
identity has been checked. **If any of that fails, nothing is stored** and the page gives you the
`notify set-secret` command to run yourself. There is deliberately no second-best channel.

One caveat worth knowing: the GUI is the least-tested part of this product, and its window has
never been rendered by CI. The command line does everything it does.

## Checking it works

```
winlogrotate notify show                          # what is configured, and what is missing
winlogrotate notify test                          # send a real message to every channel, now
winlogrotate notify status                        # what was last decided, and what is suppressed
winlogrotate notify set-secret <provider> <field> # store a credential and point the config at it
winlogrotate notify reset                         # clear a suppressed channel
```

`notify test` **records nothing** - no history, no breaker counters, no state - so running it can
never change what tonight's run decides. It also ignores an open breaker and says so, because the
command you reach for to check whether a channel is fixed must not be the one that refuses to look.

`winlogrotate doctor` reports the same facts without opening a socket, which is why it is safe to
poll.

## Per-machine and per-user installs

Everything above works on both. The exceptions:

- **`eventlog:` needs a per-machine install.** Registering an Event Log source needs administrator,
  and a per-user install deliberately registers nothing. The target reports this rather than failing
  quietly.
- **Hooks that run code need one too**, for the same reason they always have: a configuration
  directory its owner can write is a configuration directory that can run programs as SYSTEM.

Nothing about email, webhooks or Pushover is restricted - a per-user install can report perfectly
well. But a per-user configuration directory is writable by its owner, so a credential written in
the file rather than stored (`LR9006`) is readable by that account too.

## Diagnostic codes

| Code | Event | Means |
|---|---:|---|
| `LR5001` | 150 | A target is unparseable, unsupported, or its credential could not be read |
| `LR5002` | 151 | A channel could not be reached. Never an error |
| `LR5003` | 152 | A channel is suppressed after repeated failures |
| `LR5004` | 153 | Notification history was unreadable; change detection restarts |
| `LR5005` | 154 | The phase was cut short to protect the run's deadline |
| — | 155 | **The digest itself**, delivered to an `eventlog:` target |

Alert on **155**. The rest report on the notification machinery; 155 carries the thing you asked to
be told. Full table in [diagnostics](diagnostics.md).
