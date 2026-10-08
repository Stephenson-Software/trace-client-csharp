using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Xunit;

// The environment seam is static; the tests that swap it must not overlap.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace StephensonSoftware.Trace.Tests
{
    /// <summary>
    /// Drives the client against a real HTTP server on a loopback port -- the
    /// framework's own HttpListener -- the same way the Java and Python clients'
    /// suites do.
    /// </summary>
    public sealed class TraceClientTest : IDisposable
    {
        // What the client sees as its environment. Empty unless a test says
        // otherwise, so a DO_NOT_TRACK on the machine running the suite cannot
        // fail the tests that expect an enabled client.
        private readonly Dictionary<string, string> _environment = new Dictionary<string, string>();
        private readonly Func<string, string> _realEnvironment;
        private readonly StubServer _server = new StubServer();

        public TraceClientTest()
        {
            _realEnvironment = TraceClient.EnvironmentSource;
            TraceClient.EnvironmentSource = name => _environment.TryGetValue(name, out string v) ? v : null;
        }

        public void Dispose()
        {
            TraceClient.EnvironmentSource = _realEnvironment;
            _server.Dispose();
        }

        [Fact]
        public void Report_PostsTheEventToTheMetricsEndpointWithTheKey()
        {
            // Arrange
            var client = new TraceClient(_server.BaseUrl + "/", "MyGame", "1.2.3", key: "k-123");

            // Act
            client.Report("startup");

            // Assert
            Assert.True(_server.WaitFor(1, TimeSpan.FromSeconds(5)), "the report should reach the server");
            Received request = _server.Received.Single();
            Assert.Equal("POST", request.Method);
            Assert.Equal("/api/metrics", request.Path); // a trailing slash on the base URL must not double up
            Assert.Equal("Bearer k-123", request.Authorization);
            Assert.StartsWith("application/json", request.ContentType);
            Assert.Equal("{\"application\":\"MyGame\",\"name\":\"startup\",\"tags\":{\"version\":\"1.2.3\"}}", request.Body);
            client.Close();
        }

        [Fact]
        public void Report_CarriesValueAndTagsWhenGiven()
        {
            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k");
            var tags = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("command", "home"),
                new KeyValuePair<string, string>("world", "the \"end\""),
            };

            client.Report("command", 2.5, tags);

            Assert.True(_server.WaitFor(1, TimeSpan.FromSeconds(5)));
            Assert.Equal(
                "{\"application\":\"MyGame\",\"name\":\"command\",\"value\":2.5,"
                + "\"tags\":{\"command\":\"home\",\"world\":\"the \\\"end\\\"\",\"version\":\"1.2.3\"}}",
                _server.Received.Single().Body);
            client.Close();
        }

        [Fact]
        public void UserAgent_NamesTheClientVersion()
        {
            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k");

            client.Report("startup");
            client.Close();

            Assert.Equal("trace-client/" + TraceClient.Version + " (MyGame)", _server.Received.Single().UserAgent);
        }

        [Fact]
        public void Report_ReturnsBeforeTheServerAnswers()
        {
            // A server that never replies. If Report() waited on the network the
            // caller -- a game's main thread, in the case that matters -- would wait with it.
            var release = new ManualResetEventSlim(false);
            using (var slow = new StubServer(beforeReply: () => release.Wait(TimeSpan.FromSeconds(10))))
            {
                var client = new TraceClient(slow.BaseUrl, "MyGame", "1.2.3", key: "k");

                var watch = Stopwatch.StartNew();
                client.Report("startup");
                watch.Stop();

                Assert.True(watch.ElapsedMilliseconds < 1000,
                    "Report() took " + watch.ElapsedMilliseconds + " ms; it must not wait on the network");
                release.Set();
                client.Close();
            }
        }

        [Fact]
        public void Report_DoesNotThrowWhenNothingIsListening()
        {
            int deadPort = StubServer.FreePort();
            var log = new ConcurrentQueue<string>();
            var client = new TraceClient("http://127.0.0.1:" + deadPort, "MyGame", "1.2.3", key: "k", log: log.Enqueue);

            client.Report("startup"); // must not throw
            client.Close();           // waits for the in-flight attempt to fail

            Assert.Contains(log, m => m.Contains("could not deliver"));
        }

        [Fact]
        public void Report_DoesNotThrowWhenTheServerRejectsTheKey()
        {
            using (var rejecting = new StubServer(status: () => 401))
            {
                var log = new ConcurrentQueue<string>();
                var client = new TraceClient(rejecting.BaseUrl, "MyGame", "1.2.3", key: "revoked", log: log.Enqueue);

                client.Report("startup");
                client.Close();

                Assert.Contains(log, m => m.Contains("answered 401"));
            }
        }

        [Fact]
        public void Report_TreatsAnyStatusButExactly201AsAFailure()
        {
            // The wire format promises 201 is success; another 2xx is not.
            using (var ok = new StubServer(status: () => 200))
            {
                var log = new ConcurrentQueue<string>();
                var client = new TraceClient(ok.BaseUrl, "MyGame", "1.2.3", key: "k", log: log.Enqueue);

                client.Report("startup");
                client.Close();

                Assert.Single(ok.Received);
                Assert.Contains(log, m => m.Contains("answered 200"));
            }
        }

        [Fact]
        public void Report_KeepsSendingAfterAFailedDelivery()
        {
            // One rejected report must not stop the sending thread: the first
            // answer is a 500, every later one a 201.
            int answered = 0;
            using (var flaky = new StubServer(status: () => Interlocked.Increment(ref answered) == 1 ? 500 : 201))
            {
                var log = new ConcurrentQueue<string>();
                var client = new TraceClient(flaky.BaseUrl, "MyGame", "1.2.3", key: "k", log: log.Enqueue);

                client.Report("first");
                client.Report("second");
                client.Report("third");
                client.Close();

                Assert.Equal(new[] { "first", "second", "third" },
                             flaky.Received.Select(r => r.Body.Split('"')[7]).ToArray());
                Assert.Single(log, m => m.Contains("answered 500"));
            }
        }

        [Fact]
        public void Constructor_TrimsTheBaseUrlApplicationAndKey()
        {
            var client = new TraceClient("  " + _server.BaseUrl + "/  ", " MyGame ", "1.2.3", key: " k-123 ");

            client.Report("startup");
            client.Close();

            Received request = _server.Received.Single();
            Assert.Equal("/api/metrics", request.Path);
            Assert.Equal("Bearer k-123", request.Authorization);
            Assert.Equal("trace-client/" + TraceClient.Version + " (MyGame)", request.UserAgent);
            Assert.StartsWith("{\"application\":\"MyGame\",", request.Body);
        }

        [Fact]
        public void Report_NeverThrowsEvenWhenTheLoggerDoes()
        {
            int deadPort = StubServer.FreePort();
            var client = new TraceClient("http://127.0.0.1:" + deadPort, "MyGame", "1.2.3", key: "k",
                                         log: m => throw new InvalidOperationException("bad logger"));

            client.Report("startup");
            client.Report(new string('x', 300)); // dropped locally, logged, logger throws
            client.Close();
        }

        [Fact]
        public void DisabledClient_SendsNothing()
        {
            var clients = new[]
            {
                new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k", enabled: false),
                new TraceClient(_server.BaseUrl, "MyGame", "1.2.3"),
                new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "  "),
                TraceClient.Disabled(),
            };

            foreach (TraceClient client in clients)
            {
                Assert.False(client.IsEnabled);
                client.Report("startup");
                client.Close();
            }

            Thread.Sleep(300);
            Assert.Empty(_server.Received);
        }

        [Fact]
        public void DisabledReason_IsNullWhenTheClientReports()
        {
            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k");

            Assert.True(client.IsEnabled);
            Assert.Null(client.DisabledReason);
            client.Close();
        }

        [Fact]
        public void DisabledReason_NamesTheConfigFlagOrTheMissingKey()
        {
            Assert.Equal("config", new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k", enabled: false).DisabledReason);
            Assert.Equal("config", new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", enabled: false).DisabledReason);
            Assert.Equal("no key", new TraceClient(_server.BaseUrl, "MyGame", "1.2.3").DisabledReason);
            Assert.Equal("config", TraceClient.Disabled().DisabledReason);
        }

        [Theory]
        [InlineData("off")]
        [InlineData("OFF")]
        [InlineData("false")]
        [InlineData("0")]
        [InlineData("no")]
        [InlineData(" No ")]
        public void TraceUsageReporting_DisablesReportingForEveryAcceptedValue(string value)
        {
            _environment[TraceClient.EnvUsageReporting] = value;

            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k");
            client.Report("startup");
            client.Close();

            Assert.False(client.IsEnabled);
            Assert.Equal("environment", client.DisabledReason);
            Thread.Sleep(100);
            Assert.Empty(_server.Received);
        }

        [Theory]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("TRUE")]
        [InlineData("yes")]
        public void DoNotTrack_DisablesReportingForEveryAcceptedValue(string value)
        {
            _environment[TraceClient.EnvDoNotTrack] = value;

            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k");

            Assert.False(client.IsEnabled);
            Assert.Equal("environment", client.DisabledReason);
        }

        [Theory]
        [InlineData("TRACE_USAGE_REPORTING", "on")]
        [InlineData("TRACE_USAGE_REPORTING", "")]
        [InlineData("TRACE_USAGE_REPORTING", "1")]
        [InlineData("DO_NOT_TRACK", "0")]
        [InlineData("DO_NOT_TRACK", "")]
        [InlineData("DO_NOT_TRACK", "false")]
        public void OtherEnvironmentValues_LeaveTheProgramSettingInCharge(string variable, string value)
        {
            _environment[variable] = value;

            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k");

            Assert.True(client.IsEnabled);
            client.Close();
        }

        [Fact]
        public void Environment_WinsOverTheConfigFlagAndOverTheKey()
        {
            _environment[TraceClient.EnvDoNotTrack] = "1";

            Assert.Equal("environment", new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", enabled: false).DisabledReason);
            Assert.Equal("environment", new TraceClient(_server.BaseUrl, "MyGame", "1.2.3").DisabledReason);
            Assert.True(TraceClient.EnvironmentDisables());
        }

        [Fact]
        public void Report_IgnoresABlankName()
        {
            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k");

            client.Report(null);
            client.Report("   ");
            client.Close();

            Assert.Empty(_server.Received);
        }

        [Fact]
        public void Constructor_RejectsAMissingBaseUrlOrApplication()
        {
            Assert.Throws<ArgumentException>(() => new TraceClient(null, "MyGame", "1.2.3"));
            Assert.Throws<ArgumentException>(() => new TraceClient(" ", "MyGame", "1.2.3"));
            Assert.Throws<ArgumentException>(() => new TraceClient("http://x", null, "1.2.3"));
            Assert.Throws<ArgumentException>(() => new TraceClient("http://x", "", "1.2.3"));
        }

        [Fact]
        public void Constructor_NamesTheParameterItRejects()
        {
            // A programming error should point at the argument to fix.
            Assert.Equal("baseUrl", Assert.Throws<ArgumentException>(() => new TraceClient(" ", "MyGame", "1.2.3")).ParamName);
            Assert.Equal("application", Assert.Throws<ArgumentException>(() => new TraceClient("http://x", " ", "1.2.3")).ParamName);
            Assert.Equal("version", Assert.Throws<ArgumentException>(() => new TraceClient("http://x", "MyGame", null)).ParamName);
            Assert.Equal("version", Assert.Throws<ArgumentException>(
                () => new TraceClient("http://x", "MyGame", new string('9', TraceClient.MaxLength + 1))).ParamName);
            Assert.Equal("installId", Assert.Throws<ArgumentException>(
                () => new TraceClient("http://x", "MyGame", "1.2.3", installId: new string('i', TraceClient.MaxLength + 1))).ParamName);
        }

        [Fact]
        public void Constructor_RejectsAMalformedBaseUrlWithAnArgumentException()
        {
            // Exactly ArgumentException, as documented -- not the UriFormatException
            // underneath, which a caller catching ArgumentException would miss.
            ArgumentException thrown = Assert.Throws<ArgumentException>(
                () => new TraceClient("not a url", "MyGame", "1.2.3", key: "k"));

            Assert.Equal("baseUrl", thrown.ParamName);
            Assert.IsType<UriFormatException>(thrown.InnerException);
        }

        [Fact]
        public void Constructor_RejectsAMissingOrOverlongVersion()
        {
            Assert.Throws<ArgumentException>(() => new TraceClient("http://x", "MyGame", null));
            Assert.Throws<ArgumentException>(() => new TraceClient("http://x", "MyGame", ""));
            Assert.Throws<ArgumentException>(() => new TraceClient("http://x", "MyGame", "  "));
            Assert.Throws<ArgumentException>(
                () => new TraceClient("http://x", "MyGame", new string('9', TraceClient.MaxLength + 1)));
            // Trimmed before measuring: surrounding whitespace does not count.
            new TraceClient("http://x", "MyGame", " " + new string('9', TraceClient.MaxLength) + " ").Close();
        }

        [Fact]
        public void Report_TagsACommandWithTheProgramVersionTrimmed()
        {
            var client = new TraceClient(_server.BaseUrl, "MyGame", " 2.0.0-SNAPSHOT ", key: "k");

            client.Report("command", tags: new Dictionary<string, string> { { "name", "home" } });

            Assert.True(_server.WaitFor(1, TimeSpan.FromSeconds(5)));
            Assert.Equal("{\"application\":\"MyGame\",\"name\":\"command\","
                         + "\"tags\":{\"name\":\"home\",\"version\":\"2.0.0-SNAPSHOT\"}}",
                         _server.Received.Single().Body);
            client.Close();
        }

        [Fact]
        public void Report_AnEventsOwnVersionTagWinsOverTheProgramVersion()
        {
            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k");
            var tags = new Dictionary<string, string> { { "version", "9.9.9" } };

            client.Report("startup", tags: tags);

            Assert.True(_server.WaitFor(1, TimeSpan.FromSeconds(5)));
            Assert.Equal("{\"application\":\"MyGame\",\"name\":\"startup\",\"tags\":{\"version\":\"9.9.9\"}}",
                         _server.Received.Single().Body);
            Assert.Equal(new Dictionary<string, string> { { "version", "9.9.9" } }, tags); // the caller's dictionary is not modified
            client.Close();
        }

        [Fact]
        public void Report_NeverModifiesTheCallersDictionary()
        {
            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k");
            var tags = new Dictionary<string, string> { { "name", "home" } };

            client.Report("command", tags: tags);
            client.Close();

            Assert.Equal(new Dictionary<string, string> { { "name", "home" } }, tags);
            Assert.Equal("{\"application\":\"MyGame\",\"name\":\"command\",\"tags\":{\"name\":\"home\",\"version\":\"1.2.3\"}}",
                         _server.Received.Single().Body);
        }

        [Fact]
        public void WithVersion_AddsTheVersionToACopyOnly()
        {
            var tags = new Dictionary<string, string> { { "name", "home" } };

            List<KeyValuePair<string, string>> merged = TraceClient.WithVersion(tags, "1.2.3");

            Assert.Equal(new Dictionary<string, string> { { "name", "home" } }, tags);
            Assert.Contains(new KeyValuePair<string, string>("version", "1.2.3"), merged);
            Assert.Equal(new[] { new KeyValuePair<string, string>("version", "1.2.3") },
                         TraceClient.WithVersion(null, "1.2.3"));
        }

        [Fact]
        public void Json_EscapesControlCharactersAndSkipsNullOrBlankTags()
        {
            var tags = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("ok", "line\nbreak\ttab\\slash"),
                new KeyValuePair<string, string>("nullValue", null),
                new KeyValuePair<string, string>(null, "nullKey"),
                new KeyValuePair<string, string>(" ", "blankKey"),
            };

            Assert.Null(TraceClient.Json("App", "n", double.NaN, tags, out string json));

            // NaN is not JSON and is dropped; null or blank keys and null values are skipped.
            Assert.Equal("{\"application\":\"App\",\"name\":\"n\",\"tags\":{\"ok\":\"line\\nbreak\\ttab\\\\slash\"}}", json);
            Assert.Equal("\"\\u0001\"", TraceClient.Quote("\u0001"));
        }

        [Fact]
        public void Json_WritesValuesTheSameInEveryCulture()
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                TraceClient.Json("App", "n", 2.5, null, out string json);
                Assert.Equal("{\"application\":\"App\",\"name\":\"n\",\"value\":2.5}", json);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        }

        [Theory]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void Json_OmitsAnInfiniteValueLikeNaN(double value)
        {
            // Infinity is not JSON either; the report still goes, without a value.
            Assert.Null(TraceClient.Json("App", "n", value, null, out string json));

            Assert.Equal("{\"application\":\"App\",\"name\":\"n\"}", json);
        }

        [Fact]
        public void Json_WritesWholeAndNegativeValuesInTheirShortestForm()
        {
            TraceClient.Json("App", "n", 42.0, null, out string whole);
            TraceClient.Json("App", "n", -0.5, null, out string negative);

            Assert.Equal("{\"application\":\"App\",\"name\":\"n\",\"value\":42}", whole);
            Assert.Equal("{\"application\":\"App\",\"name\":\"n\",\"value\":-0.5}", negative);
        }

        [Fact]
        public void Json_OmitsTheTagsObjectWhenNoTagSurvives()
        {
            var onlyBadTags = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("nullValue", null),
                new KeyValuePair<string, string>("", "emptyKey"),
            };

            TraceClient.Json("App", "n", null, new Dictionary<string, string>(), out string empty);
            TraceClient.Json("App", "n", null, onlyBadTags, out string skipped);

            Assert.Equal("{\"application\":\"App\",\"name\":\"n\"}", empty);
            Assert.Equal("{\"application\":\"App\",\"name\":\"n\"}", skipped);
        }

        [Fact]
        public void Json_RejectsAnApplicationLongerThanTheLimitButNotOneAtIt()
        {
            string longest = new string('a', TraceClient.MaxLength);
            string tooLong = new string('a', TraceClient.MaxLength + 1);

            string problem = TraceClient.Json(tooLong, "n", null, null, out string rejected);

            Assert.Equal("application longer than " + TraceClient.MaxLength + " characters", problem);
            Assert.Null(rejected);
            Assert.Null(TraceClient.Json(longest, "n", null, null, out string accepted));
            Assert.Contains(longest, accepted);
        }

        [Fact]
        public void Json_RejectsATagKeyLongerThanTheLimitAndNamesOnlyItsStart()
        {
            // The key is cut to 32 characters in the reason, so a runaway key
            // cannot turn one log line into 255 characters of it.
            string tooLongKey = new string('k', TraceClient.MaxLength + 1);

            string problem = TraceClient.Json("App", "n", null,
                new Dictionary<string, string> { { tooLongKey, "v" } }, out string body);

            Assert.Equal("tag " + new string('k', 32) + " longer than " + TraceClient.MaxLength + " characters", problem);
            Assert.Null(body);
        }

        [Fact]
        public void Json_RejectsANameLongerThanTheLimitButNotOneAtIt()
        {
            string longest = new string('n', TraceClient.MaxLength);
            string tooLong = new string('n', TraceClient.MaxLength + 1);

            string problem = TraceClient.Json("App", tooLong, null, null, out string rejected);

            Assert.Equal("name longer than " + TraceClient.MaxLength + " characters", problem);
            Assert.Null(rejected);
            Assert.Null(TraceClient.Json("App", longest, null, null, out string accepted));
            Assert.Contains(longest, accepted);
        }

        [Fact]
        public void Json_RejectsATagValueLongerThanTheLimitAndNamesItsKey()
        {
            // The reason names the key, not the value: the value is what ran
            // away, and the key is what the program can find in its source.
            string tooLongValue = new string('v', TraceClient.MaxLength + 1);
            string longestValue = new string('v', TraceClient.MaxLength);

            string problem = TraceClient.Json("App", "n", null,
                new Dictionary<string, string> { { "level", tooLongValue } }, out string body);

            Assert.Equal("tag level longer than " + TraceClient.MaxLength + " characters", problem);
            Assert.Null(body);
            Assert.Null(TraceClient.Json("App", "n", null,
                new Dictionary<string, string> { { "level", longestValue } }, out string accepted));
            Assert.Contains("\"level\":\"" + longestValue + "\"", accepted);
        }

        [Fact]
        public void Quote_EscapesCarriageReturnAndEveryOtherControlCharacter()
        {
            Assert.Equal("\"a\\rb\"", TraceClient.Quote("a\rb"));
            Assert.Equal("\"\\u0000\\u001f\"", TraceClient.Quote("\u0000\u001f"));
            // Space and everything above it, non-ASCII included, is written as is.
            Assert.Equal("\" \u00e9/\u00fc\"", TraceClient.Quote(" \u00e9/\u00fc"));
        }

        [Fact]
        public void EnvironmentDisables_IsFalseWhenTheEnvironmentCannotBeRead()
        {
            // A sandbox that forbids reading the environment must not stop the
            // program from starting, nor silently decide for it: its own setting rules.
            TraceClient.EnvironmentSource = name => throw new System.Security.SecurityException("sandboxed");

            Assert.False(TraceClient.EnvironmentDisables());
            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k");
            Assert.True(client.IsEnabled);
            Assert.Null(client.DisabledReason);
            client.Close();
        }

        [Fact]
        public void Report_DropsWhatTheServerWouldRejectForItsSize()
        {
            var log = new ConcurrentQueue<string>();
            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k", log: log.Enqueue);
            var tooMany = Enumerable.Range(0, TraceClient.MaxTags + 1)
                .Select(i => new KeyValuePair<string, string>("t" + i, "v")).ToList();
            // The program version is one of the tags, so the event itself may carry one fewer.
            var exactlyTheLimit = tooMany.Take(TraceClient.MaxTags - 1).ToList();
            string longest = new string('v', TraceClient.MaxLength);
            string tooLong = new string('v', TraceClient.MaxLength + 1);

            client.Report("too-many-tags", tags: tooMany);
            client.Report("too-many-with-version", tags: tooMany.Take(TraceClient.MaxTags).ToList());
            client.Report("tag-value-too-long", tags: new Dictionary<string, string> { { "k", tooLong } });
            client.Report(tooLong);
            client.Report("at-the-limit", tags: exactlyTheLimit);
            client.Report("longest", tags: new Dictionary<string, string> { { "k", longest } });
            client.Close();

            Assert.Equal(new[] { "at-the-limit", "longest" },
                         _server.Received.Select(r => r.Body.Split('"')[7]).OrderBy(n => n).ToArray());
            Assert.Equal(4, log.Count(m => m.Contains("dropped")));
        }

        [Fact]
        public void Queue_IsBoundedAndDropsRatherThanGrows()
        {
            // Hold the sending thread on the first report so everything behind it
            // queues, overfill the queue, then let the server drain and count.
            var release = new ManualResetEventSlim(false);
            using (var slow = new StubServer(beforeReply: () => release.Wait(TimeSpan.FromSeconds(10))))
            {
                var client = new TraceClient(slow.BaseUrl, "MyGame", "1.2.3", key: "k");
                int flood = TraceClient.QueueCapacity * 3;

                for (int i = 0; i < flood; i++)
                {
                    client.Report("flood");
                }
                release.Set();
                int seen = -1;
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(200);
                    int now = Volatile.Read(ref slow.Delivered);
                    if (now == seen)
                    {
                        break; // nothing arrived in the last 200 ms: the queue is drained
                    }
                    seen = now;
                }

                int delivered = Volatile.Read(ref slow.Delivered);
                Assert.True(delivered >= 1, "the first report was in flight and must land");
                Assert.True(delivered <= TraceClient.QueueCapacity + 1,
                    "delivered " + delivered + " of " + flood + "; at most the in-flight one plus a full queue may survive");
                client.Close();
            }
        }

        [Fact]
        public void Close_SendsWhatWasJustQueuedBeforeStopping()
        {
            // A CLI reports once and exits at once. Without draining, the event
            // races the sender thread and is lost a good fraction of the time; 30
            // back-to-back Report()+Close() pairs make that fraction visible.
            for (int i = 0; i < 30; i++)
            {
                var client = new TraceClient(_server.BaseUrl, "MyCli", "1.2.3", key: "k");
                client.Report("startup", tags: new Dictionary<string, string> { { "run", i.ToString() } });
                client.Close();
            }

            Assert.Equal(30, _server.Received.Count);
        }

        [Fact]
        public void Dispose_DrainsLikeClose()
        {
            using (var client = new TraceClient(_server.BaseUrl, "MyCli", "1.2.3", key: "k"))
            {
                client.Report("startup");
            }

            Assert.Single(_server.Received);
        }

        [Fact]
        public void Close_StillReturnsWithinTheTimeoutWhenTheServerHangs()
        {
            var release = new ManualResetEventSlim(false);
            using (var slow = new StubServer(beforeReply: () => release.Wait(TimeSpan.FromSeconds(15))))
            {
                var client = new TraceClient(slow.BaseUrl, "MyCli", "1.2.3", key: "k");
                client.Report("startup");
                client.Report("second");

                var watch = Stopwatch.StartNew();
                client.Close();
                watch.Stop();

                Assert.True(watch.ElapsedMilliseconds < 7000,
                    "Close() took " + watch.ElapsedMilliseconds + " ms; draining must be bounded by the timeout");
                release.Set();
            }
        }

        [Fact]
        public void Close_IsIdempotentAndReportAfterCloseIsANoOp()
        {
            var client = new TraceClient(_server.BaseUrl, "MyCli", "1.2.3", key: "k");

            client.Close();
            client.Close();
            client.Report("after-close");
            TraceClient.Disabled().Close();

            Assert.False(client.IsEnabled);
            Thread.Sleep(100);
            Assert.Empty(_server.Received);
        }
    }
}
