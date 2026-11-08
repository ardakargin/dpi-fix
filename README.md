# DPI Fix — tek dosya uygulama + kurulum

Türkiye'deki DPI/DNS zorlamasını kaldıran Windows uygulaması. Motor: GoodbyeDPI 0.2.3rc3.

**İndir:** https://github.com/ardakargin/dpi-fix/releases/latest

## Klasör yapısı

```
dpi-fix\
  build.cmd           Derleme (çıktı: dist\)
  src\
    DpiCore.cs        Motor: payload, servis, profil seçimi, DoH, log, ağ izleyici
    DpiFix.cs         GUI + komut satırı
    Setup.cs          Kurulum/kaldırma
    app.manifest      requireAdministrator
  payload\            Gömülecek ikililer (goodbyedpi + WinDivert) — depoda, derleme girdisi
  LICENSES\           Apache-2.0 / LGPL-3.0 / BSD metinleri
  LICENSE             MIT (bu projenin kodu)
  THIRD-PARTY.md      Üçüncü taraf bileşenler ve uyum notları
  dist\               DERLEME ÇIKTISI — depoda tutulmaz, Release ile dağıtılır
    DpiFix.exe          Uygulama (motor gömülü, tek dosya)
    Setup.exe           Kurulum (DpiFix.exe gömülü, tek dosya)
    selftest.exe        Teşhis (UAC istemez)
```

Depoda **bulunmayan**, yalnızca yerelde duran klasörler: `site\` (tanıtım sitesi) ve
`design\` (Stitch tasarım kaynakları).

## Site tasarımı (Stitch CLI)

Tasarım Google Stitch ile üretildi, sonra projeye uyarlandı (sahte metrikler gerçek değerlerle
değiştirildi, sürüm/lisans düzeltildi, indirme düğmeleri `Setup.exe`'ye bağlandı).

```
Stitch proje : 9199455847391265127
Stitch screen: b0d31a3d10794517838e08ebfc68cba1
Canvas       : https://stitch.google.com/projects/9199455847391265127?node-id=b0d31a3d10794517838e08ebfc68cba1
```

Yeniden üretmek / varyant almak için:

```
stitch generate screen --project 9199455847391265127 --screen b0d31a3d10794517838e08ebfc68cba1 ^
  --count 3 --creative-range EXPLORE --aspects LAYOUT,COLOR_SCHEME
stitch download screen <yeni-screen-id> --project 9199455847391265127 --html --image --output design
stitch serve --project 9199455847391265127
```

Notlar:
- `index.html` Tailwind CDN + Google Fonts kullanır (3 dış istek). Üretim için Tailwind'i
  derleyip gömmek daha hızlı olur; şu hâliyle hemen açılıyor.
- Sayfa `Setup.exe`'yi aynı klasörden indirir; yayına alırken `Setup.exe`'yi sitenin köküne koyun.
- Yerel önizleme: `design\render.png` (Edge headless ile alınmış tam sayfa görüntüsü).

## Kullanıcı akışı

1. `Setup.exe` çalıştır (UAC) → Program Files'a kurar, başlat menüsü kısayolu ve
   Program Ekle/Kaldır kaydı oluşturur, servisi kurup başlatır.
2. Sonrasında `DpiFix.exe` üzerinden: Kur/Onar, Durdur, Kaldır, Bağlantı Testi,
   Site Listesi, Güncelleme.
3. Pencere kapatılabilir — Windows servisi çalışmaya devam eder, yeniden başlatmada geri gelir.

## Komut satırı

```
DpiFix.exe --install     sessiz kurulum
DpiFix.exe --uninstall   kaldırma (DNS dahil geri alır)
DpiFix.exe --status      servis durumu
DpiFix.exe --test        bağlantı testi
DpiFix.exe --update      upstream sürüm kontrolü
DpiFix.exe --selftest    teşhis → %TEMP%\dpifix-selftest.txt
```

## v2'de eklenenler

| Özellik | Nasıl |
|---|---|
| **Site listesi modu** | `--blacklist` — bypass yalnızca listedeki alan adlarına uygulanır; diğer trafik dokunulmadan akar. Dosya: `C:\ProgramData\DpiFix\blacklist.txt` |
| **TTL sorunu** | `-5` (auto-ttl içeriyor) bırakıldı; `-9` / `-6` kullanılıyor. Site bozulmasının ana sebebi buydu |
| **QUIC engelleme** | `-9` içindeki `-q`. Tarayıcı HTTP/3'ten TCP'ye düşer |
| **DNS-over-HTTPS** | Cloudflare, `netsh`/PowerShell ile. Önceki DNS ayarı `dns-backup.json`'a yazılır, kaldırmada geri yüklenir |
| **Otomatik yapılandırma** | 3 aday sırayla denenir: `-9 --blacklist` → `-9` → `-6`. Kabul kuralı: zorunlu siteler (google, youtube) açık **ve** listedeki hedeflerden en az biri açık |
| **Dinamik test listesi** | Test hedefleri `blacklist.txt`'ten okunur (ilk 6 alan adı) — sorunlu siteyi listeye ekleyince prob onu da ölçer |
| **TLS düzeltmesi** | Prob artık tarayıcı gibi TLS 1.2/1.3 ister. .NET varsayılanı TLS 1.0 olduğu için prob yanlış "engelli" diyordu |
| **DNS önbelleği** | Her probdan önce `ipconfig /flushdns` |
| **Ağ izleyici** | `NetworkAddressChanged` + 30 sn debounce; yalnızca mevcut yapılandırma **başarısızsa** yeniden seçer |
| **Kalıcı günlük** | `C:\ProgramData\DpiFix\log.txt` |
| **Bütünlük doğrulaması** | Gömülü 7 dosyanın SHA-256'sı çalışma anında karşılaştırılır |
| **Güncelleme kontrolü** | GitHub releases API, sürüm karşılaştırması (indirme yapmaz, bildirir) |
| **Hata toparlama** | `sc failure ... restart/5000/15000/60000` |

## Derleme

```
build.cmd
```
`.NET Framework` `csc.exe` kullanır — NuGet, SDK, runtime kurulumu yok.
`build.cmd`, `payload\` altındaki 7 dosyayı DpiFix.exe'ye, DpiFix.exe'yi Setup.exe'ye gömer.

## Teşhis

```
selftest.exe --selftest
type %TEMP%\dpifix-selftest.txt
```
Çıktı: mimari, gömülü kaynak doğrulaması (7/7), servis durumu ve **servise yazılmış gerçek
komut satırı**, ağ testleri.

## Depo

**https://github.com/ardakargin/dpi-fix** — public, MIT.

Güncelleme akışı:

```
git add -A
git commit -m "..."
git push
```

`gh` kurulu ama bu kabuğun PATH'inde olmayabilir; gerekirse tam yol:
`"C:\Program Files\GitHub CLI\gh.exe"`.

İlk commit `Arda Karğın <335661598+ardakargin@users.noreply.github.com>` kimliğiyle
atıldı, yani GitHub hesabına bağlı görünür.

## Notlar / riskler

- Kurulum ve servis **yönetici** ister; yönetici olmayan oturumda kurulum test edilemez.
- Kaspersky tamamen kaldırılmadıkça GoodbyeDPI çalışmaz.
- DNS-over-HTTPS `AllowFallbackToUdp=true` ile kurulur: DoH engellenirse internet kopmaz.
- Site `index.html` tek dosyadır; `Setup.exe` ile aynı klasöre koyarsanız indirme bağlantısı çalışır.
- Yasal uyarı değişmedi: kullanım sorumluluğu kullanıcıda.