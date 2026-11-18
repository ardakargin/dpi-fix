// DpiCore.cs - DpiFix motoru (payload, servis, profil secimi, DoH, log, ag izleyici)
// C# 5 uyumlu. Derleme: build.cmd
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

static class Core
{
    public const string SvcName = "DpiFix";
    public const string SvcDisplay = "DPI Fix";
    public const string SvcDesc = "DPI Fix native engine";
    public const string LegacySvc = "GoodbyeDPI";
    public const string AppVersion = ProductInfo.Version;
    public const string EngineVersion = EngineInfo.Version;
    public const string ReleasesApi = "https://api.github.com/repos/ardakargin/dpi-fix/releases/latest";

    public static string AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    public static string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DpiFix");
    public static string Arch = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";

    public static string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DpiFix");
    public static string ServiceFilesDir { get { return Path.Combine(InstallDir, "payload"); } }
    public static string LegacyServiceFilesDir { get { return Path.Combine(DataDir, "payload"); } }
    public static string PayloadDir { get { return Path.Combine(ServiceFilesDir, Arch); } }
    public static string ExePath { get { return Path.Combine(PayloadDir, "dpifix-engine.exe"); } }
    public static string BlacklistPath { get { return Path.Combine(DataDir, "blacklist.txt"); } }
    public static string LogPath { get { return Path.Combine(DataDir, "log.txt"); } }
    public static string SettingsPath { get { return Path.Combine(DataDir, "settings.ini"); } }
    public static string DnsBackupPath { get { return Path.Combine(DataDir, "dns-backup.json"); } }

    public static bool? DohPreference;
    public static bool DohEnabled { get { return DohPreference == true; } }
    public static bool AutoReprobe = false;
    public static bool VerifyPayload = true;
    public static string Lang = Loc.SystemLanguage();

    static readonly object Gate = new object();
    public static Action<string> Sink;   // GUI/konsol kancası

    // ---------------- log ----------------

