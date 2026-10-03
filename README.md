# trace-client (C#)

**One call to report that a program was used.**

A dependency-free C# client for a [trace](https://trace.danielstephenson.dev)
server — the central place a fleet of programs reports usage events to. The
whole library is one file, `src/TraceClient/TraceClient.cs`, using nothing but
`System.Net.Http`, and the integration on the program side is meant to stay one
call. It targets **netstandard2.0 and C# 7.3**, so Unity, .NET Framework 4.6.1+
and modern .NET can all vendor it unchanged. The
[Java](https://github.com/Stephenson-Software/trace-client-java) and
[Python](https://github.com/Stephenson-Software/trace-client-python) clients
speak the same wire format and make the same promises.

```csharp
using StephensonSoftware.Trace;

var trace = new TraceClient("https://trace.danielstephenson.dev", "my-game", "1.4.0",
                            key: settings.UsageReportingKey,
                            enabled: settings.UsageReportingEnabled,
                            log: message => Debug.WriteLine(message)); // optional

if (trace.IsEnabled)
{
    Console.WriteLine("Usage reporting is on: my-game sends its name and version to "
        + "https://trace.danielstephenson.dev. Turn it off in settings, or with TRACE_USAGE_REPORTING=off. "
        + "Details: https://github.com/Stephenson-Software/trace#usage-reporting");
}
else
{
    Console.WriteLine("Usage reporting is off (" + trace.DisabledReason + ").");
}

trace.Report("startup");
trace.Report("level-complete", 42.0);

// on shutdown -- also before a short-lived program exits, so the event is sent
trace.Dispose(); // same as trace.Close()
```

## Every event carries the program's version

The third constructor argument is the program's own version, and it is
required: a null or blank one, or one over 255 characters after trimming,
throws `ArgumentException`. Every event the client sends — `startup`,
`level-complete`, anything else — carries it as the tag `version`, so every
event can be tied to a release, not just `startup`. An event that passes its
own `version` tag keeps it, and the dictionary passed to `Report` is never
modified. There is no need to tag `startup` by hand any more. The `version` tag
counts toward the server's 32-tag limit, so an event may carry 31 of its own.

Before 0.2.0, the constructor took no version and only events tagged by hand
carried one. Upgrading is one argument after the application name — for
example `Application.version` in Unity, or the assembly's informational
version elsewhere. Pass `key`, `enabled` and `log` by name, as above, so a
key can never land in the version's place.

## Every event carries a random installation ID

Since 0.3.0, every event can also carry the tag `install`: a random ID for
the installation, so the trace server can count **distinct installations**
("active installs in the last 30 days") rather than raw events. This is the
same idea as bStats' `serverUuid`, and it is said out loud here because it is
the one thing the client sends that is the same from one event to the next.

**What it is.** A `Guid.NewGuid()`. It is not derived from anything — not a
hostname, an IP address, a MAC address, a player, an account or a path. It
identifies no person and no address; all it can say is "these events came
from the same installation". (The trace server still sees the IP address of
every HTTP request, as every web server does.)

**Where it lives.** Wherever the program decides — there is no hidden
default location, and without one of the options below no ID is made up and
nothing is written. The simplest way is a file the program chooses:

```csharp
var trace = new TraceClient(url, "my-game", "1.4.0",
                            key: settings.UsageReportingKey,
                            enabled: settings.UsageReportingEnabled,
                            installIdFile: Path.Combine(settings.DataDirectory, "trace-install-id"));
```

The client reads the first line of that file that is 1–255 of
`[A-Za-z0-9_.-]`; when the file is missing or holds no such line, it writes a
new random ID there (creating parent directories). If the file cannot be read
or written, a fresh ID is used in memory for that run only — the constructor
still never throws for it. `TraceClient.InstallIdFromFile(path)` does the same
on its own, but note that calling it directly reads and writes the file
whatever the opt-outs say; passing `installIdFile:` lets the client do it only
when reporting is on.

A program that already keeps settings can store the ID there instead and pass
it explicitly; `installId:` wins over `installIdFile:`:

```csharp
var trace = new TraceClient(url, "my-game", "1.4.0", key: key,
                            installId: settings.UsageReportingInstallId); // null or blank: none sent
```

An explicit ID is trimmed; one over 255 characters throws `ArgumentException`,
like an overlong version. An event that passes its own `install` tag keeps
it, and `install` is never added past the server's 32-tag limit.
`trace.InstallId` returns the ID in use (`null` when disabled or when there is
none), so a program can print it.

**Resetting it.** Delete the file (or the setting); the next start makes a new
one. Or put your own value in it.

**Opting out.** Every [opt-out](#turning-it-off) also stops the ID: a disabled
client never generates one, never writes one, and sends nothing.

## What `Report` promises

| Property | Meaning |
|---|---|
| **Returns immediately** | The HTTP call runs on one background thread the client owns. A game loop can report from its main thread and no frame waits on the network. |
| **Never throws** | A server that is down, slow, or rejecting the key is a dropped report, not an exception in your program — nor is a logger callback that throws. Drops go to the optional `log` callback, otherwise nowhere. |
| **Bounded** | At most 256 reports wait to be sent; past that, new ones are dropped. A trace server that is unreachable for a week costs a few kilobytes, not your memory. |
| **Within the server's limits** | A report the server would reject for its size — more than 32 tags, or an application, name, tag key or tag value longer than 255 characters — is dropped (and logged) instead of sent. Null or blank tag keys and null tag values are skipped. |
| **`Close()` / `Dispose()` drains** | Reports already queued get up to the client timeout (5 s total) to be sent before the thread stops, so a CLI that reports and exits at once does not lose its event. Still bounded: an unreachable server delays exit by at most the timeout, then the in-flight request is cancelled. |

Each request has a 5 s timeout.

## Turning it off

Reporting is **opt-out**, and the person running the program always has the
last word. The constructor checks these in order; the first that applies is
what `DisabledReason` returns, verbatim, so the program can log it:

| Switch | `DisabledReason` |
|---|---|
| Environment, for every trace-reporting program at once: `TRACE_USAGE_REPORTING=off` (also `false`, `0`, `no`) or `DO_NOT_TRACK=1` (also `true`, `yes`; the [consoledonottrack.com](https://consoledonottrack.com) convention), case-insensitive. Always checked first; any other value leaves the program's setting in charge. | `environment` |
| The program's own setting: `enabled: false`. | `config` |
| No key, or a blank one. | `no key` |

`DisabledReason` is `null` when the client reports. A disabled client does
nothing and costs nothing. `TraceClient.EnvironmentDisables()` answers the
environment question on its own, for programs that want it before building a
client. A program that runs on other people's machines should expose the
`enabled` switch in its settings, and say — on startup or the first time it
runs — that reporting is on, how to turn it off, and where the details are:
<https://github.com/Stephenson-Software/trace#usage-reporting>.

## Getting it

**Copy the file.** `src/TraceClient/TraceClient.cs` has no dependencies beyond
`System.Net.Http`. Drop it into your source tree (a Unity `Assets/` folder
works), keep the header so it can be found again, and you are done. On .NET
Framework, reference `System.Net.Http`; everywhere else it is already there.

**Or reference the project** `src/TraceClient/TraceClient.csproj` (assembly
`StephensonSoftware.Trace`). There is no NuGet package yet; the file is the
distribution.

**Unity WebGL** has no threads and no raw HTTP, so build the client with
`enabled: false` there. Nothing is lost: the desktop build reports.

## The wire format

`POST {baseUrl}/api/metrics` with `Authorization: Bearer <key>`,
`User-Agent: trace-client/0.3.0 (<application>)` and a body of

```json
{"application":"my-game","name":"level-complete","value":42,"tags":{"version":"1.4.0","install":"0f8b6c1e-3a52-4c8e-9a0d-6e2f1b7c4d90"}}
```

`value` is omitted when not given, and `tags` always holds at least
`version`, plus `install` when the program gave the client an
[installation ID](#every-event-carries-a-random-installation-id). `value` is written with the invariant culture, so a German locale
still sends `2.5`. The server assigns
the timestamp. A `201` is success; anything else is logged and dropped.

## Keys

A key identifies the program to the server and lets the operator revoke it;
it is scoped to *reporting only*. Because it ships inside the program, it
cannot prove anything — treat trace data as best-effort telemetry, which is
what it is. Ask the trace operator for a key for your program.

## Building

```
dotnet test
```

Tests run the client against the framework's own `HttpListener` on a loopback
port — no more dependencies than the client itself, beyond xUnit. The library
project builds as netstandard2.0 with C# 7.3 so the vendoring floor stays
honest. CI runs the suite on .NET 8 (Linux and Windows) and .NET Framework 4.8
(Windows).

## License

MIT.
