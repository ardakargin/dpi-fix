// DpiFix.cs - arayüz (koyu tema) + komut satırı
// Arayüz metinleri Ui.cs içindeki Loc ile TR/EN yerelleştirilir.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

/// Kalkan logosu
class LogoBox : Control
{
    public LogoBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(34, 34);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Width - 2, h = Height - 2;
        PointF[] pts = new PointF[] {
            new PointF(1 + w / 2, 1),
            new PointF(1 + w, 1 + h * 0.22f),
            new PointF(1 + w * 0.86f, 1 + h * 0.70f),
            new PointF(1 + w / 2, 1 + h),
            new PointF(1 + w * 0.14f, 1 + h * 0.70f),
            new PointF(1, 1 + h * 0.22f)
        };
        using (LinearGradientBrush b = new LinearGradientBrush(
            new Rectangle(0, 0, Width, Height), Theme.Accent, Color.FromArgb(0x38, 0xBD, 0xF8), 90f))
            g.FillPolygon(b, pts);
        using (Pen p = new Pen(Color.FromArgb(0x04, 0x25, 0x2B), 1.6f))
            g.DrawPolygon(p, pts);
    }
}

class MainForm : Form
{
    TextBox log;
    Label statusDot, statusText, valService, valEngine, valProfile, valSites;
    Label secStatus, secActions, secSettings, secLog, lblService, lblEngine, lblProfile, lblSites;
    Label title, sub, hint1, hint2;
    FlatButton btnInstall, btnStop, btnRemove, btnTest, btnList, btnUpdate, btnLog, btnLang;
    FlatCheck chkDoh, chkWatch, chkVerify;
    System.Windows.Forms.Timer timer;
    bool busy = false;

    public MainForm()
    {
        Text = "DPI Fix";
        ClientSize = new Size(780, 700);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        Font = Theme.Ui(9f, FontStyle.Regular);
        DoubleBuffered = true;

        // ---- başlık ----
        LogoBox logo = new LogoBox();
        logo.Location = new Point(24, 22);
        Controls.Add(logo);

        title = new Label();
        title.Font = Theme.Ui(13f, FontStyle.Bold);
        title.ForeColor = Theme.Fg;
        title.Location = new Point(68, 18);
        title.AutoSize = true;
        Controls.Add(title);

        sub = new Label();
        sub.Font = Theme.Ui(8.5f, FontStyle.Regular);
        sub.ForeColor = Theme.Muted;
        sub.Location = new Point(69, 43);
        sub.AutoSize = true;
        Controls.Add(sub);

        btnLang = new FlatButton();
        btnLang.Size = new Size(64, 30);
        btnLang.Location = new Point(692, 26);
        btnLang.Font = Theme.Ui(9f, FontStyle.Bold);
        btnLang.TextColor = Theme.Accent;
        btnLang.BorderColor = Color.FromArgb(0x25, 0x45, 0x48);
        btnLang.FillColor = Theme.AccentDark;
        btnLang.HoverColor = Color.FromArgb(0x12, 0x4C, 0x47);
        btnLang.Click += ClickLang;
        Controls.Add(btnLang);

        Panel line = new Panel();
        line.Location = new Point(24, 72);
        line.Size = new Size(732, 1);
        line.BackColor = Theme.Border;
        Controls.Add(line);

        // ---- durum kartı ----
        RoundedPanel card = new RoundedPanel();
        card.Location = new Point(24, 88);
        card.Size = new Size(732, 104);
        Controls.Add(card);

        statusDot = new Label();
        statusDot.Font = Theme.Ui(15f, FontStyle.Regular);
        statusDot.Location = new Point(18, 12);
        statusDot.AutoSize = true;
        card.Controls.Add(statusDot);

        statusText = new Label();
        statusText.Font = Theme.Ui(12f, FontStyle.Bold);
        statusText.Location = new Point(42, 16);
        statusText.AutoSize = true;
        card.Controls.Add(statusText);

        secStatus = SectionLabel(card, 640, 20);

        lblService = KVLabel(card, 18);
        valService = KVValue(card, 18);
        lblEngine = KVLabel(card, 240);
        valEngine = KVValue(card, 240);
        lblProfile = KVLabel(card, 420);
        valProfile = KVValue(card, 420);
        lblSites = KVLabel(card, 600);
        valSites = KVValue(card, 600);

        // ---- işlemler ----
        secActions = SectionLabel(this, 24, 204);

        btnInstall = MkBtn(24, 222, 150, 38, true);
        btnInstall.Click += ClickInstall;
        btnStop = MkBtn(182, 222, 104, 38, false);
        btnStop.Click += ClickStop;
        btnRemove = MkBtn(294, 222, 104, 38, false);
        btnRemove.Click += ClickRemove;

        btnTest = MkBtn(24, 268, 140, 34, false);
        btnTest.Click += ClickTest;
        btnList = MkBtn(172, 268, 118, 34, false);
        btnList.Click += ClickList;
        btnUpdate = MkBtn(298, 268, 110, 34, false);
        btnUpdate.Click += ClickUpdate;
        btnLog = MkBtn(416, 268, 110, 34, false);
        btnLog.Click += ClickOpenLog;

        // ---- ayarlar ----
        secSettings = SectionLabel(this, 24, 316);

        chkDoh = MkCheck(24, 334);
        chkWatch = MkCheck(24, 358);
        chkVerify = MkCheck(24, 382);

        // ---- günlük ----
        secLog = SectionLabel(this, 24, 412);

        RoundedPanel logCard = new RoundedPanel();
        logCard.Location = new Point(24, 430);
        logCard.Size = new Size(732, 200);
        logCard.FillColor = Theme.CardAlt;
        logCard.Padding = new Padding(12, 10, 12, 10);
        Controls.Add(logCard);

        log = new TextBox();
        log.Multiline = true;
        log.ReadOnly = true;
        log.BorderStyle = BorderStyle.None;
        log.ScrollBars = ScrollBars.Vertical;
        log.BackColor = Theme.CardAlt;
        log.ForeColor = Color.FromArgb(0xA8, 0xBD, 0xD0);
        log.Font = Theme.Mono(8.5f);
        log.Dock = DockStyle.Fill;
        logCard.Controls.Add(log);

        hint1 = new Label();
        hint1.Font = Theme.Ui(8.25f, FontStyle.Regular);
        hint1.ForeColor = Theme.Dim;
        hint1.Location = new Point(24, 640);
        hint1.AutoSize = true;
        Controls.Add(hint1);

        hint2 = new Label();
        hint2.Font = Theme.Ui(8.25f, FontStyle.Regular);
        hint2.ForeColor = Theme.Dim;
        hint2.Location = new Point(24, 660);
        hint2.AutoSize = true;
        Controls.Add(hint2);

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 1500;
        timer.Tick += delegate { RefreshStatus(); };
        timer.Start();

        LoadUiFromSettings();
        ApplyLanguage();
        RefreshStatus();
        LogText("DPI Fix v" + Core.AppVersion + " · GoodbyeDPI " + Core.EngineVersion);
        LogText(Loc.T("hint.ready"));
    }

