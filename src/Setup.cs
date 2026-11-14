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
    public const string DisplayVer = Core.AppVersion;
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

    static void PublishFile(string staged, string destination, string backup)
    {
        if (File.Exists(destination)) File.Replace(staged, destination, backup);
        else File.Move(staged, destination);
    }

    static bool RunExclusive(Func<bool> operation)
    {
        using (Mutex mutex = new Mutex(false, @"Global\DpiFix.Setup"))
        {
            bool entered;
            try { entered = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) throw new InvalidOperationException(Loc.T("setup.busy"));
            try { return operation(); }
            finally { mutex.ReleaseMutex(); }
        }
    }

    static void InstallFiles()
    {
        Directory.CreateDirectory(TargetDir);
        string token = "." + Guid.NewGuid().ToString("N");
        string appStage = TargetExe + token + ".tmp";
        string setupStage = TargetSetup + token + ".tmp";
        string previousApp = TargetExe + token + ".bak";
        string previousSetup = TargetSetup + token + ".bak";
        bool appPublished = false, setupPublished = false;
        bool sameSetup = String.Equals(Assembly.GetExecutingAssembly().Location, TargetSetup, StringComparison.OrdinalIgnoreCase);
        try
        {
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResApp))
            {
                if (resource == null) throw new InvalidDataException(Loc.T("setup.missing"));
                using (FileStream output = File.Create(appStage)) resource.CopyTo(output);
            }
            if (!sameSetup)
            {
                File.Copy(Assembly.GetExecutingAssembly().Location, setupStage);
                File.SetAttributes(setupStage, FileAttributes.Normal);
            }
            PublishFile(appStage, TargetExe, previousApp);
            appPublished = true;
            if (!sameSetup)
            {
                PublishFile(setupStage, TargetSetup, previousSetup);
                setupPublished = true;
            }
            WriteRegistry();
        }
        catch
        {
            if (setupPublished) RollbackFile(TargetSetup, previousSetup);
            if (appPublished) RollbackFile(TargetExe, previousApp);
            throw;
        }
        finally
        {
            DeleteTemporaryFile(appStage);
            DeleteTemporaryFile(setupStage);
        }
        DeleteTemporaryFile(previousApp);
        DeleteTemporaryFile(previousSetup);
    }

    static void RollbackFile(string path, string backup)
    {
        if (File.Exists(backup)) File.Replace(backup, path, null);
        else File.Delete(path);
    }

    static void DeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException ex) { Log(ex.Message); }
        catch (UnauthorizedAccessException ex) { Log(ex.Message); }
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
            Log(Loc.T("setup.shortcut", Path.GetFileName(lnkPath)));
        }
        catch (Exception ex) { Log(Loc.T("setup.error", ex.Message)); }
    }

    static string UninstallKey { get { return @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DpiFix"; } }

    public static void WriteRegistry()
    {
        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", DisplayVer);
            key.SetValue("Publisher", Pub);
            key.SetValue("DisplayIcon", TargetExe);
            key.SetValue("InstallLocation", TargetDir);
            key.SetValue("NoModify", 1);
            key.SetValue("NoRepair", 1);
            key.SetValue("UninstallString", "\"" + TargetSetup + "\" --uninstall");
            key.SetValue("EstimatedSize", 1200, RegistryValueKind.DWord);
        }
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
        return RunExclusive(delegate { return InstallCore(runServiceInstall, tryDoh); });
    }

    static bool InstallCore(bool runServiceInstall, bool tryDoh)
    {
        if (!Core.SupportedWindows()) throw new PlatformNotSupportedException(Loc.T("setup.arch"));
        Log(Loc.T("setup.installing"));
        InstallFiles();
        Core.LoadSettings();
        Core.Lang = Loc.Lang;
        Core.SaveSettings();

        Shortcut(Path.Combine(StartMenuDir, AppName + ".lnk"), TargetExe, "", AppName + " - DPI/DNS zorlamasi kaldirici");
        Shortcut(Path.Combine(StartMenuDir, AppName + " Kaldir.lnk"), TargetSetup, "--uninstall", AppName + " kaldir");

        WriteRegistry();

        if (runServiceInstall)
        {
            Log(Loc.T("setup.testing"));
            long mark = LogMark();
            int code = RunExe(TargetExe, tryDoh ? "--install --try-doh" : "--install");
            LogTail(mark);
            if (code != 0)
            {
                Log(Loc.T("setup.appOnly"));
                return false;
            }
            Log(Loc.T("setup.active"));
        }

        Log(Loc.T("setup.installed"));
        return true;
    }

    public static bool Uninstall()
    {
        return RunExclusive(UninstallCore);
    }

    static bool UninstallCore()
    {
        Log(Loc.T("setup.removing"));
        Core.Sink = Log;
        bool delayed = Core.Uninstall();

        File.Delete(Path.Combine(StartMenuDir, AppName + ".lnk"));
        File.Delete(Path.Combine(StartMenuDir, AppName + " Kaldir.lnk"));
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);

        delayed |= Core.RemoveOwnedFile(TargetExe);
        delayed |= Core.RemoveOwnedFile(TargetSetup);
        if (Directory.Exists(TargetDir) && Directory.GetFileSystemEntries(TargetDir).Length == 0)
            Directory.Delete(TargetDir);
        else if (delayed && Directory.GetFileSystemEntries(TargetDir).Length <= 2)
            Core.QueueRemoval(TargetDir);

        Log(Loc.T(delayed ? "setup.restart" : "setup.removed"));
        return delayed;
    }

    static int RunExe(string file, string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        catch (Exception ex) { Log(Loc.T("setup.error", ex.Message)); return -1; }
    }

    public static bool Installed()
    {
        return File.Exists(TargetExe);
    }
}

