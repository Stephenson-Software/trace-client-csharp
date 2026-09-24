using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StephensonSoftware.Trace.Tests
{
    /// <summary>What the stub saw of one request.</summary>
    internal sealed class Received
    {
        public string Method;
        public string Path;
        public string Authorization;
        public string ContentType;
        public string UserAgent;
        public string Body;
    }

    /// <summary>
    /// A trace server stand-in on a loopback port, built on the framework's own
    /// HttpListener, so the tests have no more dependencies than the client does.
    /// </summary>
    internal sealed class StubServer : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly Func<int> _status;
        private readonly Action _beforeReply;

        public readonly ConcurrentQueue<Received> Received = new ConcurrentQueue<Received>();
        public int Delivered;

        public string BaseUrl { get; }

        /// <param name="status">What to answer with; 201 unless given.</param>
        /// <param name="beforeReply">Runs before the answer is written -- block in it to simulate a hanging server.</param>
        public StubServer(Func<int> status = null, Action beforeReply = null)
        {
            _status = status ?? (() => 201);
            _beforeReply = beforeReply;
            int port = FreePort();
            BaseUrl = "http://127.0.0.1:" + port;
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            new Thread(Accept) { IsBackground = true }.Start();
        }

        public static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>Waits until at least <paramref name="count"/> requests have been fully received.</summary>
        public bool WaitFor(int count, TimeSpan within)
        {
            DateTime deadline = DateTime.UtcNow + within;
            while (DateTime.UtcNow < deadline)
            {
                if (Received.Count >= count)
                {
                    return true;
                }
                Thread.Sleep(10);
            }
            return Received.Count >= count;
        }

        private void Accept()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception)
                {
                    return; // stopped
                }
                Task.Run(() => Handle(context));
            }
        }

        private void Handle(HttpListenerContext context)
        {
            try
            {
                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }
                _beforeReply?.Invoke();
                Received.Enqueue(new Received
                {
                    Method = context.Request.HttpMethod,
                    Path = context.Request.Url.AbsolutePath,
                    Authorization = context.Request.Headers["Authorization"],
                    ContentType = context.Request.ContentType,
                    UserAgent = context.Request.UserAgent,
                    Body = body,
                });
                Interlocked.Increment(ref Delivered);
                context.Response.StatusCode = _status();
                context.Response.Close();
            }
            catch (Exception)
            {
                // the client gave up on this request; nothing to assert
            }
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception)
            {
                // already stopped
            }
        }
    }
}
