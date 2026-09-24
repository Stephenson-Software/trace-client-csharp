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

var trace = new TraceClient("https://trace.danielstephenson.dev", "my-game",
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

trace.Report("startup", tags: new Dictionary<string, string> { { "version", "1.4.0" } });
trace.Report("level-complete", 42.0);

// on shutdown -- also before a short-lived program exits, so the event is sent
trace.Dispose(); // same as trace.Close()
```

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
`User-Agent: trace-client/0.1.0 (<application>)` and a body of

```json
{"application":"my-game","name":"startup","tags":{"version":"1.4.0"}}
```

`value` and `tags` are omitted when not given; `value` is written with the
invariant culture, so a German locale still sends `2.5`. The server assigns
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
