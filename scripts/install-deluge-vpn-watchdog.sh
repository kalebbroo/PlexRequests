#!/bin/sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
    printf 'Run this installer as root (for example: sudo %s).\n' "$0" >&2
    exit 1
fi

vpn_interface=tun0
deluge_service=deluged.service
web_service=deluge-web.service

while [ "$#" -gt 0 ]; do
    case "$1" in
        --interface)
            [ "$#" -ge 2 ] || { printf '%s\n' '--interface requires a value' >&2; exit 2; }
            vpn_interface=$2
            shift 2
            ;;
        --deluge-service)
            [ "$#" -ge 2 ] || { printf '%s\n' '--deluge-service requires a value' >&2; exit 2; }
            deluge_service=$2
            shift 2
            ;;
        --web-service)
            [ "$#" -ge 2 ] || { printf '%s\n' '--web-service requires a value' >&2; exit 2; }
            web_service=$2
            shift 2
            ;;
        *)
            printf 'Unknown option: %s\n' "$1" >&2
            exit 2
            ;;
    esac
done

case "$vpn_interface" in
    ''|*[!A-Za-z0-9_.:-]*) printf 'Invalid interface: %s\n' "$vpn_interface" >&2; exit 2 ;;
esac
case "$deluge_service:$web_service" in
    *[!A-Za-z0-9_.@:-]*) printf 'Invalid systemd service name.\n' >&2; exit 2 ;;
esac

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/.." && pwd)
watchdog_source=$repo_root/scripts/deluge-vpn-watchdog.sh
service_source=$repo_root/docker/systemd/plexrequests-deluge-vpn-watchdog.service
timer_source=$repo_root/docker/systemd/plexrequests-deluge-vpn-watchdog.timer

for required in "$watchdog_source" "$service_source" "$timer_source"; do
    [ -f "$required" ] || { printf 'Required file is missing: %s\n' "$required" >&2; exit 1; }
done

install -D -m 0755 "$watchdog_source" /usr/local/sbin/plexrequests-deluge-vpn-watchdog
install -D -m 0644 "$service_source" /etc/systemd/system/plexrequests-deluge-vpn-watchdog.service
install -D -m 0644 "$timer_source" /etc/systemd/system/plexrequests-deluge-vpn-watchdog.timer

config_file=/etc/default/plexrequests-deluge-vpn-watchdog
config_temporary=$config_file.$$
umask 077
{
    printf 'VPN_INTERFACE=%s\n' "$vpn_interface"
    printf 'DELUGE_SERVICE=%s\n' "$deluge_service"
    printf 'DELUGE_WEB_SERVICE=%s\n' "$web_service"
} > "$config_temporary"
install -m 0644 "$config_temporary" "$config_file"
rm -f "$config_temporary"

systemctl daemon-reload
systemctl enable --now plexrequests-deluge-vpn-watchdog.timer
# Run once immediately instead of waiting for the first timer interval. On initial installation this
# deliberately restarts Deluge after verifying the interface and routes, establishing a known-good bind.
systemctl start plexrequests-deluge-vpn-watchdog.service

printf 'Installed Deluge VPN watchdog for %s.\n' "$vpn_interface"
printf 'Status: systemctl status plexrequests-deluge-vpn-watchdog.timer\n'
printf 'Logs:   journalctl -u plexrequests-deluge-vpn-watchdog.service\n'
