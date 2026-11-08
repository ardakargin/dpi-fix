# Linux

**Deneysel.** Windows uygulaması .NET/WinForms olduğu için Linux'ta çalışmaz — aynı arayüzü
Linux'a taşımak ayrı bir yazılım (Avalonia/GTK) demek. Bunun yerine burada yalnızca
**servis yaşam döngüsünü yöneten** küçük bir betik var; paket parçalama işini
[zapret](https://github.com/bol-van/zapret) yapar.

| | Windows | Linux |
|---|---|---|
| Arayüz | GUI (DpiFix.exe) | CLI (`dpi-fix.sh`) |
| Motor | GoodbyeDPI + WinDivert | zapret / nfqws |
| Kurulum | Setup.exe, servis | `dpi-fix.sh install`, systemd |
| Kalıcılık | Windows servisi | systemd birimi |

`nfqws` argümanları ağdan ağa değişir. Doğru değerleri tahmin etmek yerine zapret'in kendi
`blockcheck.sh` betiğiyle bulun, sonra buraya yazın:

```bash
sudo ./dpi-fix.sh install
sudo ./dpi-fix.sh params "--dpi-desync=split2 --dpi-desync-split-pos=2 --dpi-desync-fooling=md5sig"
./dpi-fix.sh test
```

## Gereksinimler

- root (systemd, `iptables`/`nftables`)
- zapret derlenmiş `nfqws` (`/opt/zapret/nfqws` veya PATH'te)
- `curl` (test için)

## Sınırlar

- Windows sürümündeki site listesi (`--blacklist`) eşdeğeri zapret'te `--hostlist` ile yapılır;
  bu betik henüz onu yönetmiyor.
- macOS desteklenmiyor: çekirdek seviyesinde eşdeğer bir paket filtresi yolu yok.

> Katkı: `--hostlist` desteği ve dağıtım paketleri (deb/rpm) eklenebilir.