    // ---------- yardımcılar ----------

    Label SectionLabel(Control parent, int x, int y)
    {
        Label l = new Label();
        l.Font = Theme.Ui(7.75f, FontStyle.Bold);
        l.ForeColor = Theme.Dim;
        l.Location = new Point(x, y);
        l.AutoSize = true;
        parent.Controls.Add(l);
        return l;
    }

    Label KVLabel(Control parent, int x)
    {
        Label l = new Label();
        l.Font = Theme.Ui(7.75f, FontStyle.Regular);
        l.ForeColor = Theme.Dim;
        l.Location = new Point(x, 56);
        l.AutoSize = true;
        parent.Controls.Add(l);
        return l;
    }

    Label KVValue(Control parent, int x)
    {
        Label l = new Label();
        l.Font = Theme.Ui(8.5f, FontStyle.Regular);
        l.ForeColor = Theme.Fg;
        l.Location = new Point(x, 72);
        l.AutoSize = true;
        parent.Controls.Add(l);
        return l;
    }

    FlatButton MkBtn(int x, int y, int w, int h, bool primary)
    {
        FlatButton b = new FlatButton();
        b.Location = new Point(x, y);
        b.Size = new Size(w, h);
        if (primary)
        {
            b.FillColor = Theme.Accent;
            b.HoverColor = Color.FromArgb(0x4F, 0xE3, 0xD0);
            b.TextColor = Color.FromArgb(0x04, 0x25, 0x2B);
            b.BorderColor = Color.Empty;
            b.Font = Theme.Ui(10f, FontStyle.Bold);
        }
        Controls.Add(b);
        return b;
    }

    FlatCheck MkCheck(int x, int y)
    {
        FlatCheck c = new FlatCheck();
        c.Location = new Point(x, y);
        c.CheckedChanged += delegate { SyncSettingsFromUi(); };
        Controls.Add(c);
        return c;
    }

    void SyncSettingsFromUi()
    {
        if (loading) return;   // ayarlar yüklenirken yazma
        Core.DohEnabled = chkDoh.Checked;
        Core.AutoReprobe = chkWatch.Checked;
        Core.VerifyPayload = chkVerify.Checked;
        Core.SaveSettings();
    }

    bool loading = false;

    void LoadUiFromSettings()
    {
        loading = true;
        chkDoh.Checked = Core.DohEnabled;
        chkWatch.Checked = Core.AutoReprobe;
        chkVerify.Checked = Core.VerifyPayload;
        loading = false;
    }

