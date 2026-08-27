#!/usr/bin/env bash
# Installs the udev rule that lets KrakenEliteScreenManager talk to the Kraken Elite
# (1e71:300c, 1e71:3012) without sudo. Run once: sudo ./scripts/install-udev.sh
set -euo pipefail

RULE_SRC="$(cd "$(dirname "$0")/.." && pwd)/udev/99-kraken-elite-screen-manager.rules"
RULE_DST="/etc/udev/rules.d/99-kraken-elite-screen-manager.rules"

if [[ $EUID -ne 0 ]]; then
  echo "Run as root: sudo $0" >&2
  exit 1
fi

install -m 0644 "$RULE_SRC" "$RULE_DST"
echo "Installed $RULE_DST"

udevadm control --reload-rules
udevadm trigger --subsystem-match=usb --subsystem-match=hidraw
echo "Reloaded udev rules and re-triggered."
echo
echo "If the device was already plugged in, unplug/replug the Kraken USB"
echo "(or reboot) so uaccess applies. Verify with:"
echo "  ls -l /dev/hidraw*    # the kraken node should show '+' (ACL) and be group-accessible"
