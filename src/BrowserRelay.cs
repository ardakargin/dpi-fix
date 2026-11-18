using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class BrowserRelay : IDisposable
{
    const int MaxConnections = 32;
    const int HandshakeTimeout = 10000;
    const int MaxHeaders = 8192;
    const int MaxTlsRecord = 16384;
    const int MaxClientHello = 2 * MaxTlsRecord;
    const int MaxDnsResponse = 32768;
    const string ResolverHost = "cloudflare-dns.com";

    readonly TcpListener listener;
    readonly object gate = new object();
    readonly HashSet<Connection> connections = new HashSet<Connection>();
    readonly HashSet<string> domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim dnsSlots = new SemaphoreSlim(4);
    readonly Dictionary<string, DnsCacheEntry> dnsCache = new Dictionary<string, DnsCacheEntry>();
    readonly string authority;
    readonly string pacPath;
    readonly string pacContent;
    bool disposed;

    public BrowserRelay(string[] domains)
    {
        if (domains == null) throw new ArgumentNullException("domains");
        foreach (string domain in domains)
        {
            if (!IsDnsName(domain)) throw new ArgumentException("Expected ASCII DNS names.", "domains");
            this.domains.Add(domain.ToLowerInvariant());
        }

        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(MaxConnections);
        authority = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        pacPath = "/" + Guid.NewGuid().ToString("N") + ".pac";
        pacContent = "function FindProxyForURL(url, host) {\n" +
            "  if (!/^(https|wss):/i.test(url)) return 'DIRECT';\n" +
            "  host = host.toLowerCase();\n" +
            "  var domains = [" + (this.domains.Count == 0 ? "" : "'" + string.Join("','", this.domains) + "'") + "];\n" +
            "  for (var i = 0; i < domains.length; i++) {\n" +
            "    var domain = domains[i];\n" +
            "    if (host === domain || host.slice(-domain.length - 1) === '.' + domain)\n" +
            "      return 'PROXY " + authority + "';\n" +
            "  }\n" +
            "  return 'DIRECT';\n" +
            "}\n";

        new Thread(AcceptConnections) { IsBackground = true, Name = "DpiFix browser relay" }.Start();
    }

    public string PacUrl { get { return "http://" + authority + pacPath; } }

    void AcceptConnections()
    {
        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (SocketException) { return; }
            catch (InvalidOperationException) { return; }

            lock (gate)
            {
                if (disposed || connections.Count >= MaxConnections)
                {
                    client.Close();
                    continue;
                }

                var connection = new Connection(client);
                connections.Add(connection);
                ThreadPool.QueueUserWorkItem(_ => Serve(connection));
            }
        }
    }

    async void Serve(Connection connection)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            using (var timer = new Timer(_ => connection.Dispose(), null, HandshakeTimeout, Timeout.Infinite))
            {
                NetworkStream browser = connection.Browser.GetStream();
                var input = new BufferedStream(browser, 4096);
                string[] headers = ReadHeaders(input);
                string[] request = headers[0].Split(' ');
                if (request.Length != 3 || (request[2] != "HTTP/1.1" && request[2] != "HTTP/1.0"))
                {
                    WriteResponse(browser, "400 Bad Request");
                    return;
                }

                if (request[0] == "GET" && request[1] == pacPath &&
                    string.Equals(Header(headers, "Host"), authority, StringComparison.Ordinal))
                {
                    WriteResponse(browser, "200 OK", pacContent);
                    return;
                }

                if (request[0] != "CONNECT")
                {
                    WriteResponse(browser, "404 Not Found");
                    return;
                }

                string endpoint = request[1];
                if (!endpoint.EndsWith(":443", StringComparison.Ordinal) ||
                    !IsAllowed(endpoint.Substring(0, endpoint.Length - 4)))
                {
                    WriteResponse(browser, "403 Forbidden");
                    return;
                }

                string host = endpoint.Substring(0, endpoint.Length - 4).ToLowerInvariant();
                WriteAscii(browser, "HTTP/1.1 200 Connection Established\r\n\r\n");
                byte[] hello = ReadClientHello(input, host);
                TcpClient target = ConnectTarget(connection, Resolve(connection, host, elapsed), elapsed);
                NetworkStream upstream = target.GetStream();
                upstream.Write(hello, 0, hello.Length);
                timer.Change(Timeout.Infinite, Timeout.Infinite);

                await Task.WhenAll(
                    Pipe(input, target, connection),
                    Pipe(upstream, connection.Browser, connection)).ConfigureAwait(false);
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (AuthenticationException) { }
        catch (InvalidOperationException) { }
        catch (SerializationException) { }
        finally
        {
            connection.Dispose();
            lock (gate) connections.Remove(connection);
        }
    }

    static async Task Pipe(Stream source, TcpClient target, Connection connection)
    {
        try
        {
            Socket targetSocket = target.Client;
            if (targetSocket == null) return;
            await source.CopyToAsync(target.GetStream(), 16384).ConfigureAwait(false);
            targetSocket.Shutdown(SocketShutdown.Send);
        }
        catch (IOException) { connection.Dispose(); }
        catch (SocketException) { connection.Dispose(); }
        catch (InvalidOperationException) { connection.Dispose(); }
    }

    bool IsAllowed(string host)
    {
        if (!IsDnsName(host)) return false;
        foreach (string domain in domains)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static bool IsDnsName(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253 || host.IndexOf('.') < 0) return false;
        foreach (string label in host.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-')
                return false;
            foreach (char c in label)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                    !(c >= '0' && c <= '9') && c != '-') return false;
        }

        IPAddress address;
        return !IPAddress.TryParse(host, out address);
    }

    static string[] ReadHeaders(Stream input)
    {
        var buffer = new byte[MaxHeaders];
        for (int i = 0; i < buffer.Length; i++)
        {
            int value = input.ReadByte();
            if (value < 0 || value > 127) throw new IOException("Invalid HTTP headers.");
            buffer[i] = (byte)value;
            if (i >= 3 && buffer[i - 3] == '\r' && buffer[i - 2] == '\n' &&
                buffer[i - 1] == '\r' && buffer[i] == '\n')
                return Encoding.ASCII.GetString(buffer, 0, i - 3).Split(new[] { "\r\n" }, StringSplitOptions.None);
        }
        throw new IOException("HTTP headers are too large.");
    }

    static string Header(string[] headers, string name)
    {
        string prefix = name + ":";
        string value = null;
        for (int i = 1; i < headers.Length; i++)
        {
            if (!headers[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (value != null) throw new IOException("Duplicate HTTP header.");
            value = headers[i].Substring(prefix.Length).Trim();
        }
        return value;
    }

    static void WriteAscii(Stream stream, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    static void WriteResponse(Stream stream, string status, string pac = "")
    {
        WriteAscii(stream, "HTTP/1.1 " + status + "\r\nConnection: close\r\n" +
            "Content-Type: application/x-ns-proxy-autoconfig\r\nCache-Control: no-store\r\n" +
            "Content-Length: " + pac.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n" + pac);
    }

    static byte[] ReadClientHello(Stream input, string host)
    {
        // Browser TLS can arrive in several records. Keep the exact handshake bytes,
        // including any later bytes in the final record, and change only record framing.
        byte[] data = new byte[MaxClientHello];
        byte[] header = new byte[5];
        int count = 0, helloLength = 0, version = 0;
        for (int record = 0; record < 32; record++)
        {
            ReadExactly(input, header, 0, header.Length);
            int recordVersion = UInt16(header, 1);
            if (record == 0) version = recordVersion;
            int length = UInt16(header, 3);
            if (header[0] != 22 || recordVersion < 0x0301 || recordVersion > 0x0303 ||
                length == 0 || length > MaxTlsRecord || length > data.Length - count)
                throw new IOException("Invalid TLS record.");
            ReadExactly(input, data, count, length);
            count += length;
            if (count < 4) continue;
            if (helloLength == 0)
            {
                helloLength = 4 + (data[1] << 16) + (data[2] << 8) + data[3];
                if (data[0] != 1 || helloLength > data.Length) throw new IOException("Invalid TLS ClientHello.");
            }
            if (count < helloLength) continue;

            int split = ServerNameSplit(data, helloLength, host);
            if (split > MaxTlsRecord || count - split > MaxTlsRecord)
                throw new IOException("ClientHello does not fit in two TLS records.");
            byte[] output = new byte[count + 10];
            WriteRecord(output, 0, version, data, 0, split);
            WriteRecord(output, split + 5, version, data, split, count - split);
            return output;
        }
        throw new IOException("Too many TLS records in ClientHello.");
    }

    static void WriteRecord(byte[] output, int offset, int version, byte[] data, int start, int count)
    {
        output[offset] = 22;
        output[offset + 1] = (byte)(version >> 8);
        output[offset + 2] = (byte)version;
        output[offset + 3] = (byte)(count >> 8);
        output[offset + 4] = (byte)count;
        Buffer.BlockCopy(data, start, output, offset + 5, count);
    }

    static int ServerNameSplit(byte[] data, int end, string host)
    {
        int position = 4 + 2 + 32;
        CheckLength(position, 1, end);
        if (data[position] > 32) throw new IOException("Invalid TLS session ID.");
        position += 1 + data[position];
        CheckLength(position, 2, end);
        int cipherLength = UInt16(data, position);
        if (cipherLength < 2 || cipherLength % 2 != 0) throw new IOException("Invalid TLS cipher suite list.");
        position += 2 + cipherLength;
        CheckLength(position, 1, end);
        if (data[position] == 0) throw new IOException("Invalid TLS compression list.");
        position += 1 + data[position];
        CheckLength(position, 2, end);
        int extensionLength = UInt16(data, position);
        position += 2;
        if (extensionLength != end - position) throw new IOException("Invalid TLS extensions.");

        int split = 0;
        while (position < end)
        {
            CheckLength(position, 4, end);
            int type = UInt16(data, position);
            int length = UInt16(data, position + 2);
            position += 4;
            CheckLength(position, length, end);
            if (type == 0)
            {
                if (split != 0 || length < 5 || UInt16(data, position) != length - 2 || data[position + 2] != 0)
                    throw new IOException("Invalid TLS server name extension.");
                int nameLength = UInt16(data, position + 3);
                int nameStart = position + 5;
                if (nameLength != length - 5 || nameLength != host.Length ||
                    !host.Equals(Encoding.ASCII.GetString(data, nameStart, nameLength), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("TLS server name does not match CONNECT.");
                split = nameStart + nameLength / 2;
            }
            position += length;
        }
        if (split == 0) throw new IOException("Missing TLS server name.");
        return split;
    }

    static void CheckLength(int position, int length, int end)
    {
        if (position > end || length > end - position) throw new IOException("Truncated TLS ClientHello.");
    }

    static int UInt16(byte[] data, int offset)
    {
        return (data[offset] << 8) | data[offset + 1];
    }

    static void ReadExactly(Stream stream, byte[] buffer, int offset, int length)
    {
        while (length != 0)
        {
            int read = stream.Read(buffer, offset, length);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
            length -= read;
        }
    }

    IPAddress[] Resolve(Connection connection, string host, Stopwatch elapsed)
    {
        lock (gate)
        {
            DnsCacheEntry cached;
            if (dnsCache.TryGetValue(host, out cached) && cached.Expires > DateTime.UtcNow) return cached.Addresses;
        }

        if (!dnsSlots.Wait(Remaining(elapsed))) throw new IOException("DNS request timed out.");
        try
        {
            DnsReply reply = QueryDns(connection, host, elapsed);
            if (reply.Status != 0 || reply.Truncated || reply.Answer == null)
                throw new IOException("DNS name could not be resolved.");

            var addresses = new List<IPAddress>();
            int ttl = 60;
            foreach (DnsAnswer answer in reply.Answer)
            {
                IPAddress address;
                if (answer == null || answer.Type != 1 || !IPAddress.TryParse(answer.Data, out address) ||
                    address.ToString() != answer.Data || !IsPublicAddress(address) || addresses.Contains(address)) continue;
                addresses.Add(address);
                ttl = Math.Min(ttl, answer.Ttl);
                if (addresses.Count == 8) break;
            }
            if (addresses.Count == 0) throw new IOException("DNS returned no public IPv4 addresses.");

            IPAddress[] result = addresses.ToArray();
            if (ttl > 0)
            {
                lock (gate)
                {
                    if (dnsCache.Count >= 128) dnsCache.Clear();
                    dnsCache[host] = new DnsCacheEntry { Addresses = result, Expires = DateTime.UtcNow.AddSeconds(ttl) };
                }
            }
            return result;
        }
        finally { dnsSlots.Release(); }
    }

    static DnsReply QueryDns(Connection connection, string host, Stopwatch elapsed)
    {
        // The fixed resolver address avoids using system DNS for bootstrap. SslStream
        // still verifies the resolver certificate against the HTTPS endpoint name.
        using (TcpClient client = Connect(connection, IPAddress.Parse("1.1.1.1"), elapsed))
        using (var tls = new SslStream(client.GetStream()))
        {
            tls.AuthenticateAsClient(ResolverHost, null, SslProtocols.Tls12, false);
            WriteAscii(tls, "GET /dns-query?name=" + host + "&type=A HTTP/1.0\r\nHost: " + ResolverHost +
                "\r\nAccept: application/dns-json\r\nConnection: close\r\n\r\n");

            var input = new BufferedStream(tls, 4096);
            string[] headers = ReadHeaders(input);
            string[] status = headers[0].Split(' ');
            int length;
            if (status.Length < 2 || !status[0].StartsWith("HTTP/1.", StringComparison.Ordinal) || status[1] != "200" ||
                !int.TryParse(Header(headers, "Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out length) ||
                length <= 0 || length > MaxDnsResponse || Header(headers, "Transfer-Encoding") != null)
                throw new IOException("Unexpected DNS resolver response.");

            byte[] response = new byte[length];
            ReadExactly(input, response, 0, length);
            var serializer = new DataContractJsonSerializer(typeof(DnsReply));
            using (var json = new MemoryStream(response))
            {
                var reply = (DnsReply)serializer.ReadObject(json);
                if (reply == null) throw new IOException("Empty DNS resolver response.");
                return reply;
            }
        }
    }

    static bool IsPublicAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] a = address.GetAddressBytes();
        return !(a[0] == 0 || a[0] == 10 || (a[0] == 100 && a[1] >= 64 && a[1] <= 127) ||
            a[0] == 127 || (a[0] == 169 && a[1] == 254) || (a[0] == 172 && (a[1] & 240) == 16) ||
            (a[0] == 192 && ((a[1] == 0 && (a[2] == 0 || a[2] == 2)) ||
                (a[1] == 88 && a[2] == 99) || a[1] == 168)) ||
            (a[0] == 198 && (a[1] == 18 || a[1] == 19 || (a[1] == 51 && a[2] == 100))) ||
            (a[0] == 203 && a[1] == 0 && a[2] == 113) || a[0] >= 224);
    }

    static TcpClient ConnectTarget(Connection connection, IPAddress[] addresses, Stopwatch elapsed)
    {
        foreach (IPAddress address in addresses)
        {
            try { return Connect(connection, address, elapsed); }
            catch (SocketException) { }
            catch (IOException) { }
        }
        throw new IOException("No DNS answer accepted a connection.");
    }

    static TcpClient Connect(Connection connection, IPAddress address, Stopwatch elapsed)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            Socket socket = client.Client;
            connection.SetRemote(client);
            client.NoDelay = true;
            IAsyncResult pending = socket.BeginConnect(address, 443, null, null);
            using (WaitHandle completion = pending.AsyncWaitHandle)
            {
                if (!completion.WaitOne(Math.Min(2500, Remaining(elapsed))))
                    throw new IOException("Connection timed out.");
                socket.EndConnect(pending);
            }
            return client;
        }
        catch
        {
            client.Close();
            throw;
        }
    }

    static int Remaining(Stopwatch elapsed)
    {
        long remaining = HandshakeTimeout - elapsed.ElapsedMilliseconds;
        if (remaining <= 0) throw new IOException("Handshake timed out.");
        return (int)remaining;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            listener.Stop();
            foreach (Connection connection in connections) connection.Dispose();
        }
    }

    sealed class Connection : IDisposable
    {
        readonly object gate = new object();
        public readonly TcpClient Browser;
        TcpClient remote;
        bool disposed;

        public Connection(TcpClient browser) { Browser = browser; }

        public void SetRemote(TcpClient client)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("BrowserRelay connection");
                remote = client;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                Browser.Close();
                if (remote != null) remote.Close();
            }
        }
    }

    sealed class DnsCacheEntry
    {
        public IPAddress[] Addresses;
        public DateTime Expires;
    }

    [DataContract]
    sealed class DnsReply
    {
        [DataMember(IsRequired = true)] public int Status { get; set; }
        [DataMember(Name = "TC")] public bool Truncated { get; set; }
        [DataMember] public DnsAnswer[] Answer { get; set; }
    }

    [DataContract]
    sealed class DnsAnswer
    {
        [DataMember(Name = "type")] public int Type { get; set; }
        [DataMember(Name = "data")] public string Data { get; set; }
        [DataMember(Name = "TTL")] public int Ttl { get; set; }
    }
}