    void ApplyLanguage()
    {
        title.Text = Loc.T("app.title");
        sub.Text = Loc.T("app.sub", Core.EngineVersion);
        secStatus.Text = Loc.T("sec.status");
        secActions.Text = Loc.T("sec.actions");
        secSettings.Text = Loc.T("sec.settings");
        secLog.Text = Loc.T("sec.log");

        lblService.Text = Loc.T("lbl.service");
        lblEngine.Text = Loc.T("lbl.engine");
        lblProfile.Text = Loc.T("lbl.profile");
        lblSites.Text = Loc.T("lbl.sites");

        btnInstall.Text = Loc.T("btn.install");
        btnStop.Text = Loc.T("btn.stop");
        btnRemove.Text = Loc.T("btn.remove");
        btnTest.Text = Loc.T("btn.test");
        btnList.Text = Loc.T("btn.list");
        btnUpdate.Text = Loc.T("btn.update");
        btnLog.Text = Loc.T("btn.log");
        btnLang.Text = Loc.Lang == "tr" ? "EN" : "TR";

        chkDoh.Text = Loc.T("chk.doh");
        chkWatch.Text = Loc.T("chk.watch");
        chkVerify.Text = Loc.T("chk.verify");

        hint1.Text = Loc.T("hint.tip");
        hint2.Text = Loc.T("hint.log", Core.LogPath);
        RefreshStatus();
    }

    void ClickLang(object s, EventArgs e)
    {
        Loc.Lang = Loc.Lang == "tr" ? "en" : "tr";
        Core.Lang = Loc.Lang;
        Core.SaveSettings();
        ApplyLanguage();
        LogText("── " + Loc.T("lang.name") + " ──");
    }

    void RefreshStatus()
    {
        string st = Core.State();
        string txt = Loc.T("st." + st);
        Color c = st == "running" ? Theme.Ok : (st == "absent" ? Theme.Dim : Theme.Danger);

        statusDot.Text = "●";
        statusDot.ForeColor = c;
        statusText.Text = txt;
        statusText.ForeColor = c;

        valService.Text = txt;
        valService.ForeColor = c;
        valEngine.Text = "GoodbyeDPI " + Core.EngineVersion;
        valProfile.Text = Core.CurrentArgsShort();
        valSites.Text = Loc.T("lbl.entries", Core.BlacklistCount());

        if (!busy) btnStop.Enabled = st == "running";
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
        btnLog.Enabled = !b;
        btnLang.Enabled = !b;
        Cursor = b ? Cursors.WaitCursor : Cursors.Default;
    }

    void LogText(string s)
    {
        if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { LogText(s); }); return; }
        log.AppendText(s + "\r\n");
        log.SelectionStart = log.TextLength;
        log.ScrollToCaret();
    }

    void RunAsync(string titleKey, Action work)
    {
        SyncSettingsFromUi();
        SetBusy(true);
        LogText("");
        LogText("── " + Loc.T(titleKey) + " ──");
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { work(); }
            catch (Exception ex) { LogText("HATA: " + ex.Message); }
            finally { BeginInvoke((MethodInvoker)delegate { SetBusy(false); RefreshStatus(); }); }
        });
    }

    void ClickInstall(object s, EventArgs e) { RunAsync("run.install", delegate { Core.Install(); }); }

    void ClickStop(object s, EventArgs e)
    {
        RunAsync("run.stop", delegate
        {
            Core.StopService();
            Core.Log(Loc.T("st." + Core.State()));
        });
    }

    void ClickRemove(object s, EventArgs e)
    {
        if (MessageBox.Show(Loc.T("confirm.remove"), Loc.T("confirm.title"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        RunAsync("run.remove", delegate { Core.Uninstall(); });
    }

    void ClickTest(object s, EventArgs e) { RunAsync("run.test", delegate { Core.Test(); }); }

    void ClickUpdate(object s, EventArgs e) { RunAsync("run.update", delegate { Core.CheckUpdate(); }); }

    void ClickList(object s, EventArgs e)
    {
        try
        {
            Core.EnsureBlacklist();
            Process.Start("notepad.exe", "\"" + Core.BlacklistPath + "\"");
            Core.Log(Core.BlacklistPath);
        }
        catch (Exception ex) { Core.Log(ex.Message); }
    }

    void ClickOpenLog(object s, EventArgs e)
    {
        try { Process.Start("explorer.exe", "/select,\"" + Core.LogPath + "\""); }
        catch (Exception ex) { Core.Log(ex.Message); }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Core.Sink = LogText;
        Core.StartNetworkWatch();
    }

    // Başlık çubuğunu koyu temaya çevir (Windows 10 20H1+ / 11)
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, 4);
            int caption = 0x0017110B;   // COLORREF 0x00BBGGRR = #0B1117
            DwmSetWindowAttribute(Handle, 35, ref caption, 4);
            int text = 0x00F5EDE6;
            DwmSetWindowAttribute(Handle, 36, ref text, 4);
        }
        catch { }
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
        Loc.Lang = Core.Lang;

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
            else if (cmd == "--status") { Console.WriteLine(Core.StateText()); Environment.Exit(0); }
            else if (cmd == "--test") { Core.Test(); Environment.Exit(0); }
            else if (cmd == "--update") { Core.CheckUpdate(); Environment.Exit(0); }
            else
            {
                Console.WriteLine("Kullanım: DpiFix.exe [--install|--uninstall|--status|--test|--update|--selftest]");
                Environment.Exit(2);
            }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}