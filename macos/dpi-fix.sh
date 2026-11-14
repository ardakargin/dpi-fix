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
STATE_DIR="/Library/Application Support/DPI Fix"
STATE_FILE="$STATE_DIR/previous-proxy"
export LC_ALL=C

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
  dev=$(route -n get default 2>/dev/null | awk '/interface:/{print $2}' || true)
  [ -n "$dev" ] || dev="en0"
  networksetup -listallhardwareports | awk -v d="$dev" '
    $0 ~ "Hardware Port:" { name=$0; sub(/Hardware Port: /, "", name) }
    $0 == "Device: " d && !found { print name; found=1 }
  '
}

save_proxy() {
  local srv="$1" previous
  previous=$(networksetup -getsocksfirewallproxy "$srv")
  if printf '%s\n' "$previous" | grep -q '^Authenticated Proxy Enabled: 1$'; then
    echo "Authenticated SOCKS proxy detected. Existing settings preserved." >&2
    return 1
  fi
  (umask 077; mkdir -p "$STATE_DIR"; printf '%s\n%s\n' "$srv" "$previous" > "$STATE_FILE.tmp")
  mv -f "$STATE_FILE.tmp" "$STATE_FILE"
}

restore_proxy() {
  [ -f "$STATE_FILE" ] || return 0
  local srv server port enabled
  srv=$(head -n 1 "$STATE_FILE")
  server=$(sed -n 's/^Server: //p' "$STATE_FILE")
  port=$(sed -n 's/^Port: //p' "$STATE_FILE")
  enabled=$(sed -n 's/^Enabled: //p' "$STATE_FILE")
  case "$enabled" in
    Yes) networksetup -setsocksfirewallproxy "$srv" "$server" "$port"
         networksetup -setsocksfirewallproxystate "$srv" on ;;
    No)  networksetup -setsocksfirewallproxystate "$srv" off
         if [ -n "$server" ] && [ "${port:-0}" != 0 ]; then
           networksetup -setsocksfirewallproxy "$srv" "$server" "$port"
           networksetup -setsocksfirewallproxystate "$srv" off
         fi ;;
    *)   return 1 ;;
  esac
  rm -f "$STATE_FILE"
}

cmd_install() {
  need_root
  local bin srv
  if ! bin=$(find_ciadpi); then
    bin=""
  fi

  if [ -z "$bin" ]; then
    echo "HATA: ciadpi ikilisi bulunamadı."
    echo "Lütfen 'brew install byedpi' veya https://github.com/hufrea/byedpi adresinden temin edin." >&2
    exit 1
  fi
  echo "Motor: $bin"
  srv=$(active_service)
  if [ -f "$STATE_FILE" ]; then srv=$(head -n 1 "$STATE_FILE"); fi
  if [ -n "$srv" ] && [ ! -f "$STATE_FILE" ]; then save_proxy "$srv"; fi

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
  if [ -n "$srv" ]; then
    echo "Ağ arayüzü: $srv"
    if ! networksetup -setsocksfirewallproxy "$srv" "$HOST" "$PORT" ||
       ! networksetup -setsocksfirewallproxystate "$srv" on; then
      restore_proxy
      launchctl unload "$PLIST"
      return 1
    fi
    echo "SOCKS5 proxy aktif: $HOST:$PORT ($srv)"
  fi

  echo "Kurulum tamamlandı. Arka plan servisi aktif."
}

cmd_remove() {
  need_root
  restore_proxy
  launchctl unload "$PLIST" 2>/dev/null || true
  rm -f "$PLIST"

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
  start)   cmd_install ;;
  stop)    need_root; restore_proxy; launchctl unload "$PLIST" ;;
  restart) need_root; restore_proxy; launchctl unload "$PLIST" 2>/dev/null || true; cmd_install ;;
  status)
    launchctl list | grep "$LABEL" || echo "Servis çalışmıyor."
    ;;
  test)    cmd_test ;;
  *)
    echo "Kullanım: sudo $0 {install|remove|start|stop|status|test}"
    exit 1 ;;
esac
