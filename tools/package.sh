#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")/.."
plugin_name=BTCPayServer.Plugins.WhollyCrypto
plugin_version=0.2.0
upstream=${BTCPAY_SERVER_DIR:-"$PWD/submodules/btcpayserver"}
artifacts=${WHOLLY_BUILD_ARTIFACTS:-"$PWD/.build"}
expected=2d5a0d8077bb33af080e949031da33d84b80638d
[[ $(git -C "$upstream" rev-parse HEAD) == "$expected" ]] || { echo 'Incorrect BTCPay revision.' >&2; exit 1; }
[[ -z $(git -C "$upstream" status --porcelain) ]] || { echo 'BTCPay source must be clean.' >&2; exit 1; }
python3 tools/audit.py
dotnet build "src/$plugin_name/$plugin_name.csproj" -c Release --artifacts-path "$artifacts" -p:BTCPayServerDir="$upstream" -m:2
dotnet build "$upstream/BTCPayServer.PluginPacker/BTCPayServer.PluginPacker.csproj" -c Release --artifacts-path "$artifacts" -m:2
mkdir -p "$artifacts" dist
staging=$(mktemp -d "$artifacts/package.XXXXXX")
trap 'rm -f -- "$staging/$plugin_name.dll" "$staging/$plugin_name.deps.json" "$staging/LICENSE"; rmdir -- "$staging"' EXIT
install -m 0644 "$artifacts/bin/$plugin_name/release/$plugin_name.dll" "$staging/"
install -m 0644 "$artifacts/bin/$plugin_name/release/$plugin_name.deps.json" "$staging/"
install -m 0644 LICENSE "$staging/"
dotnet "$artifacts/bin/BTCPayServer.PluginPacker/release/BTCPayServer.PluginPacker.dll" "$staging" "$plugin_name" "$PWD/dist"
python3 tools/audit.py --package "dist/$plugin_name/$plugin_version/$plugin_name.btcpay" --expected-dll "$staging/$plugin_name.dll"
echo "Preview package and checksums are in dist/$plugin_name/$plugin_version/"
