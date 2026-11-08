# DPI Fix — macOS

Bu klasör, [cagritaskn/GoodbyeDPI-Turkey](https://github.com/cagritaskn/GoodbyeDPI-Turkey)
ve [byedpi](https://github.com/hufrea/byedpi) referans alınarak hazırlanmış macOS desteğini içerir.

## Nasıl çalışır?

WinDivert yalnızca Windows'a özel bir çekirdek sürücüsüdür; macOS'ta eşdeğeri yoktur.
Bu nedenle macOS sürümü, paket parçalama ve TLS el sıkışma manipülasyonunu
yerel bir **userspace SOCKS5 proxy** (`ciadpi` / `byedpi`) ve `launchd`
LaunchDaemon servisi aracılığıyla gerçekleştirir.

- TLS ClientHello bölme (`--split 2`)
- Ters sıralı segment gönderimi (`--disorder 3`)
- TLS kayıt bölme (`--tlsrec 1+s`)
- HTTP başlık modifikasyonu (`--mod-http h,d`)

## Kurulum ve Kullanım

```bash
# 1. Gerekirse byedpi'yi kurun:
brew install byedpi

# 2. DPI Fix servisini kurun ve proxy'yi otomatik aktifleştirin:
sudo ./dpi-fix.sh install

# 3. Bağlantıyı test edin:
./dpi-fix.sh test

# 4. Servisi kaldırmak için:
sudo ./dpi-fix.sh remove
```

## Desteklenen Sürümler

- macOS 12 (Monterey) ve üstü
- Apple Silicon (M1/M2/M3/M4) & Intel x86_64