class SetupForm : Form
{
    readonly TextBox log;
    readonly FlatButton install, remove, open;
    readonly CheckBox network, doh;
    readonly ComboBox language;
    readonly Label title, location, status, activity;
    bool busy;
    string statusKey;

    public SetupForm()
    {
        ClientSize = new Size(674, 500);
        MinimumSize = new Size(630, 465);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = Theme.Ui(9.5f, FontStyle.Regular);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var layout = new TableLayoutPanel {
            Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 20),
            ColumnCount = 2, RowCount = 8, AutoScroll = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
        for (int i = 0; i < 7; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        title = LabelFor(20f, FontStyle.Bold, Theme.Fg);
        layout.Controls.Add(title, 0, 0);
        language = new ComboBox {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Theme.Card, ForeColor = Theme.Fg, AccessibleName = "Language"
        };
        language.Items.AddRange(Loc.Names);
        language.SelectedIndex = Array.IndexOf(Loc.Codes, Loc.Lang);
        layout.Controls.Add(language, 1, 0);

        location = LabelFor(9f, FontStyle.Regular, Theme.Muted);
        AddWide(layout, location, 1);
        status = LabelFor(10f, FontStyle.Bold, Theme.Accent);
        status.Margin = new Padding(0, 16, 0, 10);
        AddWide(layout, status, 2);
        network = new CheckBox { AutoSize = true, ForeColor = Theme.Fg, Margin = new Padding(0, 4, 0, 4) };
        doh = new CheckBox { AutoSize = true, ForeColor = Theme.Fg, Enabled = false, Margin = new Padding(20, 4, 0, 4) };
        network.CheckedChanged += delegate { doh.Enabled = !busy && network.Checked; };
        AddWide(layout, network, 3);
        AddWide(layout, doh, 4);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 10) };
        install = new FlatButton { Size = new Size(158, 38), FillColor = Theme.Accent, TextColor = Theme.Bg,
            HoverColor = Theme.Ok, Font = Theme.Ui(9.5f, FontStyle.Bold), Margin = new Padding(0, 0, 8, 0) };
        remove = new FlatButton { Size = new Size(132, 38), Margin = new Padding(0, 0, 8, 0) };
        open = new FlatButton { Size = new Size(144, 38), Margin = new Padding(0) };
        actions.Controls.AddRange(new Control[] { install, remove, open });
        AddWide(layout, actions, 5);
        AcceptButton = install;
        install.Click += ClickInstall;
        remove.Click += ClickRemove;
        open.Click += delegate { try { Process.Start(SetupCore.TargetExe); } catch (Exception ex) { Log(Loc.T("setup.error", ex.Message)); } };

