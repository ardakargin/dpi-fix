// Setup.cs - DpiFix kurulum/kaldırma aracı (tek dosya kurulum)
// DpiFix.exe bu exe'nin içine gömülüdür. C# 5 uyumlu.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class SetupCore
{
    public const string AppName = "DPI Fix";
    public const string Pub = "DpiFix";
    public const string DisplayVer = "2.3.1";
    const string ResApp = "payload_app";

    public static string TargetDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DpiFix"); }
    }
    public static string TargetExe { get { return Path.Combine(TargetDir, "DpiFix.exe"); } }
    public static string TargetSetup { get { return Path.Combine(TargetDir, "Kurulum.exe"); } }

    public static string StartMenuDir
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                "Programs");
        }
    }

    public static Action<string> Sink;

    public static void Log(string s)
    {
        if (Sink != null) { try { Sink(s); } catch { } }
    }

    // ---- gömülü uygulamayı çıkar ----
    public static bool ExtractApp()
    {
        try
        {
            Directory.CreateDirectory(TargetDir);
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(ResApp))
            {
                if (s == null) { Log("HATA: gömülü uygulama bulunamadı."); return false; }
                byte[] buf = new byte[s.Length];
                int off = 0;
                while (off < buf.Length)
                {
                    int n = s.Read(buf, off, buf.Length - off);
                    if (n <= 0) break;
                    off += n;
                }
                File.WriteAllBytes(TargetExe, buf);
            }
            Log("Uygulama yazıldı: " + TargetExe);
            return true;
        }
        catch (Exception ex) { Log("HATA: " + ex.Message); return false; }
    }

    // ---- kısayol (WScript.Shell, harici bağımlılık yok) ----
    static void Shortcut(string lnkPath, string target, string args, string desc)
    {
        try
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object lnk = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, new object[] { lnkPath });
            Type t = lnk.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk,
                new object[] { Path.GetDirectoryName(target) });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { desc });
            if (args != null && args.Length > 0)
                t.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { args });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            Log("Kısayol: " + Path.GetFileName(lnkPath));
        }
        catch (Exception ex) { Log("Kısayol oluşturulamadı: " + ex.Message); }
    }

    static string UninstallKey { get { return @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DpiFix"; } }

    public static void WriteRegistry()
    {
        try
        {
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", DisplayVer);
                k.SetValue("Publisher", Pub);
                k.SetValue("DisplayIcon", TargetExe);
                k.SetValue("InstallLocation", TargetDir);
                k.SetValue("NoModify", 1);
                k.SetValue("NoRepair", 1);
                k.SetValue("UninstallString", "\"" + TargetSetup + "\" --uninstall");
                k.SetValue("EstimatedSize", 1200, RegistryValueKind.DWord);
            }
            Log("Program Ekle/Kaldır kaydı oluşturuldu.");
        }
        catch (Exception ex) { Log("Kayıt yazılamadı: " + ex.Message); }
    }

    // ---- uygulamanın kendi günlüğünü izle ----
    static long LogMark()
    {
        try
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "DpiFix", "log.txt");
            return File.Exists(p) ? new FileInfo(p).Length : 0;
        }
        catch { return 0; }
    }

    static void LogTail(long from)
    {
        try
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "DpiFix", "log.txt");
            if (!File.Exists(p)) return;
            using (FileStream fs = File.Open(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (from > fs.Length) from = 0;
                fs.Seek(from, SeekOrigin.Begin);
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null) Log("   " + line);
                }
            }
        }
        catch { }
    }

    public static bool Install(bool runServiceInstall, bool tryDoh = false)
    {
        Log("---- KURULUM ----");
        if (!ExtractApp()) return false;

        try { File.Copy(Assembly.GetExecutingAssembly().Location, TargetSetup, true); Log("Kurulum aracı kopyalandı."); }
        catch (Exception ex) { Log("Kurulum aracı kopyalanamadı: " + ex.Message); }

        Shortcut(Path.Combine(StartMenuDir, AppName + ".lnk"), TargetExe, "", AppName + " - DPI/DNS zorlamasi kaldirici");
        Shortcut(Path.Combine(StartMenuDir, AppName + " Kaldir.lnk"), TargetSetup, "--uninstall", AppName + " kaldir");

        WriteRegistry();

        if (runServiceInstall)
        {
            Log("Servis kuruluyor (birkaç saniye)...");
            long mark = LogMark();
            int code = RunExe(TargetExe, tryDoh ? "--install --try-doh" : "--install");
            LogTail(mark);
            if (code != 0)
            {
                Log("Uygulama kuruldu. Bu bağlantıda doğrulanmış bir servis profili etkinleştirilemedi (kod " + code + ").");
                return false;
            }
            Log("Servis kuruldu ve başlatıldı.");
        }

        Log("---- Kurulum tamam ----");
        return true;
    }

    public static void Uninstall()
    {
        Log("---- KALDIRMA ----");
        try
        {
            if (File.Exists(TargetExe))
            {
                long mark = LogMark();
                int result = RunExe(TargetExe, "--uninstall");
                LogTail(mark);
                if (result != 0) throw new InvalidOperationException("DPI Fix service removal failed.");
            }
        }
        catch (Exception ex)
        {
            Log("Servis kaldırma hatası: " + ex.Message);
            throw;
        }

        DeleteShortcut(Path.Combine(StartMenuDir, AppName + ".lnk"));
        DeleteShortcut(Path.Combine(StartMenuDir, AppName + " Kaldir.lnk"));

        try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); Log("Kayıt silindi."); }
        catch (Exception ex) { Log("Kayıt silinemedi: " + ex.Message); }

        Log("---- Kaldırıldı. Klasör arka planda temizlenecek. ----");
        SelfDeleteDir();
    }

    static void DeleteShortcut(string p)
    {
        try { if (File.Exists(p)) { File.Delete(p); Log("Kısayol silindi: " + Path.GetFileName(p)); } }
        catch { }
    }

    // Kendi klasörünü silmek için: cmd'ye gecikmeli rmdir yaptır, sonra çık.
    static void SelfDeleteDir()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                "/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"" + TargetDir + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
        }
        catch { }
    }

    static int RunExe(string file, string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process p = Process.Start(psi);
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex) { Log("Çalıştırılamadı: " + ex.Message); return -1; }
    }

    public static bool Installed()
    {
        return File.Exists(TargetExe);
    }
}

