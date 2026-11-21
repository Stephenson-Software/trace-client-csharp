using System;
using System.IO;

namespace StephensonSoftware.Trace.Tests
{
    /// <summary>
    /// Files the suite reads from the repository itself, found by walking up from
    /// the test assembly's directory (bin/Release/&lt;framework&gt; on every OS) to the
    /// directory that holds TraceClient.sln.
    /// </summary>
    internal static class SourceTree
    {
        public static string Root
        {
            get
            {
                for (DirectoryInfo dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "TraceClient.sln")))
                    {
                        return dir.FullName;
                    }
                }
                throw new InvalidOperationException("TraceClient.sln not found above " + AppDomain.CurrentDomain.BaseDirectory);
            }
        }

        public static string PathTo(params string[] parts)
        {
            string path = Root;
            foreach (string part in parts)
            {
                path = Path.Combine(path, part);
            }
            return path;
        }
    }
}
