// DpiFix.cs - GUI + komut satırı arayüzü
// Tek dosya dağıtım: motor (goodbyedpi + WinDivert) bu exe'nin içinde gömülüdür.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

class MainForm : Form
{
    TextBox log;
    Label status;
    Button btnInstall, btnStop, btnRemove, btnTest, btnList, btnUpdate;
    CheckBox chkDoh, chkWatch, chkVerify;
    System.Windows.Forms.Timer timer;
    bool busy = false;

    public MainForm()
    {
        Text = "DPI Fix v" + Core.AppVersion;
        ClientSize = new Size(660, 566);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        Label title = new Label();
        title.Text = "DPI / DNS Zorlaması Kaldırıcı";
        title.Font = new Font("Segoe UI Semibold", 13F);
        title.Location = new Point(16, 12);
        title.AutoSize = true;
        Controls.Add(title);

        Label sub = new Label();
        sub.Text = "GoodbyeDPI " + Core.EngineVersion + " motoru • tek yapı • otomatik yapılandırma • kalıcı servis";
        sub.ForeColor = Color.Gray;
        sub.Location = new Point(18, 44);
        sub.AutoSize = true;
        Controls.Add(sub);

        status = new Label();
        status.Font = new Font("Segoe UI Semibold", 10F);
        status.Location = new Point(18, 72);
        status.AutoSize = true;
        Controls.Add(status);

        btnInstall = MkButton("Kur / Onar", 18, 102, 110, ClickInstall);
        btnStop = MkButton("Durdur", 136, 102, 90, ClickStop);
        btnRemove = MkButton("Kaldır", 234, 102, 90, ClickRemove);
        btnTest = MkButton("Bağlantı Testi", 18, 140, 120, ClickTest);
        btnList = MkButton("Site Listesi", 146, 140, 110, ClickList);
        btnUpdate = MkButton("Güncelleme", 264, 140, 110, ClickUpdate);

        chkDoh = MkCheck("DNS-over-HTTPS (Cloudflare)", 18, 182);
        chkWatch = MkCheck("Ağ değişince otomatik yeniden seç", 250, 182);
        chkVerify = MkCheck("Bütünlük doğrulaması", 18, 206);

        log = new TextBox();
        log.Multiline = true;
        log.ReadOnly = true;
        log.ScrollBars = ScrollBars.Vertical;
        log.BackColor = Color.FromArgb(250, 250, 250);
        log.Font = new Font("Consolas", 8.5F);
        log.Location = new Point(18, 236);
        log.Size = new Size(624, 314);
        Controls.Add(log);

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 2000;
        timer.Tick += delegate { RefreshStatus(); };
        timer.Start();

        RefreshStatus();
        Log("Hazır. 'Kur / Onar' ile tek tuşla kurulum yapılır.");
        Log("Kurulumdan sonra bu pencereyi kapatsanız da servis çalışmaya devam eder.");
        Log("Günlük dosyası: " + Core.LogPath);
    }

    Button MkButton(string text, int x, int y, int w, EventHandler h)
    {
        Button b = new Button();
        b.Text = text;
        b.Location = new Point(x, y);
        b.Size = new Size(w, 30);
        b.Click += h;
        Controls.Add(b);
        return b;
    }

    CheckBox MkCheck(string text, int x, int y)
    {
        CheckBox c = new CheckBox();
        c.Text = text;
        c.Location = new Point(x, y);
        c.AutoSize = true;
        c.CheckedChanged += ClickSetting;
        Controls.Add(c);
        return c;
    }

    void SyncSettingsFromUi()
    {
        Core.DohEnabled = chkDoh.Checked;
        Core.AutoReprobe = chkWatch.Checked;
        Core.VerifyPayload = chkVerify.Checked;
        Core.SaveSettings();
    }

    void ClickSetting(object s, EventArgs e) { SyncSettingsFromUi(); }

    void LoadUiFromSettings()
    {
        chkDoh.Checked = Core.DohEnabled;
        chkWatch.Checked = Core.AutoReprobe;
        chkVerify.Checked = Core.VerifyPayload;
    }