        activity = LabelFor(9f, FontStyle.Bold, Theme.Muted);
        AddWide(layout, activity, 6);
        log = new TextBox {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle,
            ScrollBars = ScrollBars.Vertical, ForeColor = Theme.Fg, BackColor = Theme.CardAlt,
            Font = Theme.Mono(9f), Margin = new Padding(0, 6, 0, 0)
        };
        AddWide(layout, log, 7);

        statusKey = SetupCore.Installed() ? "setup.installed" : "setup.ready";
        language.SelectedIndexChanged += delegate { Loc.Lang = Loc.Codes[language.SelectedIndex]; ApplyLanguage(); };
        SetupCore.Sink = Log;
        ApplyLanguage();
    }

    static Label LabelFor(float size, FontStyle style, Color color)
    {
        return new Label { AutoSize = true, ForeColor = color, Font = Theme.Ui(size, style), Margin = new Padding(0, 2, 0, 3) };
    }

    static void AddWide(TableLayoutPanel panel, Control control, int row)
    {
        panel.Controls.Add(control, 0, row);
        panel.SetColumnSpan(control, 2);
    }

    void ApplyLanguage()
    {
        Text = title.Text = Loc.T("setup.title");
        location.Text = Loc.T("setup.location", SetupCore.TargetDir);
        status.Text = Loc.T(statusKey);
        install.Text = Loc.T(SetupCore.Installed() ? "setup.repair" : "setup.install");
        remove.Text = Loc.T("btn.remove");
        open.Text = Loc.T("setup.open");
        network.Text = Loc.T("setup.network");
        doh.Text = Loc.T("setup.doh");
        activity.Text = Loc.T("setup.log");
        SetBusy(busy);
    }

    void SetBusy(bool active)
    {
        busy = active;
        install.Enabled = !active && statusKey != "setup.restart";
        language.Enabled = !active;
        network.Enabled = install.Enabled;
        doh.Enabled = !active && network.Checked;
        bool installed = SetupCore.Installed();
        remove.Enabled = open.Enabled = !active && installed && statusKey != "setup.restart";
        UseWaitCursor = active;
    }

    void Log(string message)
    {
        if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { Log(message); }); return; }
        log.AppendText(message + Environment.NewLine);
    }

    void Run(string pending, Func<string> work)
    {
        statusKey = pending;
        SetBusy(true);
        ApplyLanguage();
        ThreadPool.QueueUserWorkItem(delegate
        {
            string result = "setup.failed";
            try { result = work(); }
            catch (Exception ex) { Log(Loc.T("setup.error", ex.Message)); }
            BeginInvoke((MethodInvoker)delegate
            {
                statusKey = result;
                status.ForeColor = result == "setup.failed" || result == "setup.appOnly" ? Theme.Warn : Theme.Accent;
                SetBusy(false);
                ApplyLanguage();
            });
        });
    }

    void ClickInstall(object sender, EventArgs e)
    {
        bool start = network.Checked, encrypted = start && doh.Checked;
        Run("setup.installing", delegate
        {
            return SetupCore.Install(start, encrypted)
                ? (start ? "setup.active" : "setup.installed") : "setup.appOnly";
        });
    }

    void ClickRemove(object sender, EventArgs e)
    {
        if (MessageBox.Show(Loc.T("confirm.remove"), Loc.T("confirm.title"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Run("setup.removing", delegate { return SetupCore.Uninstall() ? "setup.restart" : "setup.removed"; });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }
}

static class SetupProgram
{
    [STAThread]
    static void Main(string[] args)
    {
        Core.LoadSettings();
        Loc.Lang = Core.Lang;
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (cmd == "--uninstall")
        {
            SetupCore.Sink = delegate (string s) { try { Console.WriteLine(s); } catch { } };
            try { if (SetupCore.Uninstall()) Environment.ExitCode = 3010; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return;
        }
        if (cmd == "--install")
        {
            SetupCore.Sink = delegate (string s) { try { Console.WriteLine(s); } catch { } };
            bool network = Array.IndexOf(args, "--app-only") < 0;
            try { Environment.ExitCode = SetupCore.Install(network, Array.IndexOf(args, "--try-doh") >= 0) ? 0 : 1; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
    }
}
