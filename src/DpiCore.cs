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
    public const string SvcDesc = "Türkiye için DPI/DNS zorlamasını kaldırır (GoodbyeDPI).";
    public const string LegacySvc = "GoodbyeDPI";
    public const string AppVersion = "2.2.1";
    public const string EngineVersion = "0.2.3rc3";
    public const string UpstreamApi = "https://api.github.com/repos/ValdikSS/GoodbyeDPI/releases/latest";

    public static string AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    public static string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DpiFix");
    public static string Arch = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";

    public static string PayloadDir { get { return Path.Combine(DataDir, "payload", Arch); } }
    public static string GoodbyeDpiPath { get { return Path.Combine(PayloadDir, "goodbyedpi.exe"); } }
    public static string RustEnginePath { get { return Path.Combine(PayloadDir, "dpifix-engine.exe"); } }
    /// Rust motoru yalnızca 64-bit sistemde ve dosya varsa kullanılabilir
    public static bool RustEngineUsable
    {
        get { return Environment.Is64BitOperatingSystem && File.Exists(RustEnginePath); }
    }
    public static bool RustEngineCompiled
    {
        get { return EngineInfoPresent.Value && Environment.Is64BitOperatingSystem; }
    }
    /// Servise yazılacak yürütülebilir dosya
    public static string ExePath
    {
        get { return Engine == "rust" && RustEngineUsable ? RustEnginePath : GoodbyeDpiPath; }
    }
    public static string BlacklistPath { get { return Path.Combine(DataDir, "blacklist.txt"); } }
    public static string LogPath { get { return Path.Combine(DataDir, "log.txt"); } }
    public static string SettingsPath { get { return Path.Combine(DataDir, "settings.ini"); } }
    public static string DnsBackupPath { get { return Path.Combine(DataDir, "dns-backup.json"); } }

    public static bool DohEnabled = true;
    public static bool AutoReprobe = true;
    public static bool VerifyPayload = true;
    public static string Lang = "tr";
    /// "goodbyedpi" (varsayılan) veya "rust"
    public static string Engine = "goodbyedpi";

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
                if (k == "doh") DohEnabled = v;
                else if (k == "autoreprobe") AutoReprobe = v;
                else if (k == "verify") VerifyPayload = v;
                else if (k == "lang")
                {
                    string raw = ls[i].Substring(p + 1).Trim().ToLowerInvariant();
                    if (raw == "en" || raw == "tr") Lang = raw;
                }
                else if (k == "engine")
                {
                    string raw = ls[i].Substring(p + 1).Trim().ToLowerInvariant();
                    if (raw == "rust" || raw == "goodbyedpi") Engine = raw;
                }
            }
        }
        catch { }
    }

    public static void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SettingsPath,
                "doh=" + (DohEnabled ? "1" : "0") + "\r\n" +
                "autoreprobe=" + (AutoReprobe ? "1" : "0") + "\r\n" +
                "verify=" + (VerifyPayload ? "1" : "0") + "\r\n" +
                "lang=" + Lang + "\r\n" +
                "engine=" + Engine + "\r\n", Encoding.UTF8);
        }
        catch { }
    }

    // Servise yazılmış komut satırının argüman kısmı (arayüzde gösterilir)
    public static string CurrentArgsShort()
    {
        try
        {
            string o;
            Sc("qc " + SvcName, out o);
            Match m = Regex.Match(o, "BINARY_PATH_NAME\\s*:\\s*(.+)");
            if (!m.Success) return "-";
            string line = m.Groups[1].Value.Trim();
            int q = line.IndexOf("\" ");
            string args = q >= 0 ? line.Substring(q + 2) : line;
            args = args.Replace(BlacklistPath, "blacklist.txt");
            if (args.Length > 28) args = args.Substring(0, 26) + "..";
            return args.Length == 0 ? "-" : args;
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
        new Res("pl_x86_goodbyedpi_exe","x86/goodbyedpi.exe","66e202c9fce9e769e2bc791b7fd6f56f21eab59f607f4ed0724e0c68c430dd1f"),
        new Res("pl_x86_WinDivert_dll","x86/WinDivert.dll","625ffdd95bfabff32d0e8a95beabcd303c01c8bba73b90402d4e84d6e15dd8e5"),
        new Res("pl_x86_WinDivert32_sys","x86/WinDivert32.sys","29ca5ceb59c9c6993a349e82b1fd46078e6f8a302764153ab84fa22e382fcdca"),
        new Res("pl_x86_WinDivert64_sys","x86/WinDivert64.sys","e69b5ba3f0cd6cfb2983e442636e7f0b342b61b15264b0328317d4559c82cf50"),
        new Res("pl_x86_64_goodbyedpi_exe","x86_64/goodbyedpi.exe","8d412b094bb9c137ff25ba9a794d1122ecc84bb776debff6c249723a13cc31cd"),
        new Res("pl_x86_64_WinDivert_dll","x86_64/WinDivert.dll","6110bfa44667405179c3e15e12af1b62037e447ed59b054b19042032995e6c7e"),
        new Res("pl_x86_64_WinDivert64_sys","x86_64/WinDivert64.sys","e69b5ba3f0cd6cfb2983e442636e7f0b342b61b15264b0328317d4559c82cf50")
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
                using (Stream s = asm.GetManifestResourceStream(r.Name))
                {
                    if (s == null) { Log("   gömülü kaynak yok: " + r.Name); warn = "Gömülü kaynak eksik: " + r.Name; continue; }
                    byte[] buf = new byte[s.Length];
                    int off = 0;
                    while (off < buf.Length)
                    {
                        int n = s.Read(buf, off, buf.Length - off);
                        if (n <= 0) break;
                        off += n;
                    }
                    File.WriteAllBytes(target, buf);
                }
            }
            catch (Exception ex) { Log("   yazılamadı " + target + ": " + ex.Message); warn = "Dosya yazılamadı: " + target; continue; }

            if (VerifyPayload)
            {
                string h = Sha256File(target);
                if (!string.Equals(h, r.Sha, StringComparison.OrdinalIgnoreCase))
                {
                    Log("   BÜTÜNLÜK UYARISI: " + r.Rel);
                    Log("     beklenen " + r.Sha);
                    Log("     bulunan  " + h);
                    warn = "Bütünlük doğrulanamadı: " + r.Rel;
                }
            }
        }

        // Rust motoru (64-bit sistemlerde) — ayrı kaynak, ayrı doğrulama
        if (EngineInfoPresent.Value && Environment.Is64BitOperatingSystem)
        {
            try
            {
                using (Stream s = asm.GetManifestResourceStream("pl_engine"))
                {
                    if (s == null) { Log("   gömülü Rust motoru yok"); }
                    else
                    {
                        byte[] buf = new byte[s.Length];
                        int off = 0;
                        while (off < buf.Length)
                        {
                            int n2 = s.Read(buf, off, buf.Length - off);
                            if (n2 <= 0) break;
                            off += n2;
                        }
                        File.WriteAllBytes(RustEnginePath, buf);
                        Log("Rust motoru çıkarıldı: dpifix-engine.exe (" + (buf.Length / 1024) + " KB)");
                        if (VerifyPayload && EngineInfo.Sha256.Length > 0)
                        {
                            string h = Sha256File(RustEnginePath);
                            if (!string.Equals(h, EngineInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                            {
                                Log("   BÜTÜNLÜK UYARISI: dpifix-engine.exe");
                                warn = "Bütünlük doğrulanamadı: dpifix-engine.exe";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("   Rust motoru yazılamadı: " + ex.Message);
                warn = "Rust motoru yazılamadı";
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
        Process p = Process.Start(psi);
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        output = o.Trim();
        return p.ExitCode;
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

    public static void KillStrays()
    {
        try
        {
            Process[] ps = Process.GetProcessesByName("goodbyedpi");
            for (int i = 0; i < ps.Length; i++)
            {
                try { ps[i].Kill(); } catch { }
                try { ps[i].WaitForExit(1500); } catch { }
            }
        }
        catch { }
    }

    public static void StopService()
    {
        string o;
        Sc("stop " + SvcName, out o);
        Thread.Sleep(900);
        KillStrays();
    }

    // ---------------- DNS katmanı ----------------

    static string dnsArgs = "";
    public static bool DohApplied = false;

    public static bool ApplyDoh()
    {
        if (!DohEnabled) { Log(Loc.T("log.dohoff")); return false; }
        string script =
            "$ErrorActionPreference='Stop'\r\n" +
            "$doh='https://cloudflare-dns.com/dns-query'\r\n" +
            "foreach($ip in @('1.1.1.1','1.0.0.1')){try{Add-DnsClientDohServerAddress -ServerAddress $ip -DohTemplate $doh -AllowFallbackToUdp $true -AutoUpgrade $true -ErrorAction SilentlyContinue}catch{}}\r\n" +
            "$idx=(Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).InterfaceIndex\r\n" +
            "if(-not $idx){throw 'aktif ag arayuzu bulunamadi'}\r\n" +
            "Get-DnsClientServerAddress -AddressFamily IPv4 | Select-Object InterfaceIndex,InterfaceAlias,ServerAddresses | ConvertTo-Json -Compress | Set-Content -Encoding UTF8 '" + DnsBackupPath + "'\r\n" +
            "Set-DnsClientServerAddress -InterfaceIndex $idx -ServerAddresses 1.1.1.1,1.0.0.1\r\n" +
            "Clear-DnsClientCache\r\n" +
            "Write-Output 'OK'\r\n";
        try
        {
            int code;
            string o = Ps(script, out code);
            if (code != 0 || o.IndexOf("OK") < 0)
            {
                Log("DNS-over-HTTPS uygulanamadı (kod " + code + "). Yönlendirme moduna geçilecek.");
                if (o.Length > 0) Log("   " + FirstLines(o, 3));
                return false;
            }
            DohApplied = true;
            Log(Loc.T("log.dohon"));
            return true;
        }
        catch (Exception ex) { Log("DNS-over-HTTPS hatası: " + ex.Message); return false; }
    }

    public static void RestoreDns()
    {
        if (!File.Exists(DnsBackupPath)) return;
        string script =
            "$ErrorActionPreference='SilentlyContinue'\r\n" +
            "$b=Get-Content -Raw '" + DnsBackupPath + "' | ConvertFrom-Json\r\n" +
            "foreach($e in @($b)){\r\n" +
            "  if($e.ServerAddresses -and $e.ServerAddresses.Count -gt 0){Set-DnsClientServerAddress -InterfaceIndex $e.InterfaceIndex -ServerAddresses $e.ServerAddresses}\r\n" +
            "  else{Set-DnsClientServerAddress -InterfaceIndex $e.InterfaceIndex -ResetServerAddresses}\r\n" +
            "}\r\n" +
            "Clear-DnsClientCache\r\n" +
            "Remove-Item '" + DnsBackupPath + "' -Force\r\n" +
            "Write-Output 'OK'\r\n";
        try
        {
            int code;
            string o = Ps(script, out code);
            if (code == 0) Log("Önceki DNS ayarı geri yüklendi.");
            else Log("DNS ayarı geri yüklenemedi: " + FirstLines(o, 2));
        }
        catch (Exception ex) { Log("DNS geri yükleme hatası: " + ex.Message); }
        DohApplied = false;
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
        return new Profile[] {
            new Profile("Dengeli (site listesi ile)", "-9 --blacklist \"" + BlacklistPath + "\""),
            new Profile("Genel (-9, tüm trafik)", "-9"),
            new Profile("Uyumlu (-6)", "-6")
        };
    }

    class ProbeTarget
    {
        public string Host; public bool Must; public bool Ok;
        public ProbeTarget(string host, bool must) { Host = host; Must = must; }
    }

    // Test hedefleri: 2 zorunlu site + kullanıcının site listesindeki ilk 6 alan adı.
    // Böylece kullanıcı sorun yaşadığı siteyi listeye eklediğinde prob onu da ölçer.
    static ProbeTarget[] Targets()
    {
        List<ProbeTarget> list = new List<ProbeTarget>();
        list.Add(new ProbeTarget("www.google.com", true));
        list.Add(new ProbeTarget("www.youtube.com", true));

        string[] bl = ReadBlacklist().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int added = 0;
        for (int i = 0; i < bl.Length && added < 6; i++)
        {
            string h = bl[i].Trim();
            if (h.Length < 4 || h.StartsWith("#")) continue;
            bool dup = false;
            for (int j = 0; j < list.Count; j++)
                if (string.Equals(list[j].Host, h, StringComparison.OrdinalIgnoreCase)) dup = true;
            if (dup) continue;
            list.Add(new ProbeTarget(h, false));
            added++;
        }
        if (added == 0)
        {
            list.Add(new ProbeTarget("discord.com", false));
            list.Add(new ProbeTarget("www.instagram.com", false));
        }
        return list.ToArray();
    }

    static ProbeTarget[] ProbeAll()
    {
        FlushDns();
        ProbeTarget[] t = Targets();
        Thread[] ts = new Thread[t.Length];
        for (int i = 0; i < t.Length; i++)
        {
            ProbeTarget cur = t[i];
            ts[i] = new Thread(delegate () { cur.Ok = TlsOk(cur.Host, 5000); });
            ts[i].IsBackground = true;
            ts[i].Start();
        }
        for (int i = 0; i < ts.Length; i++) ts[i].Join(7000);
        return t;
    }

    static bool Pass(ProbeTarget[] t)
    {
        bool must = true, any = false;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i].Must) must = must && t[i].Ok;
            else any = any || t[i].Ok;
        }
        return must && any;
    }

    public static bool TlsOk(string host, int ms)
    {
        TcpClient tcp = null;
        try
        {
            tcp = new TcpClient();
            IAsyncResult ar = tcp.BeginConnect(host, 443, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(ms, false)) return false;
            tcp.EndConnect(ar);
            tcp.ReceiveTimeout = ms;
            tcp.SendTimeout = ms;
            using (SslStream ssl = new SslStream(tcp.GetStream(), false, CertOk))
            {
                // Tarayıcı gibi modern TLS iste. .NET varsayılanı TLS 1.0/1.1 olabiliyor;
                // sunucular bunu reddedince prob yanlış "engelli" sonucu veriyor.
                SslProtocols modern = SslProtocols.Tls12 | (SslProtocols)12288; // 12288 = Tls13
                try { ssl.AuthenticateAsClient(host, null, modern, false); }
                catch (ArgumentException) { ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false); }
                return ssl.IsAuthenticated;
            }
        }
        catch { return false; }
        finally { if (tcp != null) { try { tcp.Close(); } catch { } } }
    }

    // DNS önbelleği temizlenmezse test, servis kurulmadan önceki zehirli cevabı okur.
    public static void FlushDns()
    {
        try { string o; Run("ipconfig.exe", "/flushdns", out o); }
        catch { }
    }

    static bool CertOk(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
    {
        return errors == SslPolicyErrors.None;
    }

    static void LogProbe(ProbeTarget[] t)
    {
        for (int i = 0; i < t.Length; i++)
            Log("   " + t[i].Host + " = " + (t[i].Ok ? Loc.T("log.reach") : Loc.T("log.blocked")) + (t[i].Must ? Loc.T("log.req") : ""));
    }

    static string SelectProfile()
    {
        Profile[] ps = Profiles();
        for (int i = 0; i < ps.Length; i++)
        {
            Profile pr = ps[i];
            Log(Loc.T("log.trying", pr.Label));
            Process p = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ExePath, pr.Args + " " + dnsArgs);
                psi.WorkingDirectory = Path.GetDirectoryName(ExePath);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                p = Process.Start(psi);
                Thread.Sleep(2500);
                ProbeTarget[] t = ProbeAll();
                LogProbe(t);
                if (Pass(t)) return pr.Args;
                Log("   " + Loc.T("log.next"));
            }
            catch (Exception ex) { Log("   hata: " + ex.Message); }
            finally { if (p != null && !p.HasExited) { try { p.Kill(); } catch { } } }
        }
        return null;
    }

    // ---------------- kurulum ----------------

    public static bool Install()
    {
        lock (Gate)
        {
            string o;
            bool ok = false;
            try
            {
                Log("---- KURULUM BAŞLADI (v" + AppVersion + ") ----");
                EnsureBlacklist();
                SaveSettings();

                Log(Loc.T("log.step1"));
                string warn = ExtractPayload();
                if (Engine == "rust" && !RustEngineUsable)
                {
                    Log(Loc.T("log.enginefb"));
                    Engine = "goodbyedpi";
                }
                Log(Loc.T("log.engine", Engine == "rust" ? "Rust (dpifix-engine)" : "GoodbyeDPI"));
                if (warn != null) Log("   UYARI: " + warn);

                Log(Loc.T("log.step2"));
                StopService();
                Sc("stop " + LegacySvc, out o);
                Sc("delete " + LegacySvc, out o);

                Log(Loc.T("log.step3"));
                dnsArgs = "";
                if (!ApplyDoh())
                {
                    dnsArgs = "--dns-addr 77.88.8.8 --dns-port 1253 --dnsv6-addr 2a02:6b8::feed:0ff --dnsv6-port 1253";
                    Log(Loc.T("log.dir", "Yandex :1253"));
                }

                Log(Loc.T("log.step4"));
                string args = SelectProfile();

                if (args == null && DohApplied)
                {
                    Log("Hiçbir profil geçmedi. DNS-over-HTTPS geri alınıp yönlendirme ile tekrar denenecek.");
                    RestoreDns();
                    dnsArgs = "--dns-addr 77.88.8.8 --dns-port 1253 --dnsv6-addr 2a02:6b8::feed:0ff --dnsv6-port 1253";
                    args = SelectProfile();
                }
                if (args == null)
                {
                    args = Profiles()[0].Args;
                    Log(Loc.T("log.none"));
                }
                else Log(Loc.T("log.chosen", args));

                Log(Loc.T("log.step5"));
                Sc("delete " + SvcName, out o);
                Thread.Sleep(1200);
                string full = args + (dnsArgs.Length > 0 ? " " + dnsArgs : "");
                string line = "create " + SvcName + " binPath= \"\\\"" + ExePath + "\\\" " + full + "\"" +
                              " start= auto DisplayName= \"" + SvcDisplay + "\"";
                int rc = Sc(line, out o);
                if (rc != 0) { Thread.Sleep(1500); rc = Sc(line, out o); }
                if (rc != 0) { Log("   sc create başarısız: " + o); return false; }
                Sc("description " + SvcName + " \"" + SvcDesc + "\"", out o);
                Sc("failure " + SvcName + " reset= 86400 actions= restart/5000/restart/15000/restart/60000", out o);

                Log(Loc.T("log.step6"));
                Sc("start " + SvcName, out o);
                Thread.Sleep(1800);
                ok = State() == "running";
                Log("Durum: " + StateText());
                if (!ok) Log(Loc.T("log.svcfail", FirstLines(o, 3)));
            }
            catch (Exception ex) { Log("KURULUM HATASI: " + ex.Message); ok = false; }
            Log(ok ? Loc.T("log.done")
                   : Loc.T("log.failed"));
            return ok;
        }
    }

    public static void Uninstall()
    {
        lock (Gate)
        {
            string o;
            Log("---- KALDIRMA ----");
            StopService();
            Sc("delete " + SvcName, out o);
            Sc("stop " + LegacySvc, out o); Sc("delete " + LegacySvc, out o);
            Sc("stop WinDivert", out o); Sc("delete WinDivert", out o);
            Sc("stop WinDivert14", out o); Sc("delete WinDivert14", out o);
            KillStrays();
            RestoreDns();
            Thread.Sleep(700);
            try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true); Log("Dosyalar silindi: " + DataDir); }
            catch (Exception ex) { Log("Dosyalar silinemedi (kilitli olabilir): " + ex.Message); }
            Log("Durum: " + StateText());
        }
    }

    public static void Test()
    {
        Log(Loc.T("log.testhead", StateText()));
        ProbeTarget[] t = ProbeAll();
        LogProbe(t);
        if (State() != "running") Log("   " + Loc.T("log.testwarn"));
        else if (!Pass(t)) Log("   " + Loc.T("log.testfail"));
        else Log("   " + Loc.T("log.testok"));
    }

    // ---------------- güncelleme kontrolü ----------------

    public static void CheckUpdate()
    {
        Log(Loc.T("log.updscan"));
        Log(Loc.T("log.updcur", EngineVersion, AppVersion));
        try
        {
            WebClient wc = new WebClient();
            wc.Headers.Add("User-Agent", "DpiFix");
            wc.Encoding = Encoding.UTF8;
            string json = wc.DownloadString(UpstreamApi);
            Match m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success) { Log("   Sürüm bilgisi okunamadı."); return; }
            string tag = m.Groups[1].Value.Trim();
            Log(Loc.T("log.updnew", tag));
            if (tag.TrimStart('v', 'V') != EngineVersion)
                Log("   " + Loc.T("log.updhas") + " → https://github.com/ValdikSS/GoodbyeDPI/releases");
            else Log("   " + Loc.T("log.updnone"));
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
            if (Pass(t)) { Log(Loc.T("log.netok")); return; }
            LogProbe(t);
            Log(Loc.T("log.netbad"));
            Install();
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
        string qc;
        Sc("qc " + SvcName, out qc);
        Match bm = Regex.Match(qc, "BINARY_PATH_NAME\\s*:\\s*(.+)");
        sb.AppendLine("svcArgs=" + (bm.Success ? bm.Groups[1].Value.Trim() : "-"));
        sb.AppendLine("doh=" + DohEnabled + " autoreprobe=" + AutoReprobe + " verify=" + VerifyPayload);
        ProbeTarget[] t = ProbeAll();
        for (int i = 0; i < t.Length; i++)
            sb.AppendLine("net." + t[i].Host + "=" + (t[i].Ok ? "OK" : "ENGELLI") + (t[i].Must ? " (must)" : ""));
        sb.AppendLine("pass=" + Pass(t));
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