    public static void Log(string s)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + s;
        try
        {
            Directory.CreateDirectory(DataDir);
            lock (Gate) File.AppendAllText(LogPath, line + "\r\n", Encoding.UTF8);
        }
        catch { }
        if (Sink != null) { try { Sink(s); } catch { } }
    }

    // ---------------- ayarlar ----------------

    public static void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            string[] ls = File.ReadAllLines(SettingsPath);
            for (int i = 0; i < ls.Length; i++)
            {
                int p = ls[i].IndexOf('=');
                if (p < 0) continue;
                string k = ls[i].Substring(0, p).Trim();
                bool v = ls[i].Substring(p + 1).Trim() == "1";
                if (k == "doh") DohPreference = v;
                else if (k == "autoreprobe") AutoReprobe = v;
                else if (k == "verify") VerifyPayload = v;
                else if (k == "lang")
                {
                    string raw = ls[i].Substring(p + 1).Trim().ToLowerInvariant();
                    if (Array.IndexOf(Loc.Codes, raw) >= 0) Lang = raw;
                }
            }
        }
        catch { }
    }

    public static void SaveSettings(bool required = false)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SettingsPath,
                (DohPreference.HasValue ? "doh=" + (DohEnabled ? "1" : "0") + "\r\n" : "") +
                "autoreprobe=" + (AutoReprobe ? "1" : "0") + "\r\n" +
                "verify=" + (VerifyPayload ? "1" : "0") + "\r\n" +
                "lang=" + Lang + "\r\n", Encoding.UTF8);
        }
        catch { if (required) throw; }
    }

    public static bool? DohOverride(string[] args)
    {
        if (Array.IndexOf(args, "--no-doh") >= 0) return false;
        if (Array.IndexOf(args, "--try-doh") >= 0) return true;
        return null;
    }

    // Servise yazılmış komut satırının argüman kısmı (arayüzde gösterilir)
    public static string CurrentArgsShort()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + SvcName))
            {
                if (key == null) return "-";
                string command = Convert.ToString(key.GetValue("ImagePath")).TrimEnd();
                foreach (Profile profile in Profiles())
                    if (command.EndsWith(" --service " + profile.Args, StringComparison.OrdinalIgnoreCase))
                        return profile.Label;
                return "-";
            }
        }
        catch { return "-"; }
    }

    public static int BlacklistCount()
    {
        try
        {
            if (!File.Exists(BlacklistPath)) return 0;
            string[] ls = File.ReadAllLines(BlacklistPath);
            int n = 0;
            for (int i = 0; i < ls.Length; i++)
                if (ls[i].Trim().Length > 3 && !ls[i].Trim().StartsWith("#")) n++;
            return n;
        }
        catch { return 0; }
    }

    // ---------------- gömülü payload ----------------

    class Res
    {
        public string Name; public string Rel; public string Sha;
        public Res(string name, string rel, string sha) { Name = name; Rel = rel; Sha = sha; }
    }

    static Res[] AllResources = new Res[] {
        new Res("pl_x86_64_WinDivert_dll","x86_64/WinDivert.dll","6110bfa44667405179c3e15e12af1b62037e447ed59b054b19042032995e6c7e"),
        new Res("pl_x86_64_WinDivert64_sys","x86_64/WinDivert64.sys","e69b5ba3f0cd6cfb2983e442636e7f0b342b61b15264b0328317d4559c82cf50"),
        new Res("pl_engine","x86_64/dpifix-engine.exe",EngineInfo.Sha256),
    };

    public static string Sha256File(string path)
    {
        using (SHA256 s = SHA256.Create())
        using (FileStream fs = File.OpenRead(path))
        {
            byte[] h = s.ComputeHash(fs);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }
    }

    // Dönen değer: null = sorun yok, aksi halde uyarı metni
    public static string ExtractPayload()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        CheckLocalDirectory(InstallDir);
        CheckLocalDirectory(ServiceFilesDir);
        CheckLocalDirectory(PayloadDir);
        Directory.CreateDirectory(PayloadDir);
        string warn = null;
        for (int i = 0; i < AllResources.Length; i++)
        {
            Res r = AllResources[i];
            int slash = r.Rel.IndexOf('/');
            if (r.Rel.Substring(0, slash) != Arch) continue;
            string target = Path.Combine(PayloadDir, r.Rel.Substring(slash + 1));
            try
            {
                if (File.Exists(target) && string.Equals(Sha256File(target), r.Sha, StringComparison.OrdinalIgnoreCase))
                {
                    Log(Loc.T("log.payloadreused", Path.GetFileName(target)));
                    continue;
                }

                string staged = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (Stream s = asm.GetManifestResourceStream(r.Name))
                    {
                        if (s == null) throw new InvalidDataException("Missing embedded resource: " + r.Name);
                        using (FileStream file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            s.CopyTo(file);
                    }
                    if (VerifyPayload && !string.Equals(Sha256File(staged), r.Sha, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Integrity check failed: " + r.Rel);
                    if (File.Exists(target)) File.Replace(staged, target, null);
                    else File.Move(staged, target);
                }
                finally
                {
                    if (File.Exists(staged)) File.Delete(staged);
                }
            }
            catch (IOException ex)
            {
                warn = Loc.T("log.payloaderror", target, ex.Message);
                Log(warn);
                if (Path.GetExtension(target).Equals(".sys", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                    Log(Loc.T("log.driverbusy"));
            }
            catch (UnauthorizedAccessException ex)
            {
                warn = Loc.T("log.payloaderror", target, ex.Message);
                Log(warn);
            }
        }

        return warn;
    }

    // ---------------- site listesi ----------------

    const string DefaultBlacklist =
        "discord.com\r\ndiscordapp.com\r\ndiscordapp.net\r\ndiscord.gg\r\ndiscord.media\r\n" +
        "instagram.com\r\ncdninstagram.com\r\n" +
        "roblox.com\r\nrbxcdn.com\r\n" +
        "twitter.com\r\nx.com\r\ntwimg.com\r\nt.co\r\n";

    public static void EnsureBlacklist()
    {
        if (File.Exists(BlacklistPath)) return;
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(BlacklistPath, DefaultBlacklist, Encoding.UTF8);
        Log(Loc.T("log.blacklist", BlacklistPath));
    }

    public static string ReadBlacklist()
    {
        try { return File.Exists(BlacklistPath) ? File.ReadAllText(BlacklistPath) : ""; }
        catch { return ""; }
    }

    // ---------------- süreç / servis ----------------

    public static int Run(string file, string args, out string output)
    {
        ProcessStartInfo psi = new ProcessStartInfo(file, args);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        using (Process p = Process.Start(psi))
        {
            System.Threading.Tasks.Task<string> errors = System.Threading.Tasks.Task.Factory.StartNew(
                delegate { return p.StandardError.ReadToEnd(); });
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            output = (stdout + errors.Result).Trim();
            return p.ExitCode;
        }
    }

    public static int Sc(string args, out string output) { return Run("sc.exe", args, out output); }

    static string Ps(string script, out int code)
    {
        string file = Path.Combine(Path.GetTempPath(), "dpifix-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, script, new UTF8Encoding(true));
        try
        {
            string o;
            code = Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + file + "\"", out o);
            return o;
        }
        finally { try { File.Delete(file); } catch { } }
    }

    public static string State()
    {
        try
        {
            using (ServiceController sc = new ServiceController(SvcName))
            {
                ServiceControllerStatus s = sc.Status;
                if (s == ServiceControllerStatus.Running) return "running";
                if (s == ServiceControllerStatus.Stopped) return "stopped";
                if (s == ServiceControllerStatus.StartPending) return "starting";
                if (s == ServiceControllerStatus.StopPending) return "stopping";
                return "unknown";
            }
        }
        catch (InvalidOperationException) { return "absent"; }
        catch { return "unknown"; }
    }

    public static string StateText()
    {
        return Loc.T("st." + State());
    }

    public static void StopService()
    {
        string o;
        Sc("stop " + SvcName, out o);
        using (ServiceController service = new ServiceController(SvcName))
        {
            if (State() != "absent")
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(12));
        }
    }

    public static void StopAndRestore()
    {
        lock (Gate)
        {
            SetServiceOnDemand();
            StopService();
            if (!RestoreDns()) throw new InvalidOperationException("DNS restoration failed. Backup retained.");
        }
    }

    // ---------------- DNS katmanı ----------------

    public static bool DohApplied = false;

    static bool ApplyDoh()
    {
        string script =
            "$ErrorActionPreference='Stop'\r\n" +
            "$doh='https://cloudflare-dns.com/dns-query'\r\n" +
            "$servers=@(Get-DnsClientDohServerAddress -ServerAddress '1.1.1.1','1.0.0.1')\r\n" +
            "if($servers.Count -ne 2 -or @($servers | Where-Object {$_.DohTemplate -ne $doh}).Count){throw 'Cloudflare DoH templates unavailable in Windows'}\r\n" +
            "$idx=(Find-NetRoute -RemoteIPAddress '1.1.1.1' | Where-Object {$_.DestinationPrefix} | Select-Object -First 1).InterfaceIndex\r\n" +
            "if(-not $idx){throw 'aktif ag arayuzu bulunamadi'}\r\n" +
            "if(Test-Path '" + DnsBackupPath + "'){throw 'DNS backup already exists'}\r\n" +
            "$adapter=Get-NetAdapter -InterfaceIndex $idx\r\n" +
            "$guid='{' + ([guid]$adapter.InterfaceGuid).ToString() + '}'\r\n" +
            "$key='HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\' + $guid\r\n" +
            "$static=(Get-ItemProperty -LiteralPath $key).NameServer\r\n" +
            "$addresses=@(); if(![string]::IsNullOrWhiteSpace($static)){$addresses=@($static -split '[,\\s]+' | Where-Object {$_})}\r\n" +
            "$previous=@($servers | Where-Object {!$_.AutoUpgrade -or $_.AllowFallbackToUdp} | Select-Object ServerAddress,DohTemplate,AutoUpgrade,AllowFallbackToUdp)\r\n" +
            "$backup='" + DnsBackupPath + "'\r\n" +
            "@{InterfaceIndex=$idx;InterfaceGuid=$guid;Automatic=[string]::IsNullOrWhiteSpace($static);ServerAddresses=$addresses;DohBefore=$previous;IPv4Only=$true} | ConvertTo-Json -Depth 4 -Compress | Set-Content -Encoding UTF8 ($backup + '.tmp')\r\n" +
            "Move-Item -LiteralPath ($backup + '.tmp') -Destination $backup\r\n" +
            "foreach($s in $previous){Set-DnsClientDohServerAddress -ServerAddress $s.ServerAddress -AutoUpgrade $true -AllowFallbackToUdp $false}\r\n" +
            "netsh.exe interface ipv4 set dnsservers name=$idx source=static address=1.1.1.1 validate=no\r\n" +
            "if($LASTEXITCODE -ne 0){throw 'Could not set IPv4 DNS'}\r\n" +
            "netsh.exe interface ipv4 add dnsservers name=$idx address=1.0.0.1 index=2 validate=no\r\n" +
            "if($LASTEXITCODE -ne 0){throw 'Could not set secondary IPv4 DNS'}\r\n" +
            "Clear-DnsClientCache\r\n" +
            "Write-Output 'OK'\r\n";
        try
        {
            int code;
            string o = Ps(script, out code);
            if (code != 0 || o.IndexOf("OK") < 0)
            {
                Log("DNS-over-HTTPS uygulanamadı (kod " + code + ").");
                if (o.Length > 0) Log("   " + FirstLines(o, 3));
                return false;
            }
            DohApplied = true;
            Log(Loc.T("log.dohon"));
            return true;
        }
        catch (Exception ex) { Log("DNS-over-HTTPS hatası: " + ex.Message); return false; }
    }

    public static bool RestoreDns()
    {
        if (!File.Exists(DnsBackupPath)) return true;
        string script =
            "$ErrorActionPreference='Stop'\r\n" +
            "$b=Get-Content -Raw '" + DnsBackupPath + "' | ConvertFrom-Json\r\n" +
            "foreach($e in @($b)){\r\n" +
            "  $idx=$e.InterfaceIndex\r\n" +
            "  if($e.InterfaceGuid){$adapter=Get-NetAdapter | Where-Object {([guid]$_.InterfaceGuid) -eq ([guid]$e.InterfaceGuid)}; if(-not $adapter){throw 'Original DNS adapter unavailable'}; $idx=$adapter.ifIndex}\r\n" +
            "  if($e.IPv4Only){\r\n" +
            "    if($e.Automatic){netsh.exe interface ipv4 set dnsservers name=$idx source=dhcp; if($LASTEXITCODE -ne 0){throw 'Could not restore automatic IPv4 DNS'}}\r\n" +
            "    else{\r\n" +
            "      $addresses=@($e.ServerAddresses); if(!$addresses.Count){throw 'Static IPv4 DNS backup is empty'}\r\n" +
            "      netsh.exe interface ipv4 set dnsservers name=$idx source=static address=$($addresses[0]) validate=no\r\n" +
            "      if($LASTEXITCODE -ne 0){throw 'Could not restore IPv4 DNS'}\r\n" +
            "      for($i=1;$i -lt $addresses.Count;$i++){netsh.exe interface ipv4 add dnsservers name=$idx address=$($addresses[$i]) index=$($i+1) validate=no; if($LASTEXITCODE -ne 0){throw 'Could not restore additional IPv4 DNS'}}\r\n" +
            "    }\r\n" +
            "    foreach($s in $e.DohBefore){Set-DnsClientDohServerAddress -ServerAddress $s.ServerAddress -DohTemplate $s.DohTemplate -AutoUpgrade ([bool]$s.AutoUpgrade) -AllowFallbackToUdp ([bool]$s.AllowFallbackToUdp)}\r\n" +
            "  }elseif(!$e.Automatic -and $e.ServerAddresses -and $e.ServerAddresses.Count -gt 0){Set-DnsClientServerAddress -InterfaceIndex $idx -ServerAddresses $e.ServerAddresses}\r\n" +
            "  else{Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses}\r\n" +
            "}\r\n" +
            "Clear-DnsClientCache\r\n" +
            "Remove-Item '" + DnsBackupPath + "' -Force\r\n" +
            "Write-Output 'OK'\r\n";
        try
        {
            int code;
            string o = Ps(script, out code);
            if (code == 0)
            {
                DohApplied = false;
                Log("Önceki DNS ayarı geri yüklendi.");
                return true;
            }
            else Log("DNS ayarı geri yüklenemedi: " + FirstLines(o, 2));
        }
        catch (Exception ex) { Log("DNS geri yükleme hatası: " + ex.Message); }
        return false;
    }

    static string FirstLines(string s, int n)
    {
        string[] ls = s.Split('\n');
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < ls.Length && i < n; i++) sb.Append(ls[i].Trim()).Append(' ');
        return sb.ToString().Trim();
    }

    // ---------------- yapılandırma profilleri ----------------

    public class Profile
    {
        public string Label; public string Args;
        public Profile(string label, string args) { Label = label; Args = args; }
    }

    public static Profile[] Profiles()
    {
        string domains = "--domains " + QuoteArgument(BlacklistPath);
        return new Profile[] {
            new Profile("HTTPS", domains),
            new Profile(Loc.T("profile.records"), domains + " --tls-record-split"),
            new Profile(Loc.T("profile.reordered"), domains + " --tls-reorder"),
            new Profile("HTTPS + HTTP", domains + " --http")
        };
    }

    class ProbeTarget
    {
        public string Host;
        public bool Control;
        public bool Listed;
        public int Status;
        public string Failure = "timeout";
        public bool Responded { get { return Status >= 200 && Status < 600; } }
        public bool Accessible { get { return Status >= 200 && Status < 400; } }
        public ProbeTarget(string host, bool control, bool listed)
        {
            Host = host;
            Control = control;
            Listed = listed;
        }

        public ProbeTarget NewAttempt() { return new ProbeTarget(Host, Control, Listed); }
    }

    static readonly string[][] RepresentativeSites = {
        new[] { "discord.com", "discordapp.com", "discordapp.net", "discord.gg", "discord.media" },
        new[] { "www.instagram.com", "instagram.com", "cdninstagram.com" },
        new[] { "www.roblox.com", "roblox.com", "rbxcdn.com" },
        new[] { "x.com", "twitter.com", "twimg.com", "t.co" }
    };

    static bool CoversHost(string domain, string host)
    {
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    static ProbeTarget[] Targets(string blacklist)
    {
        List<ProbeTarget> list = new List<ProbeTarget>();
        list.Add(new ProbeTarget("www.google.com", true, false));
        list.Add(new ProbeTarget("www.youtube.com", true, false));
        list.Add(new ProbeTarget("example.com", true, false));
        list.Add(new ProbeTarget("www.tiktok.com", true, false));

        string[] bl = blacklist.TrimStart('\uFEFF').Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        List<string> domains = new List<string>();
        for (int i = 0; i < bl.Length; i++)
        {
            string h = bl[i].Split('#')[0].Trim();
            if (h.StartsWith("*.", StringComparison.Ordinal)) h = h.Substring(2);
            h = Regex.Replace(h, ":[0-9]+\\z", "");
            h = h.TrimEnd('.');
            if (Uri.CheckHostName(h) != UriHostNameType.Dns || h.IndexOf('.') < 0) continue;
            domains.Add(h);
            foreach (ProbeTarget target in list)
                if (CoversHost(h, target.Host)) target.Listed = true;
        }

        const int maxListedProbes = 6;
        int added = 0;
        var represented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string[] sites in RepresentativeSites)
        {
            string primary = sites[0];
            if (!domains.Exists(domain => CoversHost(domain, primary))) continue;
            list.Add(new ProbeTarget(primary, false, true));
            represented.UnionWith(sites);
            added++;
        }
        foreach (string h in domains)
        {
            if (added >= maxListedProbes) break;
            if (represented.Contains(h) || list.Exists(target => target.Host.Equals(h, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new ProbeTarget(h, false, true));
            added++;
        }
        if (!list.Exists(target => target.Listed))
        {
            // Optional diagnostics when no valid listed target could be found.
            list.Add(new ProbeTarget("discord.com", false, false));
            list.Add(new ProbeTarget("www.instagram.com", false, false));
        }
        return list.ToArray();
    }

    static ProbeTarget[] ProbeAll(ProbeTarget[] baseline = null)
    {
        ProbeTarget[] t = baseline == null ? Targets(ReadBlacklist()) :
            Array.ConvertAll(baseline, target => target.NewAttempt());
        Thread[] ts = new Thread[t.Length];
        for (int i = 0; i < t.Length; i++)
        {
            ProbeTarget cur = t[i];
            ts[i] = new Thread(delegate () { ProbeHttps(cur, 4000); });
            ts[i].IsBackground = true;
            ts[i].Start();
        }
        Stopwatch deadline = Stopwatch.StartNew();
        for (int i = 0; i < ts.Length; i++)
        {
            int remaining = Math.Max(0, 9000 - (int)deadline.ElapsedMilliseconds);
            if (!ts[i].Join(remaining)) t[i] = t[i].NewAttempt();
        }
        return t;
    }

    static bool ControlResponded(ProbeTarget[] targets)
    {
        return Array.Exists(targets, target => target.Control && target.Responded);
    }

    static bool AllAccessible(ProbeTarget[] targets)
    {
        return Array.TrueForAll(targets, target => target.Accessible);
    }

    static bool ListedSitesResponded(ProbeTarget[] targets)
    {
        return ControlResponded(targets) && Array.Exists(targets, target => target.Listed) &&
            Array.TrueForAll(targets, target => !target.Listed || target.Responded);
    }

    static void ProbeHttps(ProbeTarget target, int ms)
    {
        TcpClient tcp = null;
        try
        {
            target.Failure = "dns";
            string host = new System.Globalization.IdnMapping().GetAscii(target.Host);
            IPAddress[] addresses = Dns.GetHostAddresses(host);
            if (addresses.Length == 0) return;
            target.Failure = "tcp";
            tcp = Socket.OSSupportsIPv6 ? new TcpClient(AddressFamily.InterNetworkV6) : new TcpClient();
            if (Socket.OSSupportsIPv6) tcp.Client.DualMode = true;
            IAsyncResult ar = tcp.BeginConnect(addresses, 443, null, null);
            using (WaitHandle wait = ar.AsyncWaitHandle)
            {
                if (!wait.WaitOne(ms, false)) return;
                tcp.EndConnect(ar);
            }
            tcp.ReceiveTimeout = ms;
            tcp.SendTimeout = ms;
            target.Failure = "tls";
            using (SslStream ssl = new SslStream(tcp.GetStream(), false,
                delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
                {
                    if (errors == SslPolicyErrors.None) return true;
                    target.Failure = "cert";
                    return false;
                }))
            {
                // Tarayıcı gibi modern TLS iste. .NET varsayılanı TLS 1.0/1.1 olabiliyor;
                // sunucular bunu reddedince prob yanlış "engelli" sonucu veriyor.
                SslProtocols modern = SslProtocols.Tls12 | (SslProtocols)12288; // 12288 = Tls13
                try { ssl.AuthenticateAsClient(host, null, modern, false); }
                catch (ArgumentException) { ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false); }
                target.Failure = "http";
                byte[] request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: " + host +
                    "\r\nUser-Agent: DPI-Fix-Connectivity/1.0\r\nConnection: close\r\n\r\n");
                ssl.Write(request, 0, request.Length);
                byte[] prefix = new byte[12];
                int used = 0;
                while (used < prefix.Length)
                {
                    int read = ssl.Read(prefix, used, prefix.Length - used);
                    if (read == 0) return;
                    used += read;
                }
                target.Status = ReadHttpStatus(prefix);
            }
        }
        catch (SocketException ex)
        {
            if (target.Failure == "dns" && ex.SocketErrorCode == SocketError.TimedOut)
                target.Failure = "timeout";
        }
        catch (ArgumentException) { }
        catch (IOException) { }
        catch (AuthenticationException) { }
        catch (InvalidOperationException) { }
        finally { if (tcp != null) { try { tcp.Close(); } catch { } } }
    }

    static int ReadHttpStatus(byte[] prefix)
    {
        if (prefix.Length != 12 || prefix[8] != ' ' ||
            (prefix[7] != '0' && prefix[7] != '1') ||
            Encoding.ASCII.GetString(prefix, 0, 7) != "HTTP/1.")
            return 0;
        for (int i = 9; i < 12; i++)
            if (prefix[i] < '0' || prefix[i] > '9') return 0;
        int status = (prefix[9] - '0') * 100 + (prefix[10] - '0') * 10 + prefix[11] - '0';
        return status >= 200 && status < 600 ? status : 0;
    }

    // DNS önbelleği temizlenmezse test, servis kurulmadan önceki zehirli cevabı okur.
    public static void FlushDns()
    {
        try { string o; Run("ipconfig.exe", "/flushdns", out o); }
        catch { }
    }

    static void LogProbe(ProbeTarget[] t)
    {
        for (int i = 0; i < t.Length; i++)
            Log("   " + t[i].Host + " = " + FormatProbe(t[i]));
    }

    static string FormatProbe(ProbeTarget target)
    {
        string result = target.Status == 0 ? Loc.T("probe." + target.Failure) : "HTTP " + target.Status;
        return result + (target.Control ? Loc.T("log.req") : "");
    }

    static bool PreservesConnectivity(ProbeTarget[] before, ProbeTarget[] after)
    {
        if (!ControlResponded(before)) return false;
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i].Accessible && !after[i].Accessible) return false;
            if (before[i].Responded && !after[i].Responded) return false;
        }
        return true;
    }

    static int AccessGain(ProbeTarget[] before, ProbeTarget[] after)
    {
        int gain = 0;
        for (int i = 0; i < before.Length; i++)
            if (before[i].Listed && !before[i].Accessible && after[i].Accessible) gain++;
        return gain;
    }

    static string SelectProfile(ProbeTarget[] baseline, bool acceptPreservation)
    {
        Profile[] ps = Profiles();
        string best = null;
        int bestGain = 0;
        int candidateCount = acceptPreservation ? 1 : ps.Length;
        for (int i = 0; i < candidateCount; i++)
        {
            Profile pr = ps[i];
            Log(Loc.T("log.trying", pr.Label));
            Process p = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ExePath, pr.Args);
                psi.WorkingDirectory = Path.GetDirectoryName(ExePath);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                p = Process.Start(psi);
                if (p.WaitForExit(2500))
                {
                    Log(Loc.T("log.engineexit", p.ExitCode, FirstLines(p.StandardError.ReadToEnd(), 2)));
                    continue;
                }
                ProbeTarget[] t = ProbeAll(baseline);
                LogProbe(t);
                if (p.HasExited)
                {
                    Log(Loc.T("log.engineexit", p.ExitCode, FirstLines(p.StandardError.ReadToEnd(), 2)));
                    continue;
                }
                if (!PreservesConnectivity(baseline, t))
                {
                    Log("   " + Loc.T("log.next"));
                    continue;
                }
                int gain = AccessGain(baseline, t);
                if (acceptPreservation) return pr.Args;
                if (gain > bestGain) { best = pr.Args; bestGain = gain; }
            }
            catch (Exception ex) { Log("   hata: " + ex.Message); }
            finally
            {
                if (p != null)
                {
                    try { if (!p.HasExited) { p.Kill(); p.WaitForExit(2000); } }
                    finally { p.Dispose(); }
                }
            }
        }
        return best;
    }

    // ---------------- kurulum ----------------

    public static bool SupportedWindows()
    {
        return Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") == "AMD64" ||
               Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") == "AMD64";
    }

    static void SetServiceOnDemand()
    {
        const int serviceDoesNotExist = 1060;
        string output;
        int result = Sc("config " + SvcName + " start= demand", out output);
        if (result != 0 && result != serviceDoesNotExist) throw new InvalidOperationException(output);
    }

    public static bool Install(bool? tryDoh = null)
    {
        lock (Gate)
        {
            string o;
            bool ok = false;
            bool stopped = false;
            bool created = false;
            bool noVerifiedProfile = false;
            bool improved = false;
            bool allowDoh = tryDoh ?? DohEnabled;
            try
            {
                if (!SupportedWindows())
                    throw new PlatformNotSupportedException("DPI Fix requires x64 Windows.");
                Log(Loc.T("log.step2"));
                // Keep the previous profile from starting at boot while it is being revalidated.
                SetServiceOnDemand();
                StopService();
                stopped = true;
                if (!RestoreDns()) return false;
                EnsureBlacklist();
                ProbeTarget[] baseline = ProbeAll();
                Log(Loc.T("log.baseline"));
                LogProbe(baseline);

                Log(Loc.T("log.step1"));
                string warn = ExtractPayload();
                if (warn != null) { Log(warn); return false; }
                SaveSettings();

                bool acceptPreservation = ListedSitesResponded(baseline);
                string args = SelectProfile(baseline, acceptPreservation);
                if (args == null && !acceptPreservation && allowDoh)
                {
                    if (ApplyDoh()) args = SelectProfile(baseline, false);
                    else if (!RestoreDns()) return false;
                }
                if (args == null)
                {
                    noVerifiedProfile = true;
                    Log(Loc.T("log.none"));
                    if (!allowDoh && Array.Exists(baseline, t => t.Listed && (t.Failure == "dns" || t.Failure == "cert") && t.Status == 0))
                        Log(Loc.T("log.trydoh"));
                    return false;
                }

                if (State() != "absent" && Sc("delete " + SvcName, out o) != 0)
                    throw new InvalidOperationException(o);
                string full = "--service " + args;
                string binPath = "\"" + ExePath + "\" " + full;
                string line = "create " + SvcName + " binPath= " + QuoteArgument(binPath) +
                              " start= demand DisplayName= \"DPI Fix\"";
                if (Sc(line, out o) != 0) throw new InvalidOperationException(o);
                created = true;
                Sc("description " + SvcName + " \"" + SvcDesc + "\"", out o);
                if (Sc("start " + SvcName, out o) != 0) throw new InvalidOperationException(o);
                using (ServiceController service = new ServiceController(SvcName))
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(12));
                ProbeTarget[] running = ProbeAll(baseline);
                LogProbe(running);
                improved = AccessGain(baseline, running) > 0;
                if (PreservesConnectivity(baseline, running) && (improved || acceptPreservation) && State() == "running")
                {
                    if (Sc("config " + SvcName + " start= auto", out o) != 0)
                        throw new InvalidOperationException(o);
                    ok = true;
                    if (Sc("failure " + SvcName + " reset= 86400 actions= restart/5000/restart/15000", out o) != 0)
                        Log(o);
                    try { File.Delete(Path.Combine(PayloadDir, "dpifix-service.exe")); }
                    catch (IOException ex) { Log(ex.Message); }
                    catch (UnauthorizedAccessException ex) { Log(ex.Message); }
                }
                else Log(Loc.T("log.serviceValidationFailed"));
            }
            catch (Exception ex) { Log(ex.Message); }
            finally
            {
                bool dnsRestored = true;
                if (!ok && stopped)
                {
                    try
                    {
                        if (created)
                        {
                            StopService();
                            Sc("delete " + SvcName, out o);
                        }
                    }
                    catch (Exception ex) { Log(ex.Message); }
                    finally { dnsRestored = RestoreDns(); }
                }
                if (ok)
                {
                    Log(Loc.T(improved ? "log.doneImproved" : "log.donePreserved"));
                    Log(Loc.T("log.done"));
                }
                else if (!noVerifiedProfile || !dnsRestored) Log(Loc.T("log.failed"));
            }
            return ok;
        }
    }

    static string QuoteArgument(string value)
    {
        return "\"" + Regex.Replace(value, "(\\\\*)(\"|$)", delegate(Match match)
        {
            string slashes = match.Groups[1].Value;
            return slashes + slashes + (match.Groups[2].Value == "\"" ? "\\\"" : "");
        }) + "\"";
    }

    public static bool Uninstall(bool keepAppData = false)
    {
        lock (Gate)
        {
            CheckLocalDirectory(DataDir);
            CheckLocalDirectory(InstallDir);
            CheckLocalDirectory(ServiceFilesDir);
            if (keepAppData) CheckLocalDirectory(LegacyServiceFilesDir);
            string output;
            SetServiceOnDemand();
            StopService();
            if (!RestoreDns()) throw new InvalidOperationException("DNS restoration failed. Backup retained.");
            if (State() != "absent" && Sc("delete " + SvcName, out output) != 0)
                throw new InvalidOperationException(output);
            RemoveOwnedDriver();
            bool delayed = Directory.Exists(ServiceFilesDir) && RemoveDataDirectory(ServiceFilesDir);
            string directory = keepAppData ? LegacyServiceFilesDir : DataDir;
            if (Directory.Exists(directory)) delayed |= RemoveDataDirectory(directory);
            return delayed;
        }
    }

    static void CheckLocalDirectory(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Unexpected redirected DPI Fix directory: " + path);
    }

    static bool RemoveDataDirectory(string path)
    {
        CheckLocalDirectory(path);
        bool delayed = false;
        foreach (string folder in Directory.GetDirectories(path))
            delayed |= RemoveDataDirectory(folder);
        foreach (string file in Directory.GetFiles(path))
            delayed |= RemoveOwnedFile(file);
        if (delayed) QueueRemoval(path);
        else Directory.Delete(path);
        return delayed;
    }

    internal static bool RemoveOwnedFile(string path)
    {
        try { File.Delete(path); return false; }
        catch (IOException)
        {
            QueueRemoval(path);
            return true;
        }
    }

    internal static void QueueRemoval(string path)
    {
        const int delayUntilReboot = 4;
        if (!MoveFileEx(path, null, delayUntilReboot))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string existing, string destination, int flags);

    static void RemoveOwnedDriver()
    {
        const string name = "WinDivert";
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WinDivert"))
        {
            if (key == null) return;
            string path = Environment.ExpandEnvironmentVariables(Convert.ToString(key.GetValue("ImagePath"))).Trim('"');
            if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path.Substring(4);
            string owned = Path.Combine(PayloadDir, "WinDivert64.sys");
            string legacy = Path.Combine(LegacyServiceFilesDir, Arch, "WinDivert64.sys");
            if (!String.Equals(path, owned, StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(path, legacy, StringComparison.OrdinalIgnoreCase)) return;
        }
        using (ServiceController driver = new ServiceController(name))
        {
            string output;
            if (driver.Status != ServiceControllerStatus.Stopped)
            {
                if (driver.Status != ServiceControllerStatus.StopPending && Sc("stop " + name, out output) != 0)
                    throw new InvalidOperationException(output);
                driver.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(12));
            }
            if (Sc("delete " + name, out output) != 0) throw new InvalidOperationException(output);
        }
    }

    public static void Test()
    {
        Log(Loc.T("log.testhead", StateText()));
        ProbeTarget[] t = ProbeAll();
        LogProbe(t);
        if (State() != "running") Log("   " + Loc.T("log.testwarn"));
        else if (!ControlResponded(t)) Log("   " + Loc.T("log.testfail"));
        else if (!AllAccessible(t)) Log("   " + Loc.T("log.testpartial"));
        else Log("   " + Loc.T("log.testok"));
    }

    // ---------------- güncelleme kontrolü ----------------

    public static void CheckUpdate()
    {
        Log(Loc.T("log.updscan"));
        Log(Loc.T("log.updcur", EngineVersion, AppVersion));
        try
        {
            using (WebClient client = new WebClient())
            {
                client.Headers.Add("User-Agent", "DpiFix");
                client.Encoding = Encoding.UTF8;
                string json = client.DownloadString(ReleasesApi);
                Match match = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                Version published;
                if (!match.Success || !Version.TryParse(match.Groups[1].Value.TrimStart('v', 'V'), out published))
                    throw new InvalidDataException("Could not read the published DPI Fix version.");
                Log("DPI Fix · " + published);
                if (published > new Version(AppVersion))
                    Log(Loc.T("log.updhas") + " https://github.com/ardakargin/dpi-fix/releases");
                else Log(Loc.T("log.updnone"));
            }
        }
        catch (Exception ex) { Log("   Kontrol başarısız: " + ex.Message); }
    }

    // ---------------- ağ izleyici ----------------

    static Timer netDebounce;

    public static void StartNetworkWatch()
    {
        try
        {
            NetworkChange.NetworkAddressChanged += delegate
            {
                if (!AutoReprobe) return;
                if (netDebounce == null)
                    netDebounce = new Timer(NetRecheck, null, Timeout.Infinite, Timeout.Infinite);
                netDebounce.Change(30000, Timeout.Infinite);
            };
        }
        catch { }
    }

    static void NetRecheck(object state)
    {
        try
        {
            if (State() != "running") return;
            Log(Loc.T("log.netchg"));
            ProbeTarget[] t = ProbeAll();
            if (AllAccessible(t)) { Log(Loc.T("log.netok")); return; }
            LogProbe(t);
            Log(Loc.T(ControlResponded(t) ? "log.testpartial" : "log.netbad"));
        }
        catch (Exception ex) { Log("Ağ yeniden kontrol hatası: " + ex.Message); }
    }

    // ---------------- teşhis ----------------

    public static void SelfTest()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("dpifix=" + AppVersion + " engine=" + EngineVersion);
        sb.AppendLine("arch=" + Arch);
        sb.AppendLine("datadir=" + DataDir);
        sb.AppendLine("payload=" + (File.Exists(ExePath) ? "var" : "yok"));
        sb.AppendLine("blacklist=" + (File.Exists(BlacklistPath) ? "var" : "yok"));
        sb.AppendLine("svc=" + State() + " (" + StateText() + ")");
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\" + SvcName))
        {
            sb.AppendLine("svcArgs=" + (key == null ? "-" : key.GetValue("ImagePath", "-")));
            sb.AppendLine("svcAutoStart=" + (key != null && Convert.ToInt32(key.GetValue("Start", -1)) == 2));
        }
        sb.AppendLine("doh=" + DohEnabled + " autoreprobe=" + AutoReprobe + " verify=" + VerifyPayload);
        ProbeTarget[] t = ProbeAll();
        for (int i = 0; i < t.Length; i++)
            sb.AppendLine("net." + t[i].Host + "=" + FormatProbe(t[i]));
        sb.AppendLine("controlResponded=" + ControlResponded(t));
        sb.AppendLine("pass=" + AllAccessible(t));
        sb.AppendLine("resCount=" + AllResources.Length);
        sb.AppendLine("embeddedVerify=" + VerifyEmbedded());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "dpifix-selftest.txt"), sb.ToString());
    }

    // Gömülü kaynakların hash'lerini beklenen değerlerle karşılaştırır
    public static string VerifyEmbedded()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        int ok = 0, bad = 0;
        StringBuilder issues = new StringBuilder();
        for (int i = 0; i < AllResources.Length; i++)
        {
            Res r = AllResources[i];
            try
            {
                using (Stream s = asm.GetManifestResourceStream(r.Name))
                {
                    if (s == null) { bad++; issues.Append(r.Rel).Append(":yok "); continue; }
                    byte[] buf = new byte[s.Length];
                    int off = 0;
                    while (off < buf.Length)
                    {
                        int n = s.Read(buf, off, buf.Length - off);
                        if (n <= 0) break;
                        off += n;
                    }
                    string h;
                    using (SHA256 sha = SHA256.Create())
                    {
                        byte[] d = sha.ComputeHash(buf);
                        StringBuilder hb = new StringBuilder();
                        for (int j = 0; j < d.Length; j++) hb.Append(d[j].ToString("x2"));
                        h = hb.ToString();
                    }
                    if (string.Equals(h, r.Sha, StringComparison.OrdinalIgnoreCase)) ok++;
                    else { bad++; issues.Append(r.Rel).Append(":uyusmazlik "); }
                }
            }
            catch { bad++; issues.Append(r.Rel).Append(":hata "); }
        }
        return ok + "/" + AllResources.Length + (bad > 0 ? " SORUNLU: " + issues.ToString().Trim() : "");
    }
}
