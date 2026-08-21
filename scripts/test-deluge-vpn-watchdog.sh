#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
watchdog=$script_dir/deluge-vpn-watchdog.sh
test_root=$(mktemp -d)
case "$test_root" in
    /tmp/*) ;;
    *) printf 'Unexpected temporary path: %s\n' "$test_root" >&2; exit 1 ;;
esac
trap 'rm -rf -- "$test_root"' EXIT

sys_net=$test_root/sys/class/net
state_dir=$test_root/run
calls=$test_root/systemctl.calls
active=$test_root/deluge.active
boot_id_file=$test_root/boot_id
mkdir -p "$sys_net" "$state_dir"
: > "$calls"
printf 'active\n' > "$active"
printf 'boot-a\n' > "$boot_id_file"

fake_systemctl=$test_root/systemctl
cat > "$fake_systemctl" <<'EOF'
#!/bin/sh
set -eu
printf '%s\n' "$*" >> "$WATCHDOG_TEST_CALLS"
case "$1" in
    is-active) [ "$(cat "$WATCHDOG_TEST_ACTIVE")" = active ] ;;
    stop) printf 'inactive\n' > "$WATCHDOG_TEST_ACTIVE" ;;
    restart) printf 'active\n' > "$WATCHDOG_TEST_ACTIVE" ;;
    try-restart) : ;;
    *) exit 2 ;;
esac
EOF
chmod +x "$fake_systemctl"

fake_ip=$test_root/ip
cat > "$fake_ip" <<'EOF'
#!/bin/sh
set -eu
printf '%s\n' "${WATCHDOG_TEST_ROUTES:-}"
EOF
chmod +x "$fake_ip"

fake_logger=$test_root/logger
cat > "$fake_logger" <<'EOF'
#!/bin/sh
exit 0
EOF
chmod +x "$fake_logger"

run_watchdog() {
    WATCHDOG_TEST_CALLS=$calls \
    WATCHDOG_TEST_ACTIVE=$active \
    WATCHDOG_TEST_ROUTES=${routes:-} \
    VPN_INTERFACE=tun0 \
    DELUGE_SERVICE=deluged.service \
    DELUGE_WEB_SERVICE=deluge-web.service \
    STATE_DIR=$state_dir \
    SYS_CLASS_NET_ROOT=$sys_net \
    BOOT_ID_FILE=$boot_id_file \
    SYSTEMCTL_BIN=$fake_systemctl \
    IP_BIN=$fake_ip \
    LOGGER_BIN=$fake_logger \
    "$watchdog"
}

assert_call() {
    grep -Fqx "$1" "$calls" || { printf 'Expected systemctl call: %s\n' "$1" >&2; exit 1; }
}
assert_no_call() {
    if grep -Fqx "$1" "$calls"; then
        printf 'Unexpected systemctl call: %s\n' "$1" >&2
        exit 1
    fi
}

# Missing tunnel is fail-closed.
run_watchdog
assert_call 'stop deluged.service'
[ "$(cat "$active")" = inactive ]

# A device without VPN routes is not ready and must remain fail-closed.
mkdir -p "$sys_net/tun0"
printf '41\n' > "$sys_net/tun0/ifindex"
printf 'active\n' > "$active"
: > "$calls"
routes='default via 192.168.1.1 dev eth0'
run_watchdog
assert_call 'stop deluged.service'

# First healthy observation establishes a known-good bind and records the ifindex.
: > "$calls"
routes='0.0.0.0/1 via 10.0.0.1 dev tun0
128.0.0.0/1 via 10.0.0.1 dev tun0
default via 192.168.1.1 dev eth0'
run_watchdog
assert_call 'restart deluged.service'
assert_call 'try-restart deluge-web.service'
[ "$(cat "$state_dir/deluge-vpn-identity")" = boot-a:41 ]

# Stable interface + active daemon is a no-op.
: > "$calls"
run_watchdog
assert_no_call 'restart deluged.service'
assert_no_call 'try-restart deluge-web.service'

# Recreated interface forces a clean libtorrent rebind.
printf '42\n' > "$sys_net/tun0/ifindex"
: > "$calls"
run_watchdog
assert_call 'restart deluged.service'
[ "$(cat "$state_dir/deluge-vpn-identity")" = boot-a:42 ]

# A reboot can reuse an ifindex, so boot identity is part of the checkpoint and forces one clean bind.
printf 'boot-b\n' > "$boot_id_file"
: > "$calls"
run_watchdog
assert_call 'restart deluged.service'
[ "$(cat "$state_dir/deluge-vpn-identity")" = boot-b:42 ]

# A full default route through the VPN is also accepted, and an inactive daemon self-heals.
printf 'inactive\n' > "$active"
: > "$calls"
routes='default via 10.0.0.1 dev tun0'
run_watchdog
assert_call 'restart deluged.service'
[ "$(cat "$active")" = active ]

printf 'Deluge VPN watchdog tests passed.\n'
