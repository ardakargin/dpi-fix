using System;
using System.Diagnostics;
using System.IO;

#if BROWSER_MODE
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

static class BrowserProgram
{
    static readonly string[] DiscordDomains = {
        "discord.com", "discordapp.com", "discordapp.net", "discord.gg", "discord.media"
    };

    [STAThread]
    static int Main(string[] args)
    {
        Loc.Lang = args.Length == 2 && args[0] == "--lang" && Array.IndexOf(Loc.Codes, args[1]) >= 0
            ? args[1] : Loc.SystemLanguage();

        try
        {
            RequireUnelevated();
            Run();
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.T("browser.error", ex.Message), Loc.T("btn.browser"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information,
        int informationLength, out int returnLength);

    static void RequireUnelevated()
    {
        const int tokenElevation = 20;
        int elevated, length;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            if (!GetTokenInformation(identity.Token, tokenElevation, out elevated, sizeof(int), out length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (elevated != 0) throw new InvalidOperationException(Loc.T("browser.elevated"));
    }

    static void Run()
    {
        string browserPath = FindBrowser();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DpiFixBrowser");
        string profileName = Path.GetFileName(browserPath).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) ? "Edge" : "Chrome";
        string profileDir = Path.Combine(root, profileName);
        Directory.CreateDirectory(profileDir);

        using (FileStream sessionLock = LockSession(root))
        {
            if (ProfileInUse(profileDir)) throw new InvalidOperationException(Loc.T("browser.profileBusy"));

            using (var relay = new BrowserRelay(DiscordDomains))
            {
                var start = new ProcessStartInfo(browserPath) {
                    Arguments = "--user-data-dir=\"" + profileDir + "\" --proxy-pac-url=\"" + relay.PacUrl +
                        "\" --disable-quic --disable-background-mode --no-first-run --no-default-browser-check" +
                        " --new-window https://discord.com/app",
                    WorkingDirectory = Path.GetDirectoryName(browserPath),
                    UseShellExecute = false
                };
                using (Process browser = Process.Start(start))
                {
                    if (browser == null) throw new InvalidOperationException(Loc.T("browser.startFailed"));
                    browser.WaitForExit();
                    // If the browser replaced its initial process, retain the relay while the dedicated profile is open.
                    while (ProfileInUse(profileDir)) Thread.Sleep(500);
                    if (browser.ExitCode != 0)
                        throw new InvalidOperationException(Loc.T("browser.exit", browser.ExitCode));
                }
            }
        }
    }

    static string FindBrowser()
    {
        string[] roots = {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        string[] paths = { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" };
        foreach (string relativePath in paths)
        {
            foreach (string root in roots)
            {
                if (String.IsNullOrEmpty(root)) continue;
                string path = Path.Combine(root, relativePath);
                if (File.Exists(path)) return path;
            }
        }
        throw new FileNotFoundException(Loc.T("browser.noBrowser"));
    }

    static FileStream LockSession(string root)
    {
        try { return File.Open(Path.Combine(root, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex)
        {
            if (!IsSharingViolation(ex)) throw;
            throw new InvalidOperationException(Loc.T("browser.alreadyRunning"));
        }
    }

    static bool ProfileInUse(string profileDir)
    {
        // Chromium opens this file for writing and shares it for reading only while the profile is active.
        try
        {
            using (File.Open(Path.Combine(profileDir, "lockfile"), FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete)) return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException ex)
        {
            if (IsSharingViolation(ex)) return true;
            throw;
        }
    }

    static bool IsSharingViolation(IOException ex)
    {
        return (ex.HResult & 0xffff) == 32;
    }
}
#else
static class BrowserSession
{
    public const string HelperName = "BrowserMode.exe";

    public static void LaunchUnelevated(string helperPath, string language)
    {
        if (!File.Exists(helperPath)) throw new FileNotFoundException(Loc.T("browser.helperMissing"));
        if (Array.IndexOf(Loc.Codes, language) < 0) language = Loc.SystemLanguage();

        // Use the desktop's Explorer instance to launch with the signed-in user's token.
        var shellWindowsClass = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        dynamic windows = Activator.CreateInstance(Type.GetTypeFromCLSID(shellWindowsClass, true));
        object location = 0, root = null;
        int handle;
        const int desktopClass = 8, needDispatch = 1;
        dynamic desktop = windows.FindWindowSW(ref location, ref root, desktopClass, out handle, needDispatch);
        if (desktop == null) throw new InvalidOperationException(Loc.T("browser.noDesktop"));
        desktop.Document.Application.ShellExecute(helperPath, "--lang " + language,
            Path.GetDirectoryName(helperPath), "open", 1);
    }
}
#endif
