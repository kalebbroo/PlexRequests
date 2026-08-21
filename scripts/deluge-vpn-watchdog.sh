#!/bin/sh
set -eu

# Fail-closed rebind guard for a native Deluge daemon pinned to a host VPN interface. When a VPN
# client recreates (rather than merely readdresses) tun0, libtorrent can retain the removed device
# and report every tracker as unreachable until Deluge restarts. This script is intentionally host-
# side: a container must not receive enough host privileges to control systemd.

vpn_interface=${VPN_INTERFACE:-tun0}
deluge_service=${DELUGE_SERVICE:-deluged.service}
web_service=${DELUGE_WEB_SERVICE:-deluge-web.service}
state_dir=${STATE_DIR:-/run/plexrequests}
sys_class_net_root=${SYS_CLASS_NET_ROOT:-/sys/class/net}
systemctl_bin=${SYSTEMCTL_BIN:-systemctl}
ip_bin=${IP_BIN:-ip}
logger_bin=${LOGGER_BIN:-logger}

case "$vpn_interface" in
    ''|*[!A-Za-z0-9_.:-]*)
        printf 'Invalid VPN_INTERFACE: %s\n' "$vpn_interface" >&2
        exit 2
        ;;
esac
case "$deluge_service:$web_service" in
    *[!A-Za-z0-9_.@:-]*)
        printf 'Invalid systemd service name.\n' >&2
        exit 2
        ;;
esac

log_message() {
    if command -v "$logger_bin" >/dev/null 2>&1; then
        "$logger_bin" -t plexrequests-deluge-vpn -- "$1"
    else
        printf '%s\n' "$1" >&2
    fi
}

stop_deluge() {
    reason=$1
    if "$systemctl_bin" is-active --quiet "$deluge_service"; then
        "$systemctl_bin" stop "$deluge_service"
        log_message "Stopped $deluge_service: $reason"
    fi
}

interface_path=$sys_class_net_root/$vpn_interface
if [ ! -r "$interface_path/ifindex" ]; then
    stop_deluge "VPN interface $vpn_interface is absent"
    exit 0
fi

if ! routes=$("$ip_bin" -4 route show 2>/dev/null); then
    stop_deluge "IPv4 VPN routes could not be inspected"
    exit 0
fi

# Accept either a full default route through the VPN or the two /1 routes commonly installed by
# commercial VPN clients. Merely finding tun0 is insufficient: clients create it before routes are
# ready and remove routes before tearing it down.
if ! printf '%s\n' "$routes" | awk -v wanted="$vpn_interface" '
    function uses_interface(    i) {
        for (i = 1; i < NF; i++) if ($i == "dev" && $(i + 1) == wanted) return 1
        return 0
    }
    $1 == "default" && uses_interface() { full = 1 }
    $1 == "0.0.0.0/1" && uses_interface() { lower = 1 }
    $1 == "128.0.0.0/1" && uses_interface() { upper = 1 }
    END { exit !(full || (lower && upper)) }
'; then
    stop_deluge "VPN routes for $vpn_interface are not ready"
    exit 0
fi

if ! current_ifindex=$(tr -d '[:space:]' < "$interface_path/ifindex") \
    || [ -z "$current_ifindex" ]; then
    stop_deluge "VPN interface identity could not be read"
    exit 0
fi

mkdir -p "$state_dir"
state_file=$state_dir/deluge-vpn-ifindex
previous_ifindex=
if [ -r "$state_file" ]; then
    previous_ifindex=$(tr -d '[:space:]' < "$state_file")
fi

if [ "$current_ifindex" = "$previous_ifindex" ] \
    && "$systemctl_bin" is-active --quiet "$deluge_service"; then
    exit 0
fi

reason="VPN interface is ready"
if [ -n "$previous_ifindex" ] && [ "$current_ifindex" != "$previous_ifindex" ]; then
    reason="VPN interface changed from ifindex $previous_ifindex to $current_ifindex"
elif [ -z "$previous_ifindex" ]; then
    reason="VPN interface identity was not previously recorded"
else
    reason="$deluge_service was inactive"
fi

# Do not update the checkpoint unless the daemon actually comes back. A transient systemd failure is
# retried on the next timer tick rather than being mistaken for a successful repair.
"$systemctl_bin" restart "$deluge_service"
"$systemctl_bin" is-active --quiet "$deluge_service"
if [ "$web_service" != "none" ]; then
    "$systemctl_bin" try-restart "$web_service" || true
fi

temporary_state=$state_file.$$
umask 077
printf '%s\n' "$current_ifindex" > "$temporary_state"
mv -f "$temporary_state" "$state_file"
log_message "Rebound $deluge_service to $vpn_interface ($reason)"
