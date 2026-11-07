# Üçüncü taraf bileşenler

Bu proje kendi kodunun yanında **değiştirilmemiş** üçüncü taraf ikilileri dağıtır.
Tam lisans metinleri `LICENSES/` klasöründedir.

| Bileşen | Dosya | Lisans | Kaynak |
|---|---|---|---|
| GoodbyeDPI 0.2.3rc3 (Türkiye fork) | `payload/*/goodbyedpi.exe` | Apache-2.0 | https://github.com/ValdikSS/GoodbyeDPI |
| WinDivert 2.2 | `payload/*/WinDivert.dll`, `WinDivert*.sys` | LGPL-3.0 | https://github.com/basil00/Divert |
| getline (NetBSD) | GoodbyeDPI içinde statik | BSD-2-Clause | NetBSD libc |
| uthash | GoodbyeDPI içinde statik | BSD-1-Clause | https://troydhanson.github.io/uthash/ |

## LGPL-3.0 uyumu (WinDivert)

WinDivert ikilileri **değiştirilmeden** yeniden dağıtılmaktadır. WinDivert ayrı bir
süreçte/çekirdek sürücüsü olarak çalışır ve bu projenin koduyla statik olarak bağlanmaz.
WinDivert kaynak koduna yukarıdaki bağlantıdan ulaşılabilir. Lisans metni:
`LICENSES/LICENSE-windivert.txt`.

## Apache-2.0 uyumu (GoodbyeDPI)

`goodbyedpi.exe` değiştirilmeden dağıtılır. Lisans metni:
`LICENSES/LICENSE-goodbyedpi.txt`. Bildirimler korunmuştur.

## Bu projenin kodu

`src/` altındaki C# kodu ve `site/` altındaki tasarım MIT lisanslıdır (bkz. `LICENSE`).