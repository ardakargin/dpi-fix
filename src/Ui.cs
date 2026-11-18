// Ui.cs - tema, özel kontroller ve yerelleştirme
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x0B, 0x11, 0x17);
    public static readonly Color Card = Color.FromArgb(0x13, 0x1C, 0x26);
    public static readonly Color CardAlt = Color.FromArgb(0x0F, 0x17, 0x20);
    public static readonly Color Border = Color.FromArgb(0x1E, 0x2A, 0x38);
    public static readonly Color Fg = Color.FromArgb(0xE6, 0xED, 0xF5);
    public static readonly Color Muted = Color.FromArgb(0x8F, 0xA3, 0xB8);
    public static readonly Color Dim = Color.FromArgb(0x5B, 0x6E, 0x82);
    public static readonly Color Accent = Color.FromArgb(0x2D, 0xD4, 0xBF);
    public static readonly Color AccentDark = Color.FromArgb(0x0E, 0x3B, 0x38);
    public static readonly Color Warn = Color.FromArgb(0xF5, 0x9E, 0x0B);
    public static readonly Color Danger = Color.FromArgb(0xEF, 0x44, 0x44);
    public static readonly Color Ok = Color.FromArgb(0x22, 0xC5, 0x5E);

    public static Font Ui(float size, FontStyle style) { return new Font("Segoe UI", size, style); }
    public static Font Mono(float size) { return new Font("Consolas", size); }

    public static GraphicsPath Round(Rectangle r, int rad)
    {
        GraphicsPath p = new GraphicsPath();
        if (rad <= 0) { p.AddRectangle(r); return p; }
        int d = rad * 2;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// Kenarları yuvarlatılmış, ince çerçeveli panel
class RoundedPanel : Panel
{
    public int Radius = 12;
    public Color FillColor = Theme.Card;
    public Color BorderColor = Theme.Border;

    public RoundedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = ClientRectangle;
        r.Width -= 1; r.Height -= 1;
        using (GraphicsPath p = Theme.Round(r, Radius))
        {
            using (SolidBrush b = new SolidBrush(FillColor)) e.Graphics.FillPath(b, p);
            using (Pen pen = new Pen(BorderColor)) e.Graphics.DrawPath(pen, p);
        }
        base.OnPaint(e);
    }
}

/// Düz, yuvarlatılmış, hover'li düğme
class FlatButton : Button
{
    public Color FillColor = Theme.Card;
    public Color HoverColor = Color.FromArgb(0x1B, 0x27, 0x34);
    public Color TextColor = Theme.Fg;
    public Color BorderColor = Theme.Border;
    public int Radius = 9;
    bool hover;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        Font = Theme.Ui(9.5f, FontStyle.Regular);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = ClientRectangle; r.Width -= 1; r.Height -= 1;
        Color fill = !Enabled ? Color.FromArgb(0x14, 0x1B, 0x23) : (hover ? HoverColor : FillColor);
        Color text = !Enabled ? Theme.Dim : TextColor;
        // yuvarlak köşelerin dışında kalan alanı temizle (artık piksel kalmasın)
        using (SolidBrush bg = new SolidBrush(Parent != null && Parent.BackColor != Color.Transparent
            ? Parent.BackColor : Theme.Bg))
            e.Graphics.FillRectangle(bg, ClientRectangle);
        using (GraphicsPath p = Theme.Round(r, Radius))
        {
            using (SolidBrush b = new SolidBrush(fill)) e.Graphics.FillPath(b, p);
            if (BorderColor != Color.Empty)
                using (Pen pen = new Pen(!Enabled ? Theme.Card : BorderColor)) e.Graphics.DrawPath(pen, p);
        }
        Size ts = TextRenderer.MeasureText(Text, Font);
        int ty = (Height - ts.Height) / 2;
        if (ty < 1) ty = 1;
        TextRenderer.DrawText(e.Graphics, Text, Font,
            new Rectangle(0, ty, Width, ts.Height + 2), text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), text, fill);
    }
}

/// Onay kutusu (koyu tema) - Control'den türetildi, çift çizim olmaz
class FlatCheck : Control
{
    bool _checked;
    public event EventHandler CheckedChanged;

    public bool Checked
    {
        get { return _checked; }
        set { _checked = value; Invalidate(); }
    }

