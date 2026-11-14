// Ui.cs - tema, özel kontroller ve yerelleştirme (TR/EN)
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

/// TR / EN yerelleştirme
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
        Add("setup.network", "Kurulumdan sonra bağlantıyı test et ve uygun servisi başlat", "Test the connection and start a compatible service");
        AddInternational("setup.network", "Verbindung testen und passenden Dienst starten", "Tester la connexion et démarrer un service compatible", "Probar la conexión e iniciar un servicio compatible");
        Add("setup.doh", "Gerekirse Windows DNS-over-HTTPS’i dene", "Try Windows DNS-over-HTTPS if needed");
        AddInternational("setup.doh", "Bei Bedarf Windows DNS-over-HTTPS testen", "Essayer le DNS-over-HTTPS de Windows si nécessaire", "Probar DNS-over-HTTPS de Windows si es necesario");
        Add("setup.open", "Uygulamayı Aç", "Open DPI Fix");
        AddInternational("setup.open", "DPI Fix öffnen", "Ouvrir DPI Fix", "Abrir DPI Fix");
        Add("setup.installing", "Uygulama kuruluyor…", "Installing the app…");
        AddInternational("setup.installing", "App wird installiert…", "Installation de l’application…", "Instalando la aplicación…");
        Add("setup.testing", "Bağlantı ve servis profili test ediliyor…", "Testing the connection and service profile…");
        AddInternational("setup.testing", "Verbindung und Dienstprofil werden geprüft…", "Test de la connexion et du profil de service…", "Probando la conexión y el perfil del servicio…");
        Add("setup.installed", "Uygulama kuruldu", "App installed");
        AddInternational("setup.installed", "App installiert", "Application installée", "Aplicación instalada");
        Add("setup.appOnly", "Uygulama kuruldu. Servis etkinleştirilemedi.", "App installed. Service could not be enabled.");
        AddInternational("setup.appOnly", "App installiert. Dienst konnte nicht aktiviert werden.", "Application installée. Service non activé.", "Aplicación instalada. No se pudo activar el servicio.");
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
        Add("app.title", "DPI / DNS Zorlaması Kaldırıcı", "DPI / DNS Block Remover");
        Add("app.sub", "DPI Fix Engine {0} · otomatik yapılandırma · kalıcı servis",
                      "DPI Fix Engine {0} · automatic setup · persistent service");
        Add("sec.status", "DURUM", "STATUS");
        Add("sec.actions", "İŞLEMLER", "ACTIONS");
        Add("sec.settings", "AYARLAR", "SETTINGS");
        Add("sec.log", "GÜNLÜK", "LOG");

        Add("st.running", "ÇALIŞIYOR", "RUNNING");
        Add("st.stopped", "DURDU", "STOPPED");
        Add("st.starting", "BAŞLIYOR", "STARTING");
        Add("st.stopping", "DURUYOR", "STOPPING");
        Add("st.absent", "KURULU DEĞİL", "NOT INSTALLED");
        Add("st.unknown", "BİLİNMİYOR", "UNKNOWN");

        Add("lbl.service", "Servis", "Service");
        Add("lbl.engine", "Motor", "Engine");
        Add("lbl.profile", "Yapılandırma", "Configuration");
        Add("lbl.sites", "Site listesi", "Site list");
        Add("lbl.entries", "{0} alan adı", "{0} domains");

        Add("btn.install", "Kur / Onar", "Install / Repair");
        Add("btn.stop", "Durdur", "Stop");
        Add("btn.remove", "Kaldır", "Remove");
        Add("btn.test", "Bağlantı Testi", "Connection Test");
        Add("btn.list", "Site Listesi", "Site List");
        Add("btn.update", "Güncelleme", "Update");
        Add("btn.log", "Günlüğü Aç", "Open Log");
        Add("btn.close", "Kapat", "Close");

        Add("chk.doh", "DNS-over-HTTPS (Cloudflare)", "DNS-over-HTTPS (Cloudflare)");
        Add("chk.watch", "Ağ değişince bağlantıyı test et", "Test after network changes");
        Add("chk.verify", "Dosya bütünlüğü doğrulaması", "Payload integrity check");
        Add("log.engine", "Motor: {0}", "Engine: {0}");
        Add("log.enginefb", "Rust motoru kullanılamıyor, GoodbyeDPI'ya dönüldü.",
                            "Rust engine unavailable, falling back to GoodbyeDPI.");

        Add("confirm.remove", "Servis, DNS ayarı ve kopyalanan dosyalar geri alınacak. Onaylıyor musunuz?",
                             "Service, DNS setting and copied files will be reverted. Continue?");
        Add("confirm.title", "Kaldır", "Remove");

        Add("hint.ready", "Hazır. 'Kur / Onar' ile tek tuşla kurulum yapılır.",
                          "Ready. One click with 'Install / Repair'.");
        Add("hint.tip", "İpucu: açılmayan siteyi 'Site Listesi'ne ekleyip 'Kur / Onar' deyin.",
                        "Tip: add a blocked site to 'Site List' then press 'Install / Repair'.");
        Add("hint.background", "Kurulumdan sonra bu pencereyi kapatsanız da servis çalışmaya devam eder.",
                               "After install you can close this window; the service keeps running.");
        Add("hint.log", "Günlük dosyası: {0}", "Log file: {0}");

        Add("run.install", "KURULUM / ONARIM", "INSTALL / REPAIR");
        Add("run.stop", "SERVİS DURDURULUYOR", "STOPPING SERVICE");
        Add("run.remove", "KALDIRMA", "REMOVAL");
        Add("run.test", "BAĞLANTI TESTİ", "CONNECTION TEST");
        Add("run.update", "GÜNCELLEME KONTROLÜ", "UPDATE CHECK");

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
        Add("log.trydoh", "Ayarlar’dan DNS-over-HTTPS’i seçip Kur / Onar ile tekrar deneyebilirsiniz.",
                          "Enable DNS-over-HTTPS in Settings and try Install / Repair again.");
        Add("log.engineexit", "Motor durdu ({0}). {1}", "The engine exited ({0}). {1}");
        Add("log.step3", "[3/6] DNS katmanı", "[3/6] DNS layer");
        Add("log.step4", "[4/6] Bu hat için en uygun yapılandırma aranıyor (~20 sn)",
                         "[4/6] Finding the best configuration for this network (~20s)");
        Add("log.step5", "[5/6] Servis oluşturuluyor", "[5/6] Creating service");
        Add("log.step6", "[6/6] Başlatılıyor", "[6/6] Starting");
        Add("log.trying", "Deneniyor: {0}", "Trying: {0}");
        Add("log.next", "Uygun değil, sonraki adaya geçiliyor.", "Not suitable, moving to next candidate.");
        Add("log.none", "Mevcut erişimi koruyup bir hedef siteye erişim kazandıran profil bulunamadı.",
                        "No profile restored a target site while preserving existing access.");
        Add("log.chosen", "Seçilen: {0}", "Selected: {0}");
        Add("log.svcfail", "Servis başlamadı. Ayrıntı: {0}", "Service did not start. Detail: {0}");
        Add("log.done", "Kurulum tamam. Uygulama kapansa da servis çalışır.",
                        "Install complete. The service keeps running after you close the app.");
        Add("log.failed", "Kurulum tamamlanamadı.", "Install could not be completed.");
        Add("log.removed", "Kaldırma tamamlandı.", "Removal complete.");
        Add("log.testhead", "Bağlantı testi (servis: {0})", "Connection test (service: {0})");
        Add("log.testok", "Tüm kontroller geçti.", "All checks passed.");
        Add("log.testfail", "Sorun sürüyorsa 'Kur / Onar' ile yapılandırmayı değiştirin.",
                            "If the problem persists use 'Install / Repair' to change configuration.");
        Add("log.testwarn", "Not: servis çalışmıyor, sonuç yanıltıcı olabilir.",
                            "Note: service is not running, result may be misleading.");
        Add("log.reach", "erişilebilir", "reachable");
        Add("log.blocked", "erişilemedi", "unreachable");
        Add("log.req", " (zorunlu)", " (required)");
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

        AddInternational("app.title", "DPI Fix", "DPI Fix", "DPI Fix");
        AddInternational("app.sub", "Verbindung und Windows-Dienst", "Connexion et service Windows", "Conexión y servicio de Windows");
        AddInternational("sec.status", "STATUS", "ÉTAT", "ESTADO");
        AddInternational("sec.actions", "AKTIONEN", "ACTIONS", "ACCIONES");
        AddInternational("sec.settings", "EINSTELLUNGEN", "PARAMÈTRES", "AJUSTES");
        AddInternational("sec.log", "PROTOKOLL", "JOURNAL", "REGISTRO");
        AddInternational("st.running", "AKTIV", "ACTIF", "ACTIVO");
        AddInternational("st.stopped", "GESTOPPT", "ARRÊTÉ", "DETENIDO");
        AddInternational("st.starting", "STARTET", "DÉMARRAGE", "INICIANDO");
        AddInternational("st.stopping", "STOPPT", "ARRÊT EN COURS", "DETENIENDO");
        AddInternational("st.absent", "NICHT INSTALLIERT", "NON INSTALLÉ", "SIN INSTALAR");
        AddInternational("st.unknown", "UNBEKANNT", "INCONNU", "DESCONOCIDO");
        AddInternational("lbl.service", "Dienst", "Service", "Servicio");
        AddInternational("lbl.engine", "Engine", "Moteur", "Motor");
        AddInternational("lbl.profile", "Konfiguration", "Configuration", "Configuración");
        AddInternational("lbl.sites", "Websites", "Sites", "Sitios");
        AddInternational("lbl.entries", "{0} Domains", "{0} domaines", "{0} dominios");
        AddInternational("btn.install", "Installieren", "Installer", "Instalar");
        AddInternational("btn.stop", "Stoppen", "Arrêter", "Detener");
        AddInternational("btn.remove", "Entfernen", "Supprimer", "Desinstalar");
        AddInternational("btn.test", "Verbindungstest", "Tester la connexion", "Probar conexión");
        AddInternational("btn.list", "Websites", "Liste des sites", "Lista de sitios");
        AddInternational("btn.update", "Updates", "Mises à jour", "Actualizaciones");
        AddInternational("btn.log", "Protokoll", "Ouvrir le journal", "Abrir registro");
        AddInternational("chk.watch", "Bei Netzwechsel testen", "Tester après un changement de réseau", "Probar al cambiar de red");
        AddInternational("chk.verify", "Dateiintegrität prüfen", "Vérifier les fichiers", "Verificar archivos");
        AddInternational("confirm.title", "Entfernen", "Supprimer", "Desinstalar");
        AddInternational("confirm.remove", "Dienst entfernen und DNS wiederherstellen?", "Supprimer le service et restaurer le DNS ?", "¿Desinstalar el servicio y restaurar el DNS?");
        AddInternational("hint.ready", "Bereit zur Installation.", "Prêt pour l’installation.", "Listo para instalar.");
        AddInternational("hint.tip", "Fügen Sie eine Website zur Liste hinzu und installieren Sie den Dienst erneut.", "Ajoutez un site à la liste, puis réinstallez le service.", "Añade un sitio a la lista y vuelve a instalar el servicio.");
        AddInternational("hint.log", "Protokoll: {0}", "Journal : {0}", "Registro: {0}");
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
