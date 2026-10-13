#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo 'Usage: sudo bash install-systemd.sh DEPLOYMENT_DIRECTORY SERVICE_USER BUSINESS_UNIT MANAGEMENT_UNIT' >&2
  exit 2
fi
if [[ $EUID -ne 0 ]]; then echo 'Run this installer as root.' >&2; exit 1; fi
root=$(realpath "$1")
service_user=$2
business=${3%.service}
management=${4%.service}
if [[ ! $service_user =~ ^[a-z_][a-z0-9_-]*[$]?$ ]] || ! id "$service_user" >/dev/null 2>&1; then
  echo 'SERVICE_USER must be an existing dedicated Linux account.' >&2; exit 1
fi
if [[ $service_user == root ]]; then echo 'Use a dedicated non-root service account.' >&2; exit 1; fi
if [[ ! $business =~ ^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,79}$ || ! $management =~ ^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,79}$ || $business == "$management" ]]; then
  echo 'Choose two distinct simple systemd unit names.' >&2; exit 1
fi
if [[ $root == *$'\n'* || $root == *$'\r'* || $root == *'"'* || $root == *'\'* || $root == *'%'* || $root == *'$'* ]]; then
  echo 'Deployment path contains a character unsupported by this installer.' >&2; exit 1
fi
for component in Exam.Server Exam.Management; do
  test -x "$root/$component/$component" || { echo "Missing Linux executable: $component" >&2; exit 1; }
  test -f "$root/$component/appsettings.json" || { echo "Configure $component/appsettings.json first." >&2; exit 1; }
done
for unit in "$business" "$management"; do
  if [[ $(systemctl show "$unit.service" --property=LoadState --value) != not-found ]]; then
    echo "Existing unit was not changed: $unit.service" >&2; exit 1
  fi
done
rules="/etc/polkit-1/rules.d/49-huaizhou-$management.rules"
if [[ -e $rules ]]; then echo "Existing authorization file was not changed: $rules" >&2; exit 1; fi
if [[ ! -d /etc/polkit-1/rules.d ]]; then echo 'Install polkit before running this installer.' >&2; exit 1; fi

write_unit() {
  local unit=$1 component=$2
  cat > "/etc/systemd/system/$unit.service" <<EOF
[Unit]
Description=Huaizhou $component
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$service_user
WorkingDirectory="$root/$component"
ExecStart="$root/$component/$component"
Environment=ASPNETCORE_ENVIRONMENT=Production
Restart=on-failure
RestartSec=5
KillSignal=SIGINT
TimeoutStopSec=90
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
UMask=0077

[Install]
WantedBy=multi-user.target
EOF
}
write_unit "$business" Exam.Server
write_unit "$management" Exam.Management
# Grant only start/stop for this deployment's business unit; no arbitrary commands or other units.
cat > "$rules" <<EOF
polkit.addRule(function(action, subject) {
    if (action.id == "org.freedesktop.systemd1.manage-units" &&
        subject.user == "$service_user" &&
        action.lookup("unit") == "$business.service" &&
        (action.lookup("verb") == "start" || action.lookup("verb") == "stop")) {
        return polkit.Result.YES;
    }
});
EOF
chmod 0644 "/etc/systemd/system/$business.service" "/etc/systemd/system/$management.service" "$rules"
systemctl daemon-reload
systemctl enable "$management.service"
echo "Installed both units without starting them; only $management.service is enabled at boot."
echo "Set Management:BusinessServiceName to $business.service; ensure $service_user can read configuration/certificates and write state/log/resource directories."
