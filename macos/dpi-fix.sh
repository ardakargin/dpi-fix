#!/usr/bin/env bash
# DPI Fix for macOS — byedpi (ciadpi) + launchd tabanlı yardımcı
#
# Referans: cagritaskn/GoodbyeDPI-Turkey (macOS portu)
#
# macOS'ta WinDivert çekirdek sürücüsü bulunmadığından, paket parçalama
# ve DPI atlatma teknikleri userspace SOCKS5 ve launchd servisi olarak
# uygulanır.
#
# Kullanım:
#   sudo ./dpi-fix.sh install     # byedpi kur, launchd servisi ve SOCKS proxy'yi ayarla
#   sudo ./dpi-fix.sh remove      # servisi ve proxy ayarlarını kaldır
#   sudo ./dpi-fix.sh start|stop|restart|status
#   ./dpi-fix.sh test             # bağlantı testi (root gerekmez)
#
# Gereksinimler:
#   - macOS 12+ (Intel & Apple Silicon)
#   - curl, networksetup (macOS dahili)
#   - byedpi / ciadpi (Homebrew veya kaynaktan derleme)

set -euo pipefail

LABEL="com.dpifix.macos"
PLIST="/Library/LaunchDaemons/${LABEL}.plist"
PORT=1080
HOST="127.0.0.1"
STATE_FILE="/var/run/dpifix-prev-proxy.txt"

need_root() { [ "$(id -u)" -eq 0 ] || { echo "Bu işlem yönetici yetkisi gerektirir: sudo $0 $*" >&2; exit 1; }; }

find_ciadpi() {
  for p in /opt/homebrew/bin/ciadpi /usr/local/bin/ciadpi "$(dirname "$0")/bin/ciadpi" $(which ciadpi 2>/dev/null); do
    if [ -x "$p" ]; then echo "$p"; return 0; fi
  done
  return 1
}

active_service() {
  # Aktif ağ arayüzünü (Wi-Fi, Ethernet vb.) belirle
  local dev
  dev=$(route -n get default 2>/dev/null | awk '/interface:/{print $2}')
  [ -n "$dev" ] || dev="en0"
  networksetup -listallhardwareports | awk -v d="$dev" '
    $0 ~ "Hardware Port:" { name=$0; sub(/Hardware Port: /, "", name) }
    $0 ~ "Device: " d { print name }
  ' | head -1
}

cmd_install() {
  need_root
  local bin
  if ! bin=$(find_ciadpi); then
    echo "ciadpi bulunamadı. Homebrew ile kuruluyor..."
    if command -v brew >/dev/null 2>&1; then
      brew install byedpi || true
      bin=$(find_ciadpi || true)
    fi
  fi

  if [ -z "$bin" ]; then
    echo "HATA: ciadpi ikilisi bulunamadı."
    echo "Lütfen 'brew install byedpi' veya https://github.com/hufrea/byedpi adresinden temin edin." >&2
    exit 1
  fi
  echo "Motor: $bin"

  # launchd servisini oluştur
  cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>${LABEL}</string>
    <key>ProgramArguments</key>
    <array>
        <string>${bin}</string>
        <string>--ip</string>
        <string>${HOST}</string>
        <string>--port</string>
        <string>${PORT}</string>
        <string>--split</string>
        <string>2</string>
        <string>--disorder</string>
        <string>3</string>
        <string>--mod-http</string>
        <string>h,d</string>
        <string>--tlsrec</string>
        <string>1+s</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardErrorPath</key>
    <string>/var/log/dpifix.log</string>
</dict>
</plist>
EOF

  launchctl unload "$PLIST" 2>/dev/null || true
  launchctl load -w "$PLIST"
  sleep 1

  # Sistem SOCKS5 proxy ayarını yap
  local srv
  srv=$(active_service)
  if [ -n "$srv" ]; then
    echo "Ağ arayüzü: $srv"
    networksetup -getsocksfirewallproxy "$srv" > "$STATE_FILE" 2>/dev/null || true
    networksetup -setsocksfirewallproxy "$srv" "$HOST" "$PORT"
    networksetup -setsocksfirewallproxystate "$srv" on
    echo "SOCKS5 proxy aktif: $HOST:$PORT ($srv)"
  fi

  echo "Kurulum tamamlandı. Arka plan servisi aktif."
}

cmd_remove() {
  need_root
  launchctl unload "$PLIST" 2>/dev/null || true
  rm -f "$PLIST"

  local srv
  srv=$(active_service)
  if [ -n "$srv" ]; then
    networksetup -setsocksfirewallproxystate "$srv" off 2>/dev/null || true
    echo "Proxy devre dışı bırakıldı: $srv"
  fi
  rm -f "$STATE_FILE"
  echo "macOS servisi ve proxy ayarları kaldırıldı."
}

cmd_test() {
  echo "macOS Bağlantı Testi:"
  for h in www.google.com www.youtube.com discord.com www.instagram.com; do
    code=$(curl -s -o /dev/null -m 8 -w "%{http_code}" --socks5-hostname "$HOST:$PORT" "https://$h" 2>/dev/null || echo "000")
    if [ "$code" = "000" ]; then
      echo "  $h = ENGELLİ"
    else
      echo "  $h = OK ($code)"
    fi
  done
}

case "${1:-}" in
  install) cmd_install ;;
  remove)  cmd_remove ;;
  start)   need_root; launchctl load -w "$PLIST" ;;
  stop)    need_root; launchctl unload "$PLIST" ;;
  status)
    launchctl list | grep "$LABEL" || echo "Servis çalışmıyor."
    ;;
  test)    cmd_test ;;
  *)
    echo "Kullanım: sudo $0 {install|remove|start|stop|status|test}"
    exit 1 ;;
esac
