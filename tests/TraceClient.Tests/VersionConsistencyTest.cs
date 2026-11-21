using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace StephensonSoftware.Trace.Tests
{
    /// <summary>
    /// Every place the client's version is written agrees with the build file,
    /// TraceClient.csproj's &lt;Version&gt;, and each appears exactly once.
    /// </summary>
    public sealed class VersionConsistencyTest
    {
        private static string Read(params string[] parts)
        {
            return File.ReadAllText(SourceTree.PathTo(parts));
        }

        private static string BuildVersion()
        {
            MatchCollection found = Regex.Matches(Read("src", "TraceClient", "TraceClient.csproj"), "<Version>([^<]+)</Version>");
            return Assert.Single(found.Cast<Match>()).Groups[1].Value;
        }

        private static void AppearsOnce(string text, string expected, string where)
        {
            int count = Regex.Matches(text, Regex.Escape(expected)).Count;
            Assert.True(count == 1, "expected \"" + expected + "\" exactly once in " + where + ", found " + count);
        }

        [Fact]
        public void EveryVersionSpotMatchesTheCsproj()
        {
            string version = BuildVersion();
            string source = Read("src", "TraceClient", "TraceClient.cs");

            AppearsOnce(source, "trace-client " + version + " --", "TraceClient.cs header");
            AppearsOnce(source, "const string Version = \"" + version + "\"", "TraceClient.cs");
            AppearsOnce(Read("README.md"), "trace-client/" + version + " (", "README.md");
            Assert.Equal(TraceClient.Version, version);
        }

        [Fact]
        public void NoSpotNamesADifferentVersion()
        {
            string version = BuildVersion();
            string source = Read("src", "TraceClient", "TraceClient.cs");

            foreach (Match m in Regex.Matches(source, "trace-client (\\S+) --"))
            {
                Assert.Equal(version, m.Groups[1].Value);
            }
            foreach (Match m in Regex.Matches(source, "const string Version = \"([^\"]*)\""))
            {
                Assert.Equal(version, m.Groups[1].Value);
            }
            foreach (Match m in Regex.Matches(Read("README.md"), "trace-client/(\\S+) \\("))
            {
                Assert.Equal(version, m.Groups[1].Value);
            }
        }
    }
}