class SetupForm : Form
{
    TextBox log;
    Button btnGo;
    CheckBox chkService, chkDoh;
    Label status;

    public SetupForm()
    {
        Text = "DPI Fix Kurulumu";
        ClientSize = new Size(560, 448);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        Label title = new Label();
        title.Text = "DPI Fix Kurulumu";
        title.Font = new Font("Segoe UI Semibold", 14F);
        title.Location = new Point(16, 14);
        title.AutoSize = true;
        Controls.Add(title);

        Label sub = new Label();
        sub.Text = "Türkiye için DPI/DNS zorlamasını kaldırır.\r\nKurulum yeri: " + SetupCore.TargetDir;
        sub.ForeColor = Color.Gray;
        sub.Location = new Point(18, 48);
        sub.AutoSize = true;
        Controls.Add(sub);

        status = new Label();
        status.Font = new Font("Segoe UI Semibold", 9F);
        status.Location = new Point(18, 92);
        status.AutoSize = true;
        status.Text = SetupCore.Installed() ? "Durum: kurulu" : "Durum: kurulu değil";
        status.ForeColor = SetupCore.Installed() ? Color.SeaGreen : Color.DimGray;
        Controls.Add(status);

        chkService = new CheckBox();
        chkService.Text = "Kurulumdan sonra servisi hemen kur ve başlat";
        chkService.Checked = true;
        chkService.Location = new Point(18, 116);
        chkService.AutoSize = true;
        Controls.Add(chkService);

        chkDoh = new CheckBox();
        chkDoh.Text = "Gerekirse Windows DNS-over-HTTPS ayarını dene";
        chkDoh.Location = new Point(18, 140);
        chkDoh.AutoSize = true;
        Controls.Add(chkDoh);
        chkService.CheckedChanged += delegate { chkDoh.Enabled = chkService.Checked; };

        btnGo = new Button();
        btnGo.Text = SetupCore.Installed() ? "Yeniden Kur" : "Kur";
        btnGo.Location = new Point(18, 172);
        btnGo.Size = new Size(120, 32);
        btnGo.Click += ClickGo;
        Controls.Add(btnGo);

        Button btnRemove = new Button();
        btnRemove.Text = "Kaldır";
        btnRemove.Location = new Point(146, 172);
        btnRemove.Size = new Size(90, 32);
        btnRemove.Click += ClickRemove;
        Controls.Add(btnRemove);

        Button btnFolder = new Button();
        btnFolder.Text = "Klasörü Aç";
        btnFolder.Location = new Point(244, 172);
        btnFolder.Size = new Size(100, 32);
        btnFolder.Click += delegate { try { Process.Start("explorer.exe", SetupCore.TargetDir); } catch { } };
        Controls.Add(btnFolder);

        log = new TextBox();
        log.Multiline = true;
        log.ReadOnly = true;
        log.ScrollBars = ScrollBars.Vertical;
        log.BackColor = Color.FromArgb(250, 250, 250);
        log.Font = new Font("Consolas", 8.5F);
        log.Location = new Point(18, 216);
        log.Size = new Size(524, 212);
        Controls.Add(log);

        SetupCore.Sink = Log;
        Log("Hazır. 'Kur' ile kurulum başlar (yönetici yetkisi gerekir).");
    }

