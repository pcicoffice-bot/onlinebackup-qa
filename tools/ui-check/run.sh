#!/bin/bash
# UI-020: the screen robot. Starts a demo backup server (with a customer, a computer and backups), the licensing centre
# with the partner portal, the customer's screen and the installation wizard (dry run), then walks every screen in
# every language (+ two pseudo languages) on a computer and a phone and checks the layout. Report: $OUT/index.html.
#   OB_RESTIC=/path/restic  [OUT=ui-report] [LANGS="en he ..."] [SHOTS=all (a picture of every screen)] [CHROMIUM=/path/chrome]  tools/ui-check/run.sh
set -e
cd "$(dirname "$0")/../.."
OUT=${OUT:-ui-report}; rm -rf "$OUT"; mkdir -p "$OUT"
W=$(mktemp -d); trap 'kill $(jobs -p) 2>/dev/null; rm -rf "$W"' EXIT
[ -n "$OB_RESTIC" ] || { echo "OB_RESTIC (the restic program) is needed"; exit 2; }
dotnet build src/Server -nologo -v q >/dev/null; dotnet build src/Agent -nologo -v q >/dev/null
SRV="dotnet src/Server/bin/Debug/net8.0/OnlineBackup.Server.dll"; AG="dotnet src/Agent/bin/Debug/net8.0/OnlineBackup.Agent.dll"
P1=18401; P2=18402; P3=18403
wait_for() { for i in $(seq 1 60); do grep -q "$2" "$1" 2>/dev/null && return 0; sleep 1; done; echo "timeout: $1"; cat "$1"; exit 1; }

