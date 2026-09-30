#!/usr/bin/env bash
# One-time setup on the Linux host that runs the portal. Run as root.
# Creates the service and build users, installs the sandbox helper, and grants the portal the single sudo
# permission it needs. Safe to run again.
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "run as root" >&2; exit 1; }
here="$(cd "$(dirname "$0")" && pwd)"

getent group portal >/dev/null || groupadd --system portal
getent passwd portal >/dev/null || useradd --system --gid portal --home-dir /var/lib/portal --shell /usr/sbin/nologin portal
getent group portal-build >/dev/null || groupadd --system portal-build
getent passwd portal-build >/dev/null || useradd --system --gid portal-build --home-dir /nonexistent --shell /usr/sbin/nologin portal-build

install -d -o portal -g portal -m 0750 /var/lib/portal /var/lib/portal/storage /var/lib/portal/keys /var/lib/portal/deployments
install -d -o portal -g portal -m 0750 /var/lib/portal/deployments/work
install -d -o root -g portal -m 0750 /etc/portal
[ -e /etc/portal/build-sandbox.conf ] || install -o root -g portal -m 0640 "$here/build-sandbox.conf.example" /etc/portal/build-sandbox.conf

install -d -o root -g root -m 0755 /usr/local/lib/portal
install -o root -g root -m 0755 "$here/run-build" /usr/local/lib/portal/run-build

# Validate before installing: a broken sudoers file can lock everyone out of sudo.
tmp="$(mktemp)"
cp "$here/sudoers.portal" "$tmp"
visudo -cf "$tmp"
install -o root -g root -m 0440 "$tmp" /etc/sudoers.d/portal-build
rm -f "$tmp"

echo "Build sandbox installed."
echo "Check it:  sudo -u portal sudo -n /usr/local/lib/portal/run-build --help  (should print 'missing arguments')"
