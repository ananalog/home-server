#!/usr/bin/env bash
# Builds a release: Mini App + self-contained server + native homectl + simulator → dist/home-<version>-linux-x64.tar.gz
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION=${VERSION:-$(git describe --tags --always --dirty 2>/dev/null | sed 's/^v//' || echo 0.0.0-dev)}
RID=${RID:-linux-x64}
OUT=dist/home-$VERSION-$RID
rm -rf "$OUT" && mkdir -p "$OUT"

echo "== Mini App"
(cd web/miniapp && npm ci --no-audit --no-fund && npm run build)

echo "== server ($RID, self-contained single file)"
dotnet publish src/Home.Server -c Release -r "$RID" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:Version="${VERSION%%-*}" -p:InformationalVersion="$VERSION" -o "$OUT"

echo "== homectl (Native AOT)"
dotnet publish src/Home.Cli -c Release -r "$RID" -p:InformationalVersion="$VERSION" -o "$OUT/cli-tmp"
mv "$OUT/cli-tmp/homectl" "$OUT/homectl" && rm -rf "$OUT/cli-tmp"

echo "== simulator"
dotnet publish src/Home.Simulator -c Release -r "$RID" --self-contained -p:PublishSingleFile=true -o "$OUT/sim-tmp"
mv "$OUT/sim-tmp/home-sim" "$OUT/home-sim" && rm -rf "$OUT/sim-tmp"

rm -f "$OUT"/*.pdb "$OUT"/*.dbg
echo "$VERSION" > "$OUT/VERSION"
tar -C dist -czf "dist/home-$VERSION-$RID.tar.gz" "home-$VERSION-$RID"
(cd dist && sha256sum "home-$VERSION-$RID.tar.gz" > "home-$VERSION-$RID.tar.gz.sha256")
echo "dist/home-$VERSION-$RID.tar.gz"
