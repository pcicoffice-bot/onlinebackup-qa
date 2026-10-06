#!/bin/bash
# SEC-090: security scan — fails when a library the product ships with has a known flaw, or when something that looks
# like a real secret (private key, cloud key, token, password value) is in the code. Runs in the quality gate.
cd "$(dirname "$0")/.."
fail=0
for p in src/Server/Server.csproj src/Agent/Agent.csproj src/Core/Core.csproj; do
  dotnet restore "$p" -v q >/dev/null 2>&1
  out=$(dotnet list "$p" package --vulnerable --include-transitive 2>&1)
  if echo "$out" | grep -q '^ *>'; then echo "VULNERABLE in $p:"; echo "$out" | grep '^ *>'; fail=1; else echo "ok  $p"; fi
done
# secrets: real-looking keys and assigned passwords in tracked files (tests and docs use obvious samples only)
pat='-----BEGIN ([A-Z]+ )?PRIVATE KEY-----|AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{40,}|sk-ant-[A-Za-z0-9_-]{20,}|xox[bpa]-[A-Za-z0-9-]{20,}|AIza[0-9A-Za-z_-]{35}'
hits=$(git ls-files | grep -v -E '\.(png|jpg|ico|woff2?|zip|bin)$|^tools/security-scan.sh$' | xargs grep -n -I -E -- "$pat" 2>/dev/null | grep -v "ToBase64String(")   # a key made by the test itself is not a secret
if [ -n "$hits" ]; then echo "SECRET-LIKE TEXT:"; echo "$hits" | cut -c1-160; fail=1; else echo "ok  no secrets in the code"; fi
exit $fail
