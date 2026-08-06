using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;

namespace ServiceKit.Net.Communicators.Tests
{
    /// <summary>
    /// An SMTP server small enough to read, so the e-mail tests need nothing installed.
    ///
    /// It keeps the ENVELOPE as well as the message. That is not detail: a blind copy is a recipient
    /// the envelope names and the headers do not, so the only way to check that Bcc works is to have
    /// both and compare them.
    /// </summary>
    public sealed class TestSmtpServer : IDisposable
    {
        public sealed class Received
        {
            /// Who the server was told to deliver to - RCPT TO, including the blind copies.
            public List<string> EnvelopeRecipients = new List<string>();
            public string EnvelopeSender;
            /// The message itself, parsed.
            public MimeMessage Message;
        }

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
        private readonly List<Received> _received = new List<Received>();
        private readonly object _lock = new object();
        private readonly Task _loop;

        public int Port { get; }

        public IReadOnlyList<Received> Messages
        {
            get
            {
                lock (_lock)
                    return _received.ToList();
            }
        }

        public TestSmtpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(_AcceptLoop);
        }

        private async Task _AcceptLoop()
        {
            while (_stopping.IsCancellationRequested == false)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => _Serve(client));
            }
        }

        private async Task _Serve(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" })
            {
                await writer.WriteLineAsync("220 test-smtp ready");

                var received = new Received();

                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null)
                        return;

                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        // multiline: every line but the last carries a dash
                        await writer.WriteLineAsync("250-test-smtp");
                        await writer.WriteLineAsync("250 8BITMIME");
                    }
                    else if (line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        await writer.WriteLineAsync("250 test-smtp");
                    }
                    else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        received.EnvelopeSender = _Address(line);
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        received.EnvelopeRecipients.Add(_Address(line));
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        await writer.WriteLineAsync("354 send it");

                        var body = new StringBuilder();
                        while (true)
                        {
                            var dataLine = await reader.ReadLineAsync();
                            if (dataLine == null || dataLine == ".")
                                break;

                            // transparency: a line that really starts with a dot was doubled
                            if (dataLine.StartsWith("..") == true)
                                dataLine = dataLine.Substring(1);

                            body.AppendLine(dataLine);
                        }

                        using (var parsed = new MemoryStream(Encoding.UTF8.GetBytes(body.ToString())))
                            received.Message = MimeMessage.Load(parsed);

                        lock (_lock)
                            _received.Add(received);

                        received = new Received();
                        await writer.WriteLineAsync("250 queued");
                    }
                    else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        await writer.WriteLineAsync("221 bye");
                        return;
                    }
                    else
                    {
                        await writer.WriteLineAsync("250 OK");
                    }
                }
            }
        }

        private static string _Address(string line)
        {
            var open = line.IndexOf('<');
            var close = line.IndexOf('>');
            if (open < 0 || close < open)
                return string.Empty;

            return line.Substring(open + 1, close - open - 1);
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            _stopping.Dispose();
        }
    }
}
