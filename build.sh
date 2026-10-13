#!/usr/bin/env bash
# Runs the tests and publishes the command line tool into dist/ on Linux
#
#   ./build.sh                   framework-dependent (small, needs the .NET 10 runtime)
#   ./build.sh --self-contained  bundles the runtime (a lot bigger)
#   ./build.sh --skip-tests

set -euo pipefail
cd "$(dirname "$0")"

self_contained=false
skip_tests=false
for arg in "$@"; do
    case "$arg" in
        --self-contained) self_contained=true ;;
        --skip-tests) skip_tests=true ;;
        *) echo "unknown option: $arg" >&2; exit 2 ;;
    esac
done

case "$(uname -m)" in
    x86_64) rid=linux-x64 ;;
    aarch64) rid=linux-arm64 ;;
    *) echo "unsupported machine: $(uname -m)" >&2; exit 1 ;;
esac

if ! $skip_tests; then
    dotnet test tests/FreeXplorer.Tests --configuration Release --nologo
fi

# the version lives in Directory.Build.props
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)

folder=freeXplorer-cli
$self_contained && folder=freeXplorer-cli-selfcontained
out="dist/$folder"
rm -rf "$out"

dotnet publish src/FreeXplorer.Cli --configuration Release --runtime "$rid" \
    --self-contained "$self_contained" --output "$out" \
    -p:DebugType=None -p:DebugSymbols=false -p:SatelliteResourceLanguages=en --nologo

cp README.md LICENSE THIRD-PARTY.txt "$out"

name="freeXplorer-cli-$version-$rid"
$self_contained && name="$name-selfcontained"
tar -czf "dist/$name.tar.gz" -C dist "$folder"
echo "done: dist/$name.tar.gz"
