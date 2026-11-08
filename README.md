# DPI Fix — Çok Platformlu DPI / DNS Zorlaması Kaldırıcı

Türkiye'deki internet servis sağlayıcılarının uyguladığı Derin Paket İncelemesi (DPI) ve DNS engellemelerini hız kaybı olmadan yerel düzeyde aşan açık kaynaklı yazılım.

**Referans Projeler:**
- [ValdikSS/GoodbyeDPI](https://github.com/ValdikSS/GoodbyeDPI) (Orijinal DPI circumvention motoru)
- [cagritaskn/GoodbyeDPI-Turkey](https://github.com/cagritaskn/GoodbyeDPI-Turkey) (Türkiye ISP kuralları & macOS uyarlaması)

**Son Sürüm İndir (Windows):**  
👉 [GitHub Releases: Setup.exe](https://github.com/ardakargin/dpi-fix/releases/latest/download/Setup.exe) (Tek tıkla kurulum)

---

## 🌟 Öne Çıkan Özellikler

- **Çift Motor Desteği (C & Rust):**
  - **GoodbyeDPI 0.2.3rc3 (C):** Yıllardır test edilmiş, kararlı varsayılan motor.
  - **DPI Fix Engine (Rust):** Sıfır harici crate ile Rust dilinde sıfırdan yazılmış, yerel FFI üzerinden WinDivert ile çalışan modern motor (`AYARLAR → Rust motoru`).
- **Site Listesi Modu (`--blacklist`):** Yalnızca hedeflenen alan adlarına müdahale edilir (`discord.com`, `instagram.com` vb.). Diğer tüm internet trafiği orijinal hızında ve dokunulmadan akar.
- **Kalıcı Windows Servisi:** Kurulumdan sonra uygulama arayüzünü kapatabilirsiniz. Bilgisayar yeniden başlatıldığında otomatik çalışır, çökme durumunda 5/15/60 sn içinde kendini toparlar.
- **DNS-over-HTTPS (DoH):** Cloudflare şifreli DNS desteği; uygulama kaldırıldığında eski DNS ayarları otomatik geri yüklenir.
- **QUIC / HTTP3 Engelleme (`-q`):** Tarayıcıyı zorunlu TCP'ye düşürerek parçalama stratejilerinin tam verimle çalışmasını sağlar.
- **İki Dil Desteği (TR / EN):** Arayüzün sağ üst köşesinden tek tıkla dil değişimi.
- **Koyu Modern Tasarım:** Tamamen özel çizilmiş kontroller, canlı durum izleme, log paneli ve koyu başlık çubuğu.

---

## 🖥️ Platform Desteği

| Platform | Arayüz | Motor | Servis Yöntemi | Durum |
|---|---|---|---|---|
| **Windows 10 / 11** (32 & 64-bit) | Modern GUI (`DpiFix.exe`) | GoodbyeDPI (C) & DPI Fix (Rust) | Windows NT Servisi | **Tam Destek** |
| **Linux** (systemd) | CLI (`linux/dpi-fix.sh`) | zapret / nfqws | systemd service | **Destekleniyor** |
| **macOS 12+** (Intel & Apple Silicon) | CLI (`macos/dpi-fix.sh`) | byedpi (`ciadpi`) | launchd LaunchDaemon | **Destekleniyor** |

- **Linux Kurulumu:** Ayrıntılar için [linux/README.md](linux/README.md)
- **macOS Kurulumu:** Ayrıntılar için [macos/README.md](macos/README.md)

---

## 🦀 Özel Rust Motoru (`engine/`)

Bu proje, GoodbyeDPI C motoruna ek olarak bağımsız bir **Rust motoru** barındırır:

- **Sıfır Harici Crate:** Yalnızca Rust standart kütüphanesi kullanılır.
- **Dinamik FFI:** `WinDivert.lib` gerektirmez; `WinDivert.dll` çalışma anında dinamik yüklenir.
- **Tam Protokol Ayrıştırma:** IPv4, IPv6, TCP, UDP ayrıştırma, birin tümleyeni sağlama toplamı (checksum) hesaplama.
- **SNI ve Host Analizi:** TLS ClientHello paketinden Server Name Indication (SNI) ve HTTP Host başlığını gerçek zamanlı tespit etme.
- **Parçalama & Sahte Paket:** SNI içinden bölme, ters sırada segment gönderimi, geçmiş TCP SEQ, bozuk checksum ve TTL manipülasyonu.
- **25 Birim Testi:** Tüm paket ve parçalama mantığı `cargo test` ile doğrulanmıştır.

Rust motorunu test etmek için:
```bash
cd engine
cargo test --release
```

---

## 📦 Proje Yapısı

```
dpi-fix\
  build.cmd             Windows derleme betiği (.NET + Rust motoru -> dist\)
  src\
    Ui.cs               Koyu tema, özel kontroller, TR/EN yerelleştirme
    DpiCore.cs          Servis yönetimi, DNS katmanı, test mekanizması
    DpiFix.cs           Modern uygulama arayüzü
    Setup.cs            Tek dosya kurulum ve kaldırma aracı
    app.manifest        Yönetici yetkisi (requireAdministrator)
  engine\               Özel Rust DPI atlatma motoru
    Cargo.toml
    src\
      main.rs           CLI & motor giriş noktası
      engine.rs         Paket döngüsü & baypas stratejileri
      packet.rs         IP/TCP/UDP ayrıştırıcı & checksum
      sniff.rs          TLS SNI & HTTP Host ayrıştırıcı
      hostlist.rs       Site listesi eşleme algoritması
      windivert.rs      WinDivert FFI katmanı
  linux\                Linux zapret/systemd yönetim betikleri
  macos\                macOS byedpi/launchd yönetim betikleri
  payload\              WinDivert sürücüleri & GoodbyeDPI ikilileri (derleme girdisi)
  LICENSES\             Apache-2.0, LGPL-3.0, BSD lisans metinleri
  LICENSE               MIT Lisansı (bu projenin kodu)
  THIRD-PARTY.md        Üçüncü taraf bileşen uyum beyanları
  dist\                 DERLEME ÇIKTILARI (Release dosyaları):
    Setup.exe           Kurulum aracı (DpiFix.exe içinde gömülü)
    DpiFix.exe          Uygulama (tüm motorlar gömülü, taşınabilir)
    selftest.exe        Teşhis aracı (yönetici yetkisi istemez)
```

---

## 🔨 Derleme (Build)

Gereksinimler:
- Windows 10/11
- .NET Framework 4.x (`csc.exe` dahildir)
- (Opsiyonel) Rust 1.80+ (Rust motorunu derlemek için; yoksa GoodbyeDPI ile derlenir)

Kök dizinde:
```cmd
build.cmd
```
Tüm ikili dosyalar derlenip `dist\` klasörüne yerleştirilir.

---

## ⚠️ Notlar ve Sorumluluk Reddi

- Windows'ta servis kurmak ve ağ sürücüsünü çalıştırmak için **Yönetici Yetkisi** zorunludur.
- Bilgisayarınızda Kaspersky antivirüs yazılımı varsa GoodbyeDPI/WinDivert sürücüsünü engeller; Kaspersky'ın tamamen kaldırılması önerilir.
- Bu yazılım yalnızca eğitim ve araştırma amacıyla geliştirilmiştir. Kullanımdan doğan sorumluluk kullanıcıya aittir.
