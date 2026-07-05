#!/usr/bin/env bash
# Builds self-contained single-file dfdb binaries for Windows, Linux, and macOS.
# Output goes to ./dist/<rid>/dfdb[.exe]

set -e

RIDS="${1:-win-x64,linux-x64,osx-x64,osx-arm64,linux-arm64}"
ROOT="$(pwd)"
PROJECT="$ROOT/src/DocumentForge.Cli"
DIST="$ROOT/dist"

IFS=',' read -ra RID_ARRAY <<< "$RIDS"
for rid in "${RID_ARRAY[@]}"; do
    rid="$(echo "$rid" | xargs)"  # trim
    echo ""
    echo "=== Publishing dfdb for $rid ==="
    out="$DIST/$rid"

    dotnet publish "$PROJECT" \
        --configuration Release \
        --runtime "$rid" \
        --self-contained \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        --output "$out"

    binary="dfdb"
    [[ "$rid" == win-* ]] && binary="dfdb.exe"
    if [ -f "$out/$binary" ]; then
        size=$(du -m "$out/$binary" | cut -f1)
        echo "  → $out/$binary (${size} MB)"
    fi
done

echo ""
echo "All done. Drop the binary on any machine and run it:"
echo "  dfdb serve --port 4300 --data-dir ./data"
echo ""
