# DPI Fix — Çok Platformlu DPI / DNS Zorlaması Kaldırıcı

Derin Paket İncelemesi (DPI) ve DNS kaynaklı erişim sorunları için açık kaynaklı bir masaüstü uygulaması. Sonuçlar ağ, servis sağlayıcı ve hedef siteye göre değişebilir.

**Referans Projeler:**
- [ValdikSS/GoodbyeDPI](https://github.com/ValdikSS/GoodbyeDPI) (Orijinal DPI circumvention motoru)
- [cagritaskn/GoodbyeDPI-Turkey](https://github.com/cagritaskn/GoodbyeDPI-Turkey) (Türkiye ISP kuralları & macOS uyarlaması)
- [ByeDPI](https://github.com/hufrea/byedpi) (yerel SOCKS proxy, macOS yardımcısının motoru ve doğrudan Windows servis mimarisi için referans)
- [zapret](https://github.com/bol-van/zapret) (Linux yardımcısının `nfqws` motoru) ve [zapret2](https://github.com/bol-van/zapret2) (düşük kaynaklı cihazlara yönelik mimari referansı)

**Windows indirmeleri**

- [Windows x64 için Setup.exe](https://github.com/ardakargin/dpi-fix/releases/latest/download/Setup.exe)
- [Sürüm notları ve diğer indirmeler](https://github.com/ardakargin/dpi-fix/releases/latest)
- [DPI Fix tanıtım sitesi](https://ardakargin.github.io/dpi-fix/)

---

## 🌟 Öne Çıkan Özellikler

- **DPI Fix Engine:** Windows servisinde çalışan, harici crate kullanmayan yerel Rust motoru.
- **Alan adı kapsamı:** Motor yalnızca listedeki alan adları için tanıyabildiği HTTP veya TLS başlangıç paketlerini işler.
- **Kalıcı Windows Servisi:** Doğrulanan profil Windows açılışında otomatik çalışır. Beklenmeyen kapanışlarda Windows servis kurtarma ayarları devreye girer.
- **DNS-over-HTTPS (DoH):** Yeni kurulumlarda kapalıdır. Açıldığında Windows'ta yapılandırılmış Cloudflare DoH kaydını kullanır. Servis durdurulurken veya kaldırılırken önceki DNS ayarı geri yüklenir.
- **Bağlantı kontrolü:** Örnek sitelerde HTTPS yanıtlarını motor kapalıyken, profil denemesinde ve servis çalışırken karşılaştırır. Çalışan bir site erişimini kaybederse veya hiçbir hedefte erişim kazanımı ölçülmezse servis otomatik başlangıca alınmaz. Kontrol, listedeki ilk altı geçerli hedef ve üç genel siteyle sınırlıdır.
- **Dil desteği:** Ana pencere Türkçe, İngilizce, Almanca, Fransızca ve İspanyolca. Yeni kurulumlar desteklenen Windows dilini kullanır.
- **Koyu Modern Tasarım:** Tamamen özel çizilmiş kontroller, canlı durum izleme, log paneli ve koyu başlık çubuğu.

---

## 🖥️ Platform Desteği

| Platform | Arayüz | Motor | Servis Yöntemi | Durum |
|---|---|---|---|---|
| **Windows 10 / 11** (x64) | Modern GUI (`DpiFix.exe`) | DPI Fix Engine | Windows NT Servisi | **x64 paket** |
| **Linux** (systemd) | CLI (`linux/dpi-fix.sh`) | zapret / nfqws | systemd service | **Deneysel** |
| **macOS 12+** (Intel & Apple Silicon) | CLI (`macos/dpi-fix.sh`) | byedpi (`ciadpi`) | launchd LaunchDaemon | **Deneysel** |

- **Linux Kurulumu:** Ayrıntılar için [linux/README.md](linux/README.md)
- **macOS Kurulumu:** Ayrıntılar için [macos/README.md](macos/README.md)

---

## 🦀 Özel Rust Motoru (`engine/`)

Windows paketi DPI Fix Engine, WinDivert DLL ve WinDivert sürücüsünü içerir.
Motor tek süreçte çalışır. TCP paketleri için iki tamponu yeniden kullanır ve bağlantı başına önbellek tutmaz.
HTTP ve TLS başlangıçları alan adı listesine göre değerlendirilir. QUIC, ECH ve parçalı TLS el sıkışmaları bu sürümün kapsamı dışındadır.

Rust motorunu test etmek için:
```bash
cd engine
cargo test --release --locked
```

---

## 📦 Proje Yapısı

```
dpi-fix\
  build.cmd             Windows derleme betiği (.NET + Rust motoru -> dist\)
  src\
    Ui.cs               Koyu tema, özel kontroller ve yerelleştirme
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
      service.rs        Yerel Windows servis giriş noktası
      hostlist.rs       Site listesi eşleme algoritması
      windivert.rs      WinDivert FFI katmanı
  linux\                Linux zapret/systemd yönetim betikleri
  macos\                macOS byedpi/launchd yönetim betikleri
  payload\              WinDivert DLL ve sürücüsü (derleme girdisi)
  LICENSES\             Apache-2.0, LGPL-3.0, BSD lisans metinleri
  LICENSE               MIT Lisansı (bu projenin kodu)
  THIRD-PARTY.md        Üçüncü taraf bileşen uyum beyanları
  dist\                 DERLEME ÇIKTILARI (Release dosyaları):
    Setup.exe           Kurulum aracı (DpiFix.exe içinde gömülü)
    DpiFix.exe          Uygulama (DPI Fix Engine gömülü)
    selftest.exe        Teşhis aracı (yönetici yetkisi istemez)
```

---

## 🔨 Derleme (Build)

Gereksinimler:
- Windows 10/11
- .NET Framework 4.x (`csc.exe` dahildir)
- Rust 1.80+ ve x86_64-pc-windows-msvc hedefi

Kök dizinde:
```cmd
build.cmd
```
Tüm ikili dosyalar derlenip `dist\` klasörüne yerleştirilir.

---

## ⚠️ Notlar ve Sorumluluk Reddi

- Windows'ta servis kurmak ve ağ sürücüsünü çalıştırmak için **Yönetici Yetkisi** zorunludur.
- Bu yazılım yalnızca eğitim ve araştırma amacıyla geliştirilmiştir. Kullanımdan doğan sorumluluk kullanıcıya aittir.
