#!/usr/bin/env bash
# DPI Fix for Linux — zapret (nfqws) tabanlı yardımcı
#
# DENEYSEL. Windows sürümündeki arayüz .NET/WinForms olduğu için Linux'ta çalışmaz;
# bu betik yalnızca servis yaşam döngüsünü yönetir. Paket parçalama işini zapret yapar.
#
# Kullanım:
#   sudo ./dpi-fix.sh install     # servisi kur ve başlat
#   sudo ./dpi-fix.sh remove      # servisi kaldır
#   sudo ./dpi-fix.sh start|stop|restart|status
#   ./dpi-fix.sh test             # bağlantı testi (root gerekmez)
#   sudo ./dpi-fix.sh params "..." # nfqws argümanlarını değiştir
#
# Önce zapret kurun: https://github.com/bol-van/zapret (veya dağıtımınızın paketi)
# Doğru nfqws argümanlarını bulmak için zapret içindeki blockcheck.sh betiğini çalıştırın.

set -euo pipefail

SVC=dpi-fix
UNIT=/etc/systemd/system/${SVC}.service
CONF_DIR=/etc/dpi-fix
ARGS_FILE=${CONF_DIR}/nfqws.args
DEFAULT_ARGS="--dpi-desync=split2 --dpi-desync-split-pos=2 --dpi-desync-fooling=md5sig"
ENV_FILE=${CONF_DIR}/nfqws.env

need_root() { [ "$(id -u)" -eq 0 ] || { echo "Bu komut root gerektirir (sudo)." >&2; exit 1; }; }

find_nfqws() {
  for p in /opt/zapret/nfqws /opt/zapret/bin/nfqws /usr/sbin/nfqws /usr/bin/nfqws; do
    [ -x "$p" ] && { echo "$p"; return 0; }
  done
  command -v nfqws 2>/dev/null && return 0
  return 1
}

write_environment() {
  local args="$1"
  [[ "$args" != *$'\n'* && "$args" != *$'\r'* ]] || return 1
  args=${args//\\/\\\\}
  args=${args//\"/\\\"}
  printf 'NFQWS_ARGS="%s"\n' "$args" > "$ENV_FILE.tmp"
  mv -f "$ENV_FILE.tmp" "$ENV_FILE"
}

cmd_install() {
  need_root
  local nfqws
  if ! nfqws=$(find_nfqws); then
    echo "HATA: nfqws bulunamadı. Önce zapret kurun: https://github.com/bol-van/zapret" >&2
    exit 1
  fi
  echo "Motor: $nfqws"

  mkdir -p "$CONF_DIR"
  [ -f "$ARGS_FILE" ] || { echo "$DEFAULT_ARGS" > "$ARGS_FILE"; echo "Varsayılan argümanlar yazıldı: $ARGS_FILE"; }
  write_environment "$(cat "$ARGS_FILE")"

  cat > "$UNIT" <<EOF
[Unit]
Description=DPI Fix (zapret nfqws)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
EnvironmentFile=${ENV_FILE}
ExecStart="${nfqws}" \$NFQWS_ARGS
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

  systemctl daemon-reload
  systemctl enable --now "$SVC"
  sleep 1
  systemctl --no-pager --lines=5 status "$SVC" || true
  echo
  echo "Kuruldu. Argüman değiştirmek için: sudo $0 params \"--dpi-desync=...\""
}

cmd_remove() {
  need_root
  systemctl disable --now "$SVC" 2>/dev/null || true
  rm -f "$UNIT"
  systemctl daemon-reload
  echo "Servis kaldırıldı. (Ayarlar duruyor: $CONF_DIR — silmek için: sudo rm -rf $CONF_DIR)"
}

cmd_params() {
  need_root
  [ $# -ge 1 ] || { echo "Kullanım: sudo $0 params \"--dpi-desync=...\"" >&2; exit 1; }
  mkdir -p "$CONF_DIR"
  write_environment "$*"
  printf '%s\n' "$*" > "$ARGS_FILE"
  if [ -f "$UNIT" ]; then systemctl restart "$SVC"; fi
  echo "Argümanlar güncellendi: $*"
}

cmd_test() {
  echo "Bağlantı testi:"
  for h in www.google.com www.youtube.com discord.com; do
    if code=$(curl -s -o /dev/null -m 8 -w "%{http_code}" "https://$h"); then
      echo "  $h = OK ($code)"
    else
      echo "  $h = erişilemedi"
    fi
  done
  command -v systemctl >/dev/null && systemctl is-active --quiet "$SVC" \
    && echo "  servis = çalışıyor" || echo "  servis = durmuş/kurulu değil"
}

case "${1:-}" in
  install) cmd_install ;;
  remove)  cmd_remove ;;
  start|stop|restart|status) need_root; systemctl "$1" "$SVC" ;;
  params)  shift; cmd_params "$@" ;;
  test)    cmd_test ;;
  *)
    echo "Kullanım: sudo $0 {install|remove|start|stop|restart|status|params \"...\"|test}"
    exit 1 ;;
esac