    void RefreshStatus()
    {
        status.Text = "Servis durumu: " + Core.StateText();
        status.ForeColor = Core.State() == "running" ? Color.SeaGreen : Color.Firebrick;
        if (!busy) btnStop.Enabled = Core.State() == "running";
    }

    void SetBusy(bool b)
    {
        busy = b;
        btnInstall.Enabled = !b;
        btnStop.Enabled = !b && Core.State() == "running";
        btnRemove.Enabled = !b;
        btnTest.Enabled = !b;
        btnList.Enabled = !b;
        btnUpdate.Enabled = !b;
        Cursor = b ? Cursors.WaitCursor : Cursors.Default;
    }

    void Log(string s)
    {
        if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { Log(s); }); return; }
        log.AppendText(s + "\r\n");
        log.SelectionStart = log.TextLength;
        log.ScrollToCaret();
    }

    void RunAsync(string title, Action work)
    {
        SyncSettingsFromUi();
        SetBusy(true);
        Log("");
        Log("=== " + title + " ===");
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { work(); }
            catch (Exception ex) { Log("HATA: " + ex.Message); }
            finally { BeginInvoke((MethodInvoker)delegate { SetBusy(false); RefreshStatus(); }); }
        });
    }

    void ClickInstall(object s, EventArgs e)
    {
        RunAsync("KURULUM / ONARIM", delegate { Core.Install(); });
    }

    void ClickStop(object s, EventArgs e)
    {
        RunAsync("SERVİS DURDURULUYOR", delegate
        {
            Core.StopService();
            Core.Log("Durum: " + Core.StateText());
        });
    }

    void ClickRemove(object s, EventArgs e)
    {
        if (MessageBox.Show("Servis, DNS ayarı ve kopyalanan dosyalar geri alınacak. Onaylıyor musunuz?",
            "Kaldır", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        RunAsync("KALDIRMA", delegate { Core.Uninstall(); });
    }

    void ClickTest(object s, EventArgs e)
    {
        RunAsync("BAĞLANTI TESTİ", delegate { Core.Test(); });
    }

    void ClickList(object s, EventArgs e)
    {
        try
        {
            Core.EnsureBlacklist();
            Process.Start("notepad.exe", "\"" + Core.BlacklistPath + "\"");
            Core.Log("Site listesi açıldı: " + Core.BlacklistPath);
            Core.Log("Değişiklikten sonra 'Kur / Onar' basın (servise yeniden yazılır).");
        }
        catch (Exception ex) { Core.Log("Liste açılamadı: " + ex.Message); }
    }

    void ClickUpdate(object s, EventArgs e)
    {
        RunAsync("GÜNCELLEME KONTROLÜ", delegate { Core.CheckUpdate(); });
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LoadUiFromSettings();
        Core.StartNetworkWatch();
    }
}

static class Program
{
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    static void Main(string[] args)
    {
        Core.LoadSettings();

        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (cmd == "--selftest") { Core.SelfTest(); return; }

        if (cmd.Length > 0)
        {
            AttachConsole(-1);
            try
            {
                StreamWriter sw = new StreamWriter(Console.OpenStandardOutput());
                sw.AutoFlush = true;
                Console.SetOut(sw);
            }
            catch { }
            Core.Sink = delegate (string s) { try { Console.WriteLine(s); } catch { } };

            if (cmd == "--install") { bool ok = Core.Install(); Environment.Exit(ok ? 0 : 1); }
            else if (cmd == "--uninstall") { Core.Uninstall(); Environment.Exit(0); }
            else if (cmd == "--status") { Console.WriteLine(Core.State() + " (" + Core.StateText() + ")"); Environment.Exit(0); }
            else if (cmd == "--test") { Core.Test(); Environment.Exit(0); }
            else if (cmd == "--update") { Core.CheckUpdate(); Environment.Exit(0); }
            else { Console.WriteLine("Kullanım: DpiFix.exe [--install|--uninstall|--status|--test|--update|--selftest]"); Environment.Exit(2); }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
