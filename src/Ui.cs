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

    static readonly Dictionary<string, string> TR = new Dictionary<string, string>();
    static readonly Dictionary<string, string> EN = new Dictionary<string, string>();

    static void Add(string key, string tr, string en) { TR[key] = tr; EN[key] = en; }

    static Loc()
    {
        Add("app.title", "DPI / DNS Zorlaması Kaldırıcı", "DPI / DNS Block Remover");
        Add("app.sub", "GoodbyeDPI {0} motoru · otomatik yapılandırma · kalıcı servis",
                      "GoodbyeDPI {0} engine · automatic setup · persistent service");
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
        Add("chk.watch", "Ağ değişince otomatik yeniden seç", "Re-detect when network changes");
        Add("chk.verify", "Dosya bütünlüğü doğrulaması", "Payload integrity check");
        Add("chk.rust", "Rust motoru (deneysel)", "Rust engine (experimental)");
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
        Add("log.step1", "[1/6] Motor dosyaları çıkarılıyor", "[1/6] Extracting engine files");
        Add("log.step2", "[2/6] Eski servisler durduruluyor", "[2/6] Stopping previous services");
        Add("log.step3", "[3/6] DNS katmanı", "[3/6] DNS layer");
        Add("log.step4", "[4/6] Bu hat için en uygun yapılandırma aranıyor (~20 sn)",
                         "[4/6] Finding the best configuration for this network (~20s)");
        Add("log.step5", "[5/6] Servis oluşturuluyor", "[5/6] Creating service");
        Add("log.step6", "[6/6] Başlatılıyor", "[6/6] Starting");
        Add("log.trying", "Deneniyor: {0}", "Trying: {0}");
        Add("log.next", "Uygun değil, sonraki adaya geçiliyor.", "Not suitable, moving to next candidate.");
        Add("log.none", "Hiçbir yapılandırma doğrulanamadı; varsayılan uygulanıyor.",
                        "No configuration verified; applying default.");
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
        Add("log.blocked", "engelli", "blocked");
        Add("log.req", " (zorunlu)", " (required)");
        Add("log.updscan", "Güncelleme kontrolü...", "Checking for updates...");
        Add("log.updcur", "Kurulu motor: GoodbyeDPI {0} | DpiFix v{1}",
                          "Installed engine: GoodbyeDPI {0} | DpiFix v{1}");
        Add("log.updnew", "Upstream son sürüm: {0}", "Latest upstream: {0}");
        Add("log.updhas", "Yeni motor sürümü mevcut.", "A newer engine version is available.");
        Add("log.updnone", "Motor güncel.", "Engine is up to date.");
        Add("log.netchg", "Ağ değişikliği algılandı; mevcut yapılandırma test ediliyor...",
                          "Network change detected; testing current configuration...");
        Add("log.netok", "Mevcut yapılandırma hâlâ çalışıyor, değişiklik yapılmadı.",
                         "Current configuration still works, no change made.");
        Add("log.netbad", "Mevcut yapılandırma çalışmıyor; yeniden seçiliyor.",
                          "Current configuration stopped working; re-detecting.");
        Add("log.dohon", "DNS-over-HTTPS (Cloudflare) etkinleştirildi, önceki DNS ayarı yedeklendi.",
                         "DNS-over-HTTPS (Cloudflare) enabled, previous DNS setting backed up.");
        Add("log.dohoff", "DNS-over-HTTPS kapalı (ayar).", "DNS-over-HTTPS disabled (setting).");
        Add("log.dir", "Yönlendirme modu: {0}", "Redirect mode: {0}");
        Add("log.blacklist", "Varsayılan site listesi oluşturuldu: {0}", "Default site list created: {0}");
    }

    public static string T(string key)
    {
        Dictionary<string, string> d = Lang == "en" ? EN : TR;
        string v;
        return d.TryGetValue(key, out v) ? v : key;
    }

    public static string T(string key, params object[] args)
    {
        return string.Format(T(key), args);
    }
}