    void Log(string s)
    {
        if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { Log(s); }); return; }
        log.AppendText(s + "\r\n");
        log.SelectionStart = log.TextLength;
        log.ScrollToCaret();
    }

    void ClickGo(object s, EventArgs e)
    {
        btnGo.Enabled = false;
        Cursor = Cursors.WaitCursor;
        bool svc = chkService.Checked;
        bool tryDoh = chkDoh.Checked;
        ThreadPool.QueueUserWorkItem(delegate
        {
            bool ok = false;
            try { ok = SetupCore.Install(svc, tryDoh); }
            catch (Exception ex) { Log("HATA: " + ex.Message); }
            BeginInvoke((MethodInvoker)delegate
            {
                btnGo.Enabled = true;
                Cursor = Cursors.Default;
                bool installed = SetupCore.Installed();
                status.Text = installed ? (ok ? "Durum: kurulu" : "Uygulama kurulu, servis kurulumu tamamlanmadı") : "Durum: kurulu değil";
                status.ForeColor = installed && ok ? Color.SeaGreen : Color.DimGray;
                if (ok && MessageBox.Show("Kurulum tamamlandı. Uygulama şimdi açılsın mı?", "DPI Fix",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    try { Process.Start(SetupCore.TargetExe); } catch { }
                }
            });
        });
    }

    void ClickRemove(object s, EventArgs e)
    {
        if (MessageBox.Show("DPI Fix kaldırılsın mı? (servis, DNS ayarı ve dosyalar geri alınır)",
            "Kaldır", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        btnGo.Enabled = false;
        ThreadPool.QueueUserWorkItem(delegate
        {
            bool removed = false;
            try { SetupCore.Uninstall(); removed = true; }
            catch (Exception ex) { Log("HATA: " + ex.Message); }
            BeginInvoke((MethodInvoker)delegate
            {
                btnGo.Enabled = true;
                status.Text = removed ? "Durum: kurulu değil" : "Kaldırma tamamlanamadı";
                status.ForeColor = removed ? Color.DimGray : Color.IndianRed;
            });
        });
    }
}

static class SetupProgram
{
    [STAThread]
    static void Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (cmd == "--uninstall")
        {
            SetupCore.Sink = delegate (string s) { try { Console.WriteLine(s); } catch { } };
            try { SetupCore.Uninstall(); }
            catch { Environment.ExitCode = 1; }
            return;
        }
        if (cmd == "--install")
        {
            SetupCore.Sink = delegate (string s) { try { Console.WriteLine(s); } catch { } };
            Environment.ExitCode = SetupCore.Install(true, Array.IndexOf(args, "--try-doh") >= 0) ? 0 : 1;
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
    }
}