    public FlatCheck()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        ForeColor = Theme.Muted;
        Font = Theme.Ui(9f, FontStyle.Regular);
        Height = 22;
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Width = 15 + 8 + TextRenderer.MeasureText(Text, Font).Width + 4;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int box = 15, y = (Height - box) / 2;
        Rectangle r = new Rectangle(0, y, box, box);
        using (GraphicsPath p = Theme.Round(r, 4))
        {
            using (SolidBrush b = new SolidBrush(Checked ? Theme.Accent : Theme.CardAlt)) e.Graphics.FillPath(b, p);
            using (Pen pen = new Pen(Checked ? Theme.Accent : Theme.Border)) e.Graphics.DrawPath(pen, p);
        }
        if (Checked)
        {
            using (Pen pen = new Pen(Color.FromArgb(0x04, 0x25, 0x2B), 2f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                e.Graphics.DrawLines(pen, new Point[] {
                    new Point(4, y + 8), new Point(7, y + 11), new Point(12, y + 4) });
            }
        }
        TextRenderer.DrawText(e.Graphics, Text, Font,
            new Rectangle(box + 8, 0, Width - box - 8, Height),
            Enabled ? ForeColor : Theme.Dim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// Arayüz yerelleştirmesi
static class Loc
{
    public static string Lang = "tr";
    public static readonly string[] Codes = { "tr", "en", "de", "fr", "es" };
    public static readonly string[] Names = { "Türkçe", "English", "Deutsch", "Français", "Español" };
    static readonly Dictionary<string, string[]> International = new Dictionary<string, string[]>();

    public static string SystemLanguage()
    {
        string code = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Array.IndexOf(Codes, code) >= 0 ? code : "en";
    }

    static void AddInternational(string key, string de, string fr, string es)
    {
        International[key] = new[] { de, fr, es };
    }

    static readonly Dictionary<string, string> TR = new Dictionary<string, string>();
    static readonly Dictionary<string, string> EN = new Dictionary<string, string>();

    static void Add(string key, string tr, string en) { TR[key] = tr; EN[key] = en; }

    static Loc()
    {
        Add("profile.reordered", "HTTPS (ters TCP sırası)", "HTTPS (reordered TCP)");
        AddInternational("profile.reordered", "HTTPS (umgekehrte TCP-Reihenfolge)", "HTTPS (segments TCP inversés)", "HTTPS (segmentos TCP invertidos)");
        Add("profile.records", "HTTPS (bölünmüş TLS kayıtları)", "HTTPS (split TLS records)");
        AddInternational("profile.records", "HTTPS (geteilte TLS-Records)", "HTTPS (enregistrements TLS fractionnés)", "HTTPS (registros TLS divididos)");
        Add("setup.title", "DPI Fix Kurulumu", "DPI Fix Setup");
        AddInternational("setup.title", "DPI Fix installieren", "Installation de DPI Fix", "Instalación de DPI Fix");
        Add("setup.location", "Kurulum klasörü: {0}", "Install folder: {0}");
        AddInternational("setup.location", "Installationsordner: {0}", "Dossier d’installation : {0}", "Carpeta de instalación: {0}");
        Add("setup.ready", "Kuruluma hazır", "Ready to install");
        AddInternational("setup.ready", "Bereit zur Installation", "Prêt pour l’installation", "Listo para instalar");
        Add("setup.install", "Kur", "Install");
        AddInternational("setup.install", "Installieren", "Installer", "Instalar");
        Add("setup.repair", "Yeniden Kur", "Reinstall");
        AddInternational("setup.repair", "Neu installieren", "Réinstaller", "Reinstalar");
        Add("setup.network", "Bu bilgisayar için Windows servisini test et ve kur", "Test and install the Windows service for this PC");
        AddInternational("setup.network", "Windows-Dienst für diesen PC testen und installieren", "Tester et installer le service Windows pour ce PC", "Probar e instalar el servicio de Windows para este PC");
        Add("setup.doh", "Gerekirse Windows DNS’ini Cloudflare DNS-over-HTTPS ile dene", "Try Cloudflare DNS over HTTPS for Windows DNS if needed");
        AddInternational("setup.doh", "Bei Bedarf Cloudflare DNS over HTTPS für Windows-DNS testen",
            "Si nécessaire, tester le DNS sur HTTPS de Cloudflare pour Windows",
            "Si es necesario, probar DNS sobre HTTPS de Cloudflare en Windows");
        Add("setup.open", "Uygulamayı Aç", "Open DPI Fix");
        AddInternational("setup.open", "DPI Fix öffnen", "Ouvrir DPI Fix", "Abrir DPI Fix");
        Add("setup.installing", "Uygulama kuruluyor…", "Installing the app…");
        AddInternational("setup.installing", "App wird installiert…", "Installation de l’application…", "Instalando la aplicación…");
        Add("setup.testing", "Bağlantı ve servis profili test ediliyor…", "Testing the connection and service profile…");
        AddInternational("setup.testing", "Verbindung und Dienstprofil werden geprüft…", "Test de la connexion et du profil de service…", "Probando la conexión y el perfil del servicio…");
        Add("setup.installed", "Uygulama kuruldu", "App installed");
        AddInternational("setup.installed", "App installiert", "Application installée", "Aplicación instalada");
        Add("setup.appOnly", "Uygulama kuruldu. Servis sonucu için işlem günlüğüne bakın.",
            "App installed. See the activity log for service details.");
        AddInternational("setup.appOnly", "App installiert. Details zum Dienst stehen im Vorgangsprotokoll.",
            "Application installée. Le journal indique le résultat pour le service.",
            "Aplicación instalada. Consulta el registro para ver el estado del servicio.");
        Add("setup.active", "Servis çalışıyor", "Service running");
        AddInternational("setup.active", "Dienst läuft", "Service en cours", "Servicio en ejecución");
        Add("setup.removing", "DPI Fix kaldırılıyor…", "Removing DPI Fix…");
        AddInternational("setup.removing", "DPI Fix wird entfernt…", "Suppression de DPI Fix…", "Desinstalando DPI Fix…");
        Add("setup.removed", "DPI Fix kaldırıldı", "DPI Fix removed");
        AddInternational("setup.removed", "DPI Fix entfernt", "DPI Fix désinstallé", "DPI Fix desinstalado");
        Add("setup.restart", "Kaldırmayı tamamlamak için Windows’u yeniden başlatın.", "Restart Windows to finish removal.");
        AddInternational("setup.restart", "Starten Sie Windows neu, um die Deinstallation abzuschließen.", "Redémarrez Windows pour terminer la désinstallation.", "Reinicia Windows para completar la desinstalación.");
        Add("setup.error", "İşlem tamamlanamadı. {0}", "Operation could not be completed. {0}");
        AddInternational("setup.error", "Vorgang fehlgeschlagen. {0}", "L’opération a échoué. {0}", "No se pudo completar la operación. {0}");
        Add("setup.failed", "İşlem tamamlanamadı", "Operation failed");
        AddInternational("setup.failed", "Vorgang fehlgeschlagen", "Échec de l’opération", "No se pudo completar");
        Add("setup.missing", "Kurulum paketindeki uygulama bulunamadı.", "Application missing from setup package.");
        AddInternational("setup.missing", "App fehlt im Installationspaket.", "Application absente du programme d’installation.", "Falta la aplicación en el instalador.");
        Add("setup.busy", "Başka bir DPI Fix kurulumu çalışıyor.", "Another DPI Fix setup is running.");
        AddInternational("setup.busy", "Ein anderes DPI Fix-Setup läuft bereits.", "Une autre installation de DPI Fix est en cours.", "Ya se está ejecutando otra instalación de DPI Fix.");
        Add("setup.arch", "DPI Fix, Windows x64 gerektirir.", "DPI Fix requires Windows x64.");
        AddInternational("setup.arch", "DPI Fix erfordert Windows x64.", "DPI Fix nécessite Windows x64.", "DPI Fix requiere Windows x64.");
        Add("setup.shortcut", "Kısayol oluşturuldu: {0}", "Shortcut created: {0}");
        AddInternational("setup.shortcut", "Verknüpfung erstellt: {0}", "Raccourci créé : {0}", "Acceso directo creado: {0}");
        Add("setup.log", "İşlem günlüğü", "Activity log");
        AddInternational("setup.log", "Vorgangsprotokoll", "Journal des opérations", "Registro de actividad");
        Add("app.title", "DPI Fix", "DPI Fix");
        Add("app.sub", "Bağlantı ve Windows servisi", "Connection and Windows service");
        Add("app.serviceStatus", "Windows servisi · {0}", "Windows service · {0}");
        Add("sec.actions", "İŞLEMLER", "ACTIONS");
        Add("sec.settings", "AYARLAR", "SETTINGS");
        Add("sec.log", "GÜNLÜK", "LOG");

        Add("st.running", "ÇALIŞIYOR", "RUNNING");
        Add("st.stopped", "DURDU", "STOPPED");
        Add("st.starting", "BAŞLIYOR", "STARTING");
        Add("st.stopping", "DURDURULUYOR", "STOPPING");
        Add("st.absent", "KURULU DEĞİL", "NOT INSTALLED");
        Add("st.unknown", "BİLİNMİYOR", "UNKNOWN");

        Add("lbl.appVersion", "Uygulama sürümü", "App version");
        Add("lbl.engine", "Paketlenen motor", "Bundled engine");
        Add("lbl.profile", "Yapılandırma", "Configuration");
        Add("lbl.sites", "Site listesi", "Site list");
        Add("lbl.entries", "{0} alan adı", "{0} domains");

        Add("btn.install", "Servisi Kur / Onar", "Install / Repair Service");
        Add("btn.stop", "Servisi Durdur", "Stop Service");
        Add("btn.remove", "Servisi Kaldır", "Remove Service");
        Add("btn.test", "Bağlantı Testi", "Connection Test");
        Add("btn.list", "Site Listesi", "Site List");
        Add("btn.update", "Güncelleme", "Update");
        Add("btn.log", "Günlüğü Aç", "Open Log");
        Add("btn.browser", "Discord’u Tarayıcıda Aç", "Discord in Browser");
        AddInternational("btn.browser", "Discord im Browser", "Discord sur le Web", "Discord en la web");
        Add("browser.help", "Discord, ayrı bir Edge veya Chrome profilinde açılır.\r\nYerel bağlantı aracı Cloudflare DNS-over-HTTPS’i yalnızca seçili Discord alan adları için kullanır.\r\nDiğer sitelere doğrudan bağlanılır. Tarayıcı site sertifikalarını doğrular.",
            "Opens Discord in a separate Edge or Chrome profile.\r\nThe local relay uses Cloudflare DNS over HTTPS for selected Discord domains only.\r\nOther sites connect directly. The browser verifies site certificates.");
        AddInternational("browser.help",
            "Öffnet Discord in einem separaten Edge- oder Chrome-Profil.\r\nDie lokale Weiterleitung nutzt DNS over HTTPS von Cloudflare nur für ausgewählte Discord-Domains.\r\nAndere Websites werden direkt aufgerufen. Der Browser prüft die Website-Zertifikate.",
            "Ouvre Discord dans un profil Edge ou Chrome distinct.\r\nLe relais local utilise le DNS sur HTTPS de Cloudflare uniquement pour certains domaines Discord.\r\nLes autres sites sont accessibles directement. Le navigateur vérifie les certificats des sites.",
            "Abre Discord en un perfil independiente de Edge o Chrome.\r\nEl intermediario local usa DNS sobre HTTPS de Cloudflare solo para algunos dominios de Discord.\r\nLos demás sitios se conectan directamente. El navegador verifica los certificados de los sitios.");
        Add("browser.error", "Tarayıcı modu açılamadı. {0}", "Browser mode could not open. {0}");
        AddInternational("browser.error", "Der Browsermodus konnte nicht geöffnet werden. {0}",
            "Impossible d’ouvrir le mode navigateur. {0}", "No se pudo abrir el modo navegador. {0}");
        Add("browser.helperMissing", "BrowserMode.exe bulunamadı. DPI Fix’i kurulum paketiyle yeniden kurun.",
            "BrowserMode.exe is missing. Reinstall DPI Fix using the setup package.");
        AddInternational("browser.helperMissing", "BrowserMode.exe fehlt. Installieren Sie DPI Fix mit dem Installationspaket neu.",
            "BrowserMode.exe est introuvable. Réinstallez DPI Fix avec le programme d’installation.",
            "Falta BrowserMode.exe. Reinstala DPI Fix con el instalador.");
        Add("browser.noDesktop", "Windows Gezgini’nin masaüstü oturumu bulunamadı.", "The Windows Explorer desktop session was not available.");
        AddInternational("browser.noDesktop", "Die Desktop-Sitzung des Windows-Explorers war nicht verfügbar.",
            "La session de bureau de l’Explorateur Windows était indisponible.", "La sesión de escritorio del Explorador de Windows no estaba disponible.");
        Add("browser.elevated", "Tarayıcı modunu yönetici yetkisi olmadan çalıştırın.", "Run browser mode without administrator privileges.");
        AddInternational("browser.elevated", "Starten Sie den Browsermodus ohne Administratorrechte.",
            "Exécutez le mode navigateur sans privilèges administrateur.", "Ejecuta el modo navegador sin privilegios de administrador.");
        Add("browser.noBrowser", "Microsoft Edge veya Google Chrome’un standart kurulumu bulunamadı.",
            "A standard installation of Microsoft Edge or Google Chrome was not found.");
        AddInternational("browser.noBrowser", "Es wurde keine Standardinstallation von Microsoft Edge oder Google Chrome gefunden.",
            "Aucune installation standard de Microsoft Edge ou Google Chrome n’a été trouvée.",
            "No se encontró una instalación estándar de Microsoft Edge o Google Chrome.");
        Add("browser.alreadyRunning", "Tarayıcı modu zaten çalışıyor. Açık tarayıcı penceresini kullanın.",
            "Browser mode is already running. Use the open browser window.");
        AddInternational("browser.alreadyRunning", "Der Browsermodus läuft bereits. Verwenden Sie das geöffnete Browserfenster.",
            "Le mode navigateur est déjà actif. Utilisez la fenêtre de navigateur ouverte.",
            "El modo navegador ya está en ejecución. Usa la ventana del navegador abierta.");
        Add("browser.profileBusy", "DPI Fix tarayıcı profili kullanımda. Profilin pencerelerini kapatıp tekrar deneyin.",
            "The DPI Fix browser profile is in use. Close its windows and try again.");
        AddInternational("browser.profileBusy", "Das Browserprofil von DPI Fix ist bereits in Verwendung. Schließen Sie seine Fenster und versuchen Sie es erneut.",
            "Le profil de navigateur DPI Fix est déjà utilisé. Fermez ses fenêtres et réessayez.",
            "El perfil del navegador de DPI Fix está en uso. Cierra sus ventanas y vuelve a intentarlo.");
        Add("browser.startFailed", "Tarayıcı başlatılamadı.", "The browser did not start.");
        AddInternational("browser.startFailed", "Der Browser wurde nicht gestartet.", "Le navigateur n’a pas démarré.", "El navegador no se inició.");
        Add("browser.exit", "Tarayıcı {0} çıkış koduyla kapandı.", "The browser exited with code {0}.");
        AddInternational("browser.exit", "Der Browser wurde mit Code {0} beendet.",
            "Le navigateur s’est fermé avec le code {0}.", "El navegador se cerró con el código {0}.");
        Add("btn.lang", "Dil", "Language");
        Add("btn.close", "Kapat", "Close");

        Add("chk.doh", "Gerekirse Windows DNS-over-HTTPS’i dene", "Try Windows DNS over HTTPS if needed");
        AddInternational("chk.doh", "Bei Bedarf Windows-DNS über HTTPS testen", "Tester le DNS sur HTTPS de Windows si nécessaire", "Probar DNS sobre HTTPS de Windows si hace falta");
        Add("chk.watch", "Ağ değişince bağlantıyı test et", "Test after network changes");
        Add("chk.verify", "Dosya bütünlüğü doğrulaması", "Payload integrity check");
        Add("log.engine", "Motor: {0}", "Engine: {0}");
        Add("log.enginefb", "Rust motoru kullanılamıyor, GoodbyeDPI'ya dönüldü.",
                            "Rust engine unavailable, falling back to GoodbyeDPI.");

        Add("confirm.remove", "DPI Fix uygulaması ve Windows servisi kaldırılacak. Önceki DNS ayarları geri yüklenecek, uygulama ayarları, site listesi ve günlük silinecek. Devam edilsin mi?",
                             "DPI Fix and its Windows service will be uninstalled. Previous DNS settings will be restored, and the app settings, site list, and log will be deleted. Continue?");
        Add("confirm.title", "DPI Fix’i Kaldır", "Uninstall DPI Fix");
        Add("confirm.serviceRemove", "Windows servisi ve servis dosyaları kaldırılacak. Önceki DNS ayarları geri yüklenecek. Devam edilsin mi?",
                                     "The Windows service and its files will be removed. Previous DNS settings will be restored. Continue?");
        Add("service.restart", "Servisin kaldırılmasını tamamlamak için Windows’u yeniden başlatın.",
                               "Restart Windows to finish removing the service.");

        Add("hint.tip", "Site listesindeki değişiklikleri uygulamak için ‘Servisi Kur / Onar’ düğmesini kullanın.",
                        "Use ‘Install / Repair Service’ to apply changes to the site list.");
        Add("hint.background", "Kurulumdan sonra bu pencereyi kapatsanız da servis çalışmaya devam eder.",
                               "After install you can close this window; the service keeps running.");
        Add("hint.log", "Günlük dosyası: {0}", "Log file: {0}");

        Add("run.install", "Servis kuruluyor veya onarılıyor…", "Installing or repairing the service…");
        Add("run.stop", "Servis durduruluyor…", "Stopping the service…");
        Add("run.remove", "Servis kaldırılıyor…", "Removing the service…");
        Add("run.test", "Bağlantı test ediliyor…", "Testing the connection…");
        Add("run.update", "Güncellemeler kontrol ediliyor…", "Checking for updates…");

        Add("lang.name", "Türkçe", "English");
        Add("lang.other", "English", "Türkçe");

        // --- günlük / motor mesajları ---
        Add("log.step1", "Motor dosyaları çıkarılıyor", "Extracting engine files");
        Add("log.payloadreused", "Paketle eşleşen dosya kullanılıyor: {0}", "Reusing matching file: {0}");
        Add("log.payloaderror", "Dosya hazırlanamadı: {0}. {1}", "Could not prepare file: {0}. {1}");
        Add("log.driverbusy", "Sürücü dosyası değiştirilemedi. Windows’u yeniden başlatıp kurulumu tekrar deneyin.",
                              "The driver file could not be replaced. Restart Windows and retry setup.");
        Add("log.step2", "DPI Fix servisi durduruluyor", "Stopping the DPI Fix service");
        Add("log.baseline", "Motor kapalıyken bağlantı sonuçları", "Connection results with the engine stopped");
        Add("probe.dns", "DNS çözümlemesi başarısız", "DNS lookup failed");
        Add("probe.tcp", "TCP bağlantısı kurulamadı", "TCP connection failed");
        Add("probe.tls", "TLS bağlantısı kurulamadı", "TLS handshake failed");
        Add("probe.cert", "TLS sertifikası doğrulanamadı", "TLS certificate validation failed");
        Add("probe.http", "Geçerli bir HTTP yanıtı alınamadı", "No valid HTTP response");
        Add("probe.timeout", "Bağlantı testi zaman aşımına uğradı", "Connection check timed out");
        Add("log.trydoh", "Ayarlar’da DNS-over-HTTPS’i seçip ‘Servisi Kur / Onar’ ile tekrar deneyebilirsiniz.",
                          "Enable DNS-over-HTTPS in Settings and try ‘Install / Repair Service’ again.");
        Add("log.engineexit", "Motor durdu ({0}). {1}", "The engine exited ({0}). {1}");
        Add("log.step3", "[3/6] DNS katmanı", "[3/6] DNS layer");
        Add("log.step4", "[4/6] Bu hat için en uygun yapılandırma aranıyor (~20 sn)",
                         "[4/6] Finding the best configuration for this network (~20s)");
        Add("log.step5", "[5/6] Servis oluşturuluyor", "[5/6] Creating service");
        Add("log.step6", "[6/6] Başlatılıyor", "[6/6] Starting");
        Add("log.trying", "Deneniyor: {0}", "Trying: {0}");
        Add("log.next", "Uygun değil, sonraki adaya geçiliyor.", "Not suitable, moving to next candidate.");
        Add("log.none", "Bağlantı kontrollerini geçen bir profil bulunamadı. Windows servisi devre dışı kaldı. Discord için ‘Discord’u Tarayıcıda Aç’ seçeneğini deneyin.",
                        "No profile passed the connection checks. The Windows service remains inactive. For Discord, try ‘Discord in Browser’.");
        AddInternational("log.none", "Kein Profil hat die Verbindungstests bestanden. Der Windows-Dienst bleibt inaktiv. Versuchen Sie für Discord „Discord im Browser“.",
            "Aucun profil n’a réussi les tests de connexion. Le service Windows reste inactif. Pour Discord, essayez « Discord sur le Web ».",
            "Ningún perfil superó las pruebas de conexión. El servicio de Windows sigue inactivo. Para Discord, prueba «Discord en la web».");
        Add("log.serviceValidationFailed", "Windows servisi son bağlantı kontrolünü geçemedi.",
            "The Windows service failed the final connection check.");
        AddInternational("log.serviceValidationFailed", "Der Windows-Dienst hat den abschließenden Verbindungstest nicht bestanden.",
            "Le service Windows a échoué au dernier test de connexion.",
            "El servicio de Windows no superó la comprobación final de conexión.");
        Add("log.chosen", "Seçilen: {0}", "Selected: {0}");
        Add("log.svcfail", "Servis başlamadı. Ayrıntı: {0}", "Service did not start. Detail: {0}");
        Add("log.done", "Servis çalışıyor. Windows açıldığında başlar ve uygulama kapalıyken çalışır.",
                        "The service is running. It starts with Windows and runs after you close the app.");
        Add("log.doneImproved", "Test edilen sitelerden en az birine erişim iyileşti. Mevcut bağlantılar korundu.",
            "Access improved for at least one tested site and existing connections were preserved.");
        AddInternational("log.doneImproved", "Der Zugriff auf mindestens eine getestete Website wurde verbessert. Bestehende Verbindungen blieben erhalten.",
            "L’accès à au moins un des sites testés s’est amélioré et les connexions existantes ont été préservées.",
            "Mejoró el acceso a por lo menos un sitio probado y se conservaron las conexiones existentes.");
        Add("log.donePreserved", "Test edilen siteler zaten yanıt veriyordu. Servis mevcut bağlantıları korudu. Erişimde iyileşme ölçülmedi.",
            "The tested sites already responded. The service preserved existing connections, with no measured improvement in access.");
        AddInternational("log.donePreserved", "Die getesteten Websites antworteten bereits. Der Dienst erhielt bestehende Verbindungen. Eine Verbesserung des Zugriffs wurde nicht gemessen.",
            "Les sites testés répondaient déjà. Le service a préservé les connexions existantes. Aucune amélioration de l’accès n’a été mesurée.",
            "Los sitios probados ya respondían. El servicio conservó las conexiones existentes. No se midió ninguna mejora en el acceso.");
        Add("log.failed", "Servis kurulumu tamamlanamadı.", "Service installation could not be completed.");
        Add("log.removed", "Servis ve servis dosyaları kaldırıldı.", "The service and its files were removed.");
        Add("log.testhead", "Bağlantı testi (servis: {0})", "Connection test (service: {0})");
        Add("log.testok", "Tüm kontroller geçti.", "All checks passed.");
        Add("log.testpartial", "Genel HTTPS bağlantısı doğrulandı. Bazı sitelere erişim doğrulanamadı.",
                               "General HTTPS connectivity was confirmed. Access to some sites could not be confirmed.");
        AddInternational("log.testpartial", "Die allgemeine HTTPS-Verbindung wurde bestätigt. Der Zugriff auf einige Websites konnte nicht bestätigt werden.",
            "La connexion HTTPS générale a été confirmée. L’accès à certains sites n’a pas pu être confirmé.",
            "Se confirmó la conexión HTTPS general. No se pudo confirmar el acceso a algunos sitios.");
        Add("log.testfail", "Sorun sürüyorsa ‘Servisi Kur / Onar’ ile yapılandırmayı değiştirin.",
                            "If the problem persists, use ‘Install / Repair Service’ to change the configuration.");
        Add("log.testwarn", "Not: servis çalışmıyor, sonuç yanıltıcı olabilir.",
                            "Note: service is not running, result may be misleading.");
        Add("log.reach", "erişilebilir", "reachable");
        Add("log.blocked", "erişilemedi", "unreachable");
        Add("log.req", " (kontrol)", " (control)");
        AddInternational("log.req", " (Kontrolle)", " (contrôle)", " (control)");
        Add("log.updscan", "Güncelleme kontrolü...", "Checking for updates...");
        Add("log.updcur", "Kurulu motor: DPI Fix Engine {0} | DpiFix v{1}",
                          "Installed engine: DPI Fix Engine {0} | DpiFix v{1}");
        Add("log.updnew", "Upstream son sürüm: {0}", "Latest upstream: {0}");
        Add("log.updhas", "Yeni DPI Fix sürümü mevcut.", "A newer DPI Fix version is available.");
        Add("log.updnone", "DPI Fix güncel.", "DPI Fix is up to date.");
        Add("log.netchg", "Ağ değişikliği algılandı; mevcut yapılandırma test ediliyor...",
                          "Network change detected; testing current configuration...");
        Add("log.netok", "Mevcut yapılandırma hâlâ çalışıyor, değişiklik yapılmadı.",
                         "Current configuration still works, no change made.");
        Add("log.netbad", "Ağ değişikliğinden sonra bağlantı kontrolü başarısız.",
                          "Connectivity check failed after the network change.");
        Add("log.dohon", "DNS-over-HTTPS (Cloudflare) etkinleştirildi, önceki DNS ayarı yedeklendi.",
                         "DNS-over-HTTPS (Cloudflare) enabled, previous DNS setting backed up.");
        Add("log.dohoff", "DNS-over-HTTPS kapalı (ayar).", "DNS-over-HTTPS disabled (setting).");
        Add("log.dir", "Yönlendirme modu: {0}", "Redirect mode: {0}");
        Add("log.blacklist", "Varsayılan site listesi oluşturuldu: {0}", "Default site list created: {0}");

        AddInternational("app.sub", "Verbindung und Windows-Dienst", "Connexion et service Windows", "Conexión y servicio de Windows");
        AddInternational("app.serviceStatus", "Windows-Dienst · {0}", "Service Windows · {0}", "Servicio de Windows · {0}");
        AddInternational("sec.actions", "AKTIONEN", "ACTIONS", "ACCIONES");
        AddInternational("sec.settings", "EINSTELLUNGEN", "PARAMÈTRES", "AJUSTES");
        AddInternational("sec.log", "PROTOKOLL", "JOURNAL", "REGISTRO");
        AddInternational("st.running", "AKTIV", "ACTIF", "ACTIVO");
        AddInternational("st.stopped", "GESTOPPT", "ARRÊTÉ", "DETENIDO");
        AddInternational("st.starting", "STARTET", "DÉMARRAGE", "INICIANDO");
        AddInternational("st.stopping", "STOPPT", "ARRÊT EN COURS", "DETENIENDO");
        AddInternational("st.absent", "NICHT INSTALLIERT", "NON INSTALLÉ", "SIN INSTALAR");
        AddInternational("st.unknown", "UNBEKANNT", "INCONNU", "DESCONOCIDO");
        AddInternational("lbl.appVersion", "App-Version", "Version de l’application", "Versión de la aplicación");
        AddInternational("lbl.engine", "Mitgelieferte Engine", "Moteur fourni", "Motor incluido");
        AddInternational("lbl.profile", "Konfiguration", "Configuration", "Configuración");
        AddInternational("lbl.sites", "Websites", "Sites", "Sitios");
        AddInternational("lbl.entries", "{0} Domains", "{0} domaines", "{0} dominios");
        AddInternational("btn.install", "Dienst installieren / reparieren", "Installer / réparer le service", "Instalar / reparar el servicio");
        AddInternational("btn.stop", "Dienst stoppen", "Arrêter le service", "Detener el servicio");
        AddInternational("btn.remove", "Dienst entfernen", "Supprimer le service", "Desinstalar el servicio");
        AddInternational("btn.test", "Verbindungstest", "Tester la connexion", "Probar conexión");
        AddInternational("btn.list", "Websites", "Liste des sites", "Lista de sitios");
        AddInternational("btn.update", "Updates", "Mises à jour", "Actualizaciones");
        AddInternational("btn.log", "Protokoll öffnen", "Ouvrir le journal", "Abrir registro");
        AddInternational("btn.lang", "Sprache", "Langue", "Idioma");
        AddInternational("chk.watch", "Bei Netzwechsel testen", "Tester après un changement de réseau", "Probar al cambiar de red");
        AddInternational("chk.verify", "Dateiintegrität prüfen", "Vérifier les fichiers", "Verificar archivos");
        AddInternational("confirm.title", "DPI Fix deinstallieren", "Désinstaller DPI Fix", "Desinstalar DPI Fix");
        AddInternational("confirm.remove",
            "DPI Fix und sein Windows-Dienst werden deinstalliert. Die bisherigen DNS-Einstellungen werden wiederhergestellt. App-Einstellungen, Website-Liste und Protokoll werden gelöscht. Fortfahren?",
            "DPI Fix et son service Windows seront désinstallés. Les paramètres DNS précédents seront restaurés. Les paramètres de l’application, la liste des sites et le journal seront supprimés. Continuer ?",
            "Se desinstalarán DPI Fix y su servicio de Windows. Se restaurarán los ajustes de DNS anteriores y se borrarán los ajustes de la aplicación, la lista de sitios y el registro. ¿Continuar?");
        AddInternational("confirm.serviceRemove",
            "Der Windows-Dienst und seine Dateien werden entfernt. Die bisherigen DNS-Einstellungen werden wiederhergestellt. Fortfahren?",
            "Le service Windows et ses fichiers seront supprimés. Les paramètres DNS précédents seront restaurés. Continuer ?",
            "Se desinstalará el servicio de Windows y se eliminarán sus archivos. Se restaurarán los ajustes de DNS anteriores. ¿Continuar?");
        AddInternational("service.restart", "Starten Sie Windows neu, um den Dienst vollständig zu entfernen.",
            "Redémarrez Windows pour terminer la suppression du service.", "Reinicia Windows para terminar de desinstalar el servicio.");
        AddInternational("hint.tip", "Verwenden Sie „Dienst installieren / reparieren“, um Änderungen an der Website-Liste zu übernehmen.",
            "Utilisez « Installer / réparer le service » pour appliquer les modifications de la liste des sites.",
            "Utiliza «Instalar / reparar el servicio» para aplicar los cambios en la lista de sitios.");
        AddInternational("hint.log", "Protokoll: {0}", "Journal : {0}", "Registro: {0}");
        AddInternational("run.install", "Dienst wird installiert oder repariert…", "Installation ou réparation du service…", "Instalando o reparando el servicio…");
        AddInternational("run.stop", "Dienst wird gestoppt…", "Arrêt du service…", "Deteniendo el servicio…");
        AddInternational("run.remove", "Dienst wird entfernt…", "Suppression du service…", "Desinstalando el servicio…");
        AddInternational("run.test", "Verbindung wird getestet…", "Test de la connexion…", "Probando la conexión…");
        AddInternational("run.update", "Suche nach Updates…", "Recherche de mises à jour…", "Buscando actualizaciones…");
        AddInternational("log.trydoh",
            "Aktivieren Sie DNS-over-HTTPS in den Einstellungen und versuchen Sie es erneut mit „Dienst installieren / reparieren“.",
            "Activez le DNS-over-HTTPS dans les paramètres, puis réessayez avec « Installer / réparer le service ».",
            "Activa DNS-over-HTTPS en los ajustes y vuelve a intentarlo con «Instalar / reparar el servicio».");
        AddInternational("log.done", "Der Dienst läuft. Er startet mit Windows und bleibt nach dem Schließen der App aktiv.",
            "Le service est en cours d’exécution. Il démarre avec Windows et reste actif après la fermeture de l’application.",
            "El servicio está en ejecución. Se inicia con Windows y sigue activo al cerrar la aplicación.");
        AddInternational("log.failed", "Der Dienst konnte nicht installiert werden.", "Le service n’a pas pu être installé.", "No se pudo instalar el servicio.");
        AddInternational("log.removed", "Der Dienst und seine Dateien wurden entfernt.", "Le service et ses fichiers ont été supprimés.", "Se desinstaló el servicio y se eliminaron sus archivos.");
        AddInternational("log.testfail",
            "Falls das Problem weiter besteht, ändern Sie die Konfiguration über „Dienst installieren / reparieren“.",
            "Si le problème persiste, utilisez « Installer / réparer le service » pour modifier la configuration.",
            "Si el problema persiste, usa «Instalar / reparar el servicio» para cambiar la configuración.");
    }

    public static string T(string key)
    {
        int language = Array.IndexOf(Codes, Lang);
        string[] localized;
        if (language >= 2 && International.TryGetValue(key, out localized)) return localized[language - 2];
        Dictionary<string, string> d = language == 0 ? TR : EN;
        string v;
        return d.TryGetValue(key, out v) ? v : key;
    }

    public static string T(string key, params object[] args)
    {
        return string.Format(T(key), args);
    }
}
