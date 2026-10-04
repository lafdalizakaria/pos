#!/usr/bin/env bash
# Builds the register package (Windows x64, self-contained .NET + vision service sources + install scripts).
#   deploy/register/build-package.sh [version] [output-dir]
# Output: newrest-pos-register-<version>.zip + .sha256 (distribute with Intune / SCCM / GPO, or copy by hand).
set -euo pipefail
version="${1:-1.0.0}"
out="${2:-artifacts}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

dotnet publish "$root/src/Newrest.Pos.Client" -c Release -r win-x64 --self-contained \
  -p:Version="$version" -p:PublishReadyToRun=false -o "$stage/caisse" --nologo -v q
rm -f "$stage/caisse/appsettings.local.json"

mkdir -p "$stage/vision"
(cd "$root/vision" && tar --exclude=.venv --exclude=data --exclude=tests --exclude=__pycache__ --exclude=.pytest_cache \
  --exclude=.ruff_cache --exclude=runs -cf - .) | (cd "$stage/vision" && tar -xf -)
cp "$root/deploy/vision/install-vision-service.ps1" "$root/deploy/register/install-register.ps1" "$stage/"
cp "$root/deploy/register/README.md" "$stage/LISEZMOI.md"
echo "$version" > "$stage/VERSION"

mkdir -p "$out"
zip_path="$(cd "$out" && pwd)/newrest-pos-register-$version.zip"
(cd "$stage" && rm -f "$zip_path" && zip -qr "$zip_path" .)
(cd "$out" && sha256sum "$(basename "$zip_path")" > "$(basename "$zip_path").sha256")
echo "$zip_path"