# the backup server: an administrator, a customer, a computer with two backups (one restorable from the web)
$SRV init --system-home "$W/sys" --admin admin --password Admin-Pass-123 --host localhost --user-home "$W/users|UNLIMITED|100" >/dev/null
# BRAND-010: a partner's brand (name, slogan, logo, colours), so every screen is checked with branding
python3 - "$W/sys/conf/system.xml" <<'PY'
import sys, zlib, struct, base64, xml.etree.ElementTree as ET
def png(w, h):   # a small logo: a rounded indigo-to-violet tile
    rows = b''
    for y in range(h):
        rows += b'\0' + b''.join(bytes((79 + x * 60 // w, 70 - y * 20 // h, 229, 255 if min(x, y, w - 1 - x, h - 1 - y) > 2 else 0)) for x in range(w))
    c = lambda t, d: struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    return b'\x89PNG\r\n\x1a\n' + c(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)) + c(b'IDAT', zlib.compress(rows)) + c(b'IEND', b'')
t = ET.parse(sys.argv[1]); b = t.getroot().find('BRANDING')
if b is None: b = ET.SubElement(t.getroot(), 'BRANDING')
for k, v in {'PRODUCT': 'ITCare Cloud Backup', 'SLOGAN': 'Your data, always safe', 'COMPANY': 'ITCare IT', 'PHONE': '03-5550000', 'EMAIL': 'support@example.invalid',
             'COLOR': '#4F46E5', 'ACCENT': '#F97316', 'LOGO': 'data:image/png;base64,' + base64.b64encode(png(64, 64)).decode()}.items(): b.set(k, v)
# TECH-010: two-step is mandatory for administrators — the robot's administrator has it set up (a throw-away test secret)
t.getroot().find('ADMIN').set('TOTP_SECRET', 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP')
t.write(sys.argv[1], encoding='utf-8', xml_declaration=True)
PY
# a licence with every module (a throw-away key of this run), so the screens show every kind of set (SQL Server too)
$SRV license-keygen --out "$W/rkey" > "$W/rkey.txt"; export OB_LICENSE_PUBKEY=$(tail -1 "$W/rkey.txt")
LIC=$($SRV license-issue --key "$W/rkey" --server-id "$($SRV server-id --system-home "$W/sys")" --company "Robot IT" --users 100 --storage-gb 1000 --days 30)
python3 - "$W/sys/conf/system.xml" "$LIC" <<'PY2'
import sys, xml.etree.ElementTree as ET
t = ET.parse(sys.argv[1]); l = t.getroot().find('LICENSE')
if l is None: l = ET.SubElement(t.getroot(), 'LICENSE')
l.set('KEY', sys.argv[2]); t.write(sys.argv[1], encoding='utf-8', xml_declaration=True)
PY2
$SRV adduser --system-home "$W/sys" --login dana-office --password Customer-Pass-1 --quota-gb 1 --email it@example.invalid >/dev/null
$SRV run --system-home "$W/sys" --prefix "http://localhost:$P1/" > "$W/srv.log" 2>&1 & wait_for "$W/srv.log" listening
mkdir -p "$W/data/Dana" "$W/data/Shared"; echo n > "$W/data/Dana/Budget 2026.xlsx"; echo w > "$W/data/Dana/letter.docx"; echo x > "$W/data/Shared/plan.xlsx"
$AG register --home "$W/ag" --server "http://localhost:$P1/" --login dana-office --password Customer-Pass-1 --computer OFFICE-PC >/dev/null
S1=$($AG addset --home "$W/ag" --password Customer-Pass-1 --name "Office files" --engine RESTIC --source "$W/data" --keytype PASSWORD | tail -1)
S2=$($AG addset --home "$W/ag" --password Customer-Pass-1 --name "Accounting server" --source "$W/data/Shared" --keytype PASSWORD | tail -1)
$AG backup --home "$W/ag" --set "$S1" >/dev/null; $AG backup --home "$W/ag" --set "$S2" >/dev/null
# an SQL Server set (its databases), so the screens and the demo show how one is built
$AG addset --home "$W/ag" --password Customer-Pass-1 --name "SQL Server — ERP" --type MSSQL --db-user sa --source "Microsoft SQL Server\\SQLEXPRESS\\ERP" --source "Microsoft SQL Server\\SQLEXPRESS\\Payroll" --hour 1 --keytype PASSWORD >/dev/null
mkdir -p "$W/data/Shared/Temp" "$W/data/Projects/2024" "$W/data/Projects/2025"; $AG folders --home "$W/ag" >/dev/null
# the customer's screen
$AG ui --home "$W/ag" --port $P3 --no-browser --minutes 120 > "$W/ui.log" 2>&1 & wait_for "$W/ui.log" http
# the licensing centre with the partner portal (a throw-away key; a fake package is enough for the screens)
mkdir -p "$W/pkg/server/client"; echo x > "$W/pkg/Setup.cmd"; echo x > "$W/pkg/server/OnlineBackup.Server.exe"; echo x > "$W/pkg/server/client/OnlineBackup.Agent.exe"
$SRV license-keygen --out "$W/key" >/dev/null
$SRV license-center-portal --data "$W/center" --package "$W/pkg" --center-url "http://localhost:$P2" --owner-password Owner-Pass-2026 >/dev/null
$SRV license-center --key "$W/key" --data "$W/center" --prefix "http://localhost:$P2/" > "$W/lc.log" 2>&1 & wait_for "$W/lc.log" listening
# the installation wizard, without touching this machine (dry run)
OB_SETUP_ROOT="$W/setuproot" $SRV setup > "$W/setup.log" 2>&1 & wait_for "$W/setup.log" "http://"
# the client software's installation wizard (SETUP-C10), on a package for this server — the robot does not install
mkdir -p "$W/cpkg"
printf '<CONNECTION SERVER="http://localhost:%s" FOLDER="RobotBackup" />' "$P1" > "$W/cpkg/connection.xml"
printf '<BRANDING PRODUCT="Robot Backup" COMPANY="Robot IT" COLOR="#4F46E5" ACCENT="#F97316" LANGUAGE="en" />' > "$W/cpkg/branding.xml"
$AG setup-ui --package "$W/cpkg" --install-dir "$W/cinst" --data-dir "$W/cdata" > "$W/csetup.log" 2>&1 & wait_for "$W/csetup.log" "http://"

export CSETUP_URL=$(grep -o 'http://[^ ]*' "$W/csetup.log" | head -1)
export ADMIN_TOTP=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP ADMIN_URL="http://localhost:$P1" CLIENT_URL=$(grep -o 'http://[^ ]*' "$W/ui.log" | head -1) PORTAL_URL="http://localhost:$P2" SETUP_URL=$(grep -o 'http://[^ ]*' "$W/setup.log" | head -1) OUT
export NODE_PATH=${NODE_PATH:-$(npm root -g)}
node tools/ui-check/${ROBOT:-ui-check}.js
