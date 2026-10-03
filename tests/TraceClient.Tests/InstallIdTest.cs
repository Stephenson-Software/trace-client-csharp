using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace StephensonSoftware.Trace.Tests
{
    /// <summary>
    /// The per-installation ID sent as the tag <c>install</c>: the same cases
    /// the Java client's suite covers, plus the file helper.
    /// </summary>
    public sealed class InstallIdTest : IDisposable
    {
        private readonly Dictionary<string, string> _environment = new Dictionary<string, string>();
        private readonly Func<string, string> _realEnvironment;
        private readonly StubServer _server = new StubServer();
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "trace-install-" + Guid.NewGuid().ToString("N"));

        public InstallIdTest()
        {
            _realEnvironment = TraceClient.EnvironmentSource;
            TraceClient.EnvironmentSource = name => _environment.TryGetValue(name, out string v) ? v : null;
        }

        public void Dispose()
        {
            TraceClient.EnvironmentSource = _realEnvironment;
            _server.Dispose();
            try
            {
                Directory.Delete(_directory, true);
            }
            catch (Exception)
            {
                // nothing was made
            }
        }

        private static bool IsGuid(string value)
        {
            return Guid.TryParseExact(value, "D", out Guid _);
        }

        [Fact]
        public void InstallIdFromFile_WritesOneIdOnceAndReusesIt()
        {
            string path = Path.Combine(_directory, "nested", "dirs", "install-id");

            string first = TraceClient.InstallIdFromFile(path);
            string second = TraceClient.InstallIdFromFile(path);

            Assert.True(IsGuid(first), first);
            Assert.Equal(first, second);
            Assert.Equal(first + "\n", File.ReadAllText(path)); // parent directories were created
        }

        [Fact]
        public void InstallIdFromFile_ReadsTheFirstValidLineAndReplacesAFileWithoutOne()
        {
            Directory.CreateDirectory(_directory);
            string kept = Path.Combine(_directory, "kept");
            File.WriteAllText(kept, "\n  not valid!  \n  my-own_id.1  \nsecond\n");
            string junk = Path.Combine(_directory, "junk");
            File.WriteAllText(junk, "has spaces\n" + new string('a', TraceClient.MaxLength + 1) + "\n");

            Assert.Equal("my-own_id.1", TraceClient.InstallIdFromFile(kept));
            string fresh = TraceClient.InstallIdFromFile(junk);
            Assert.True(IsGuid(fresh), fresh);
            Assert.Equal(fresh, TraceClient.InstallIdFromFile(junk));
        }

        [Fact]
        public void InstallIdFromFile_FallsBackToAnInMemoryIdWithoutThrowing()
        {
            // A path under a regular file can be neither read nor created, even as root.
            Directory.CreateDirectory(_directory);
            string blocker = Path.Combine(_directory, "a-file");
            File.WriteAllText(blocker, "x");
            string unwritable = Path.Combine(blocker, "install-id");

            string first = TraceClient.InstallIdFromFile(unwritable);
            string second = TraceClient.InstallIdFromFile(unwritable);

            Assert.True(IsGuid(first), first);
            Assert.NotEqual(first, second); // nothing persisted
            Assert.Equal("x", File.ReadAllText(blocker));
            Assert.True(IsGuid(TraceClient.InstallIdFromFile(null)));
            Assert.True(IsGuid(TraceClient.InstallIdFromFile("  ")));
        }

        [Fact]
        public void InstallIdFromFile_DoesNotOverwriteAPathItCannotRead()
        {
            // A directory where the file should be: unreadable as a file, and left alone.
            string directoryInTheWay = Path.Combine(_directory, "install-id");
            Directory.CreateDirectory(directoryInTheWay);

            string id = TraceClient.InstallIdFromFile(directoryInTheWay);

            Assert.True(IsGuid(id), id);
            Assert.True(Directory.Exists(directoryInTheWay));
        }

        [Fact]
        public void Client_PersistsItsIdInTheGivenFileAndSendsItOnEveryEvent()
        {
            string path = Path.Combine(_directory, "install-id");
            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k", installIdFile: path);
            string id = client.InstallId;

            client.Report("startup");
            client.Report("command", tags: new Dictionary<string, string> { { "name", "home" } });
            client.Close();
            var again = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k", installIdFile: path);
            again.Close();

            Assert.True(IsGuid(id), id);
            Assert.Equal(id, again.InstallId);
            Assert.Equal(id + "\n", File.ReadAllText(path));
            Assert.Equal(new[]
                {
                    "{\"application\":\"MyGame\",\"name\":\"startup\",\"tags\":{\"version\":\"1.2.3\",\"install\":\"" + id + "\"}}",
                    "{\"application\":\"MyGame\",\"name\":\"command\",\"tags\":{\"name\":\"home\",\"version\":\"1.2.3\",\"install\":\"" + id + "\"}}",
                },
                _server.Received.Select(r => r.Body).ToArray());
        }

        [Fact]
        public void Client_WithAnUnwritableFileStillReportsWithAnInMemoryId()
        {
            Directory.CreateDirectory(_directory);
            string blocker = Path.Combine(_directory, "a-file");
            File.WriteAllText(blocker, "x");
            var log = new List<string>();

            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k", log: log.Add,
                                         installIdFile: Path.Combine(blocker, "install-id"));
            client.Report("startup");
            client.Close();

            Assert.True(IsGuid(client.InstallId), client.InstallId);
            Assert.Contains("\"install\":\"" + client.InstallId + "\"", _server.Received.Single().Body);
            Assert.Contains(log, m => m.Contains("in-memory install ID"));
        }

        [Fact]
        public void DisabledClient_NeverMakesUpOrWritesAnId()
        {
            string path = Path.Combine(_directory, "install-id");
            var clients = new List<TraceClient>
            {
                new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k", enabled: false, installIdFile: path, installId: null),
                new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", installIdFile: path),
                new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k", enabled: false, installId: "explicit"),
            };
            _environment["DO_NOT_TRACK"] = "1";
            clients.Add(new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k", installIdFile: path));
            _environment.Clear();
            _environment["TRACE_USAGE_REPORTING"] = "off";
            clients.Add(new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k", installIdFile: path));

            foreach (TraceClient client in clients)
            {
                Assert.False(client.IsEnabled);
                Assert.Null(client.InstallId);
                client.Report("startup");
                client.Close();
            }

            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(_directory));
            Thread.Sleep(200);
            Assert.Empty(_server.Received);
        }

        [Fact]
        public void ExplicitId_IsTrimmedWinsOverTheFileAndWritesNothing()
        {
            string path = Path.Combine(_directory, "install-id");
            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k",
                                         installId: "  my-install  ", installIdFile: path);

            client.Report("startup");
            client.Close();

            Assert.Equal("my-install", client.InstallId);
            Assert.False(File.Exists(path));
            Assert.Equal("{\"application\":\"MyGame\",\"name\":\"startup\",\"tags\":{\"version\":\"1.2.3\",\"install\":\"my-install\"}}",
                         _server.Received.Single().Body);
        }

        [Fact]
        public void NoIdOrABlankOne_SendsNoInstallTag()
        {
            var none = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k");
            var blank = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k", installId: "   ", installIdFile: " ");

            none.Report("startup");
            blank.Report("startup");
            none.Close();
            blank.Close();

            Assert.Null(none.InstallId);
            Assert.Null(blank.InstallId);
            Assert.All(_server.Received, r => Assert.DoesNotContain("install", r.Body));
            Assert.Equal(2, _server.Received.Count);
        }

        [Fact]
        public void ExplicitId_LongerThanTheLimitIsRejectedLikeAnOverlongVersion()
        {
            ArgumentException thrown = Assert.Throws<ArgumentException>(
                () => new TraceClient("http://x", "MyGame", "1.2.3", key: "k",
                                      installId: new string('i', TraceClient.MaxLength + 1)));
            Assert.Equal("installId", thrown.ParamName);
            // Rejected even for a disabled client: it is a programming error, not a runtime one.
            Assert.Throws<ArgumentException>(
                () => new TraceClient("http://x", "MyGame", "1.2.3", enabled: false,
                                      installId: new string('i', TraceClient.MaxLength + 1)));
            var atTheLimit = new TraceClient("http://x", "MyGame", "1.2.3", key: "k",
                                             installId: " " + new string('i', TraceClient.MaxLength) + " ");
            Assert.Equal(TraceClient.MaxLength, atTheLimit.InstallId.Length);
            atTheLimit.Close();
        }

        [Fact]
        public void Report_AnEventsOwnInstallTagWins()
        {
            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k", installId: "configured");

            client.Report("startup", tags: new Dictionary<string, string> { { "install", "own" } });
            client.Close();

            Assert.Equal("{\"application\":\"MyGame\",\"name\":\"startup\",\"tags\":{\"install\":\"own\",\"version\":\"1.2.3\"}}",
                         _server.Received.Single().Body);
        }

        [Fact]
        public void Report_NeverAddsTheInstallTagPastTheTagLimit()
        {
            var client = new TraceClient(_server.BaseUrl, "MyGame", "1.2.3", key: "k", installId: "configured");
            // With version, 31 own tags fill the limit: install is left off rather than the event dropped.
            var full = Enumerable.Range(0, TraceClient.MaxTags - 1)
                .Select(i => new KeyValuePair<string, string>("t" + i, "v")).ToList();
            var roomForOne = full.Take(TraceClient.MaxTags - 2).ToList();

            client.Report("full", tags: full);
            client.Report("room", tags: roomForOne);
            client.Close();

            Dictionary<string, string> bodies = _server.Received.ToDictionary(r => r.Body.Split('"')[7], r => r.Body);
            Assert.DoesNotContain("install", bodies["full"]);
            Assert.Contains("\"install\":\"configured\"", bodies["room"]);
        }

        [Fact]
        public void WithInstall_CountsOnlyTagsThatWouldBeSent()
        {
            var tags = Enumerable.Range(0, TraceClient.MaxTags - 1)
                .Select(i => new KeyValuePair<string, string>("t" + i, "v")).ToList();
            tags.Add(new KeyValuePair<string, string>(" ", "skipped by Json"));

            List<KeyValuePair<string, string>> merged = TraceClient.WithInstall(tags, "id");

            Assert.Contains(new KeyValuePair<string, string>("install", "id"), merged);
            Assert.Same(tags, TraceClient.WithInstall(tags, null));
        }
    }
}
