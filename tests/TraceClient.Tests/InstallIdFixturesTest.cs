using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace StephensonSoftware.Trace.Tests
{
    /// <summary>
    /// Install-ID compatibility: the released loader's behaviour on every file in
    /// tests/install-id-fixtures, as recorded in that directory's expected.txt.
    /// An ID a previous release stored must keep loading as the same ID, and a file
    /// the loader cannot read must never be overwritten. Released fixtures and their
    /// loaded/kept lines are frozen; a new case is a new fixture plus its line.
    /// </summary>
    public sealed class InstallIdFixturesTest : IDisposable
    {
        private static readonly Regex Uuid4 = new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");
        private static readonly string FixtureDirectory = SourceTree.PathTo("tests", "install-id-fixtures");

        private readonly string _scratch = Path.Combine(Path.GetTempPath(), "trace-install-fixtures-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_scratch, true);
            }
            catch (Exception)
            {
                // nothing was made
            }
        }

        private static List<string[]> ExpectedLines()
        {
            var lines = new List<string[]>();
            foreach (string raw in File.ReadAllLines(Path.Combine(FixtureDirectory, "expected.txt")))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                lines.Add(line.Split(new[] { ' ' }, 3));
            }
            return lines;
        }

        public static IEnumerable<object[]> Cases()
        {
            return ExpectedLines().Select(parts => new object[] { parts[0], parts[1], parts.Length > 2 ? parts[2] : null });
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j])
                {
                    j++;
                }
                if (j == needle.Length)
                {
                    return true;
                }
            }
            return false;
        }

        [Fact]
        public void ExpectedTxt_NamesExactlyTheFixturesInTheDirectory()
        {
            var named = ExpectedLines().Select(parts => parts[0]).ToList();
            var present = Directory.GetFiles(FixtureDirectory, "*.id").Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal).ToList();

            Assert.Equal(named.Count, named.Distinct().Count());
            Assert.Equal(present, named.OrderBy(name => name, StringComparer.Ordinal).ToList());
            Assert.All(ExpectedLines(), parts =>
                Assert.True((parts[1] == "loaded" && parts.Length == 3) || ((parts[1] == "replaced" || parts[1] == "kept") && parts.Length == 2),
                            string.Join(" ", parts)));
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void InstallIdFromFile_DoesWhatTheReleasedLoaderDid(string fixture, string outcome, string id)
        {
            string directory = Path.Combine(_scratch, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fixture);
            File.Copy(Path.Combine(FixtureDirectory, fixture), path);
            byte[] before = File.ReadAllBytes(path);

            string returned = TraceClient.InstallIdFromFile(path);
            byte[] after = File.ReadAllBytes(path);

            switch (outcome)
            {
                case "loaded":
                    Assert.Equal(id, returned);
                    Assert.Equal(before, after);
                    break;
                case "replaced":
                    Assert.Matches(Uuid4, returned);
                    Assert.Equal(Encoding.ASCII.GetBytes(returned + "\n"), after);
                    break;
                case "kept":
                    Assert.Equal(before, after);
                    Assert.Matches(Uuid4, returned);
                    Assert.False(Contains(before, Encoding.ASCII.GetBytes(returned)), returned + " is in " + fixture);
                    break;
                default:
                    throw new InvalidOperationException("unknown outcome in expected.txt: " + outcome);
            }
        }
    }
}
