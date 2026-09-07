#!/usr/bin/env bash
set -euo pipefail

GAME="c:/Program Files (x86)/Steam/steamapps/common/Nuclear Option"
MANAGED="$GAME/NuclearOption_Data/Managed"
CORE="$GAME/BepInEx/core"
CSC="C:/Program Files/dotnet/sdk/9.0.203/Roslyn/bincore/csc.dll"
SRC="$(cd "$(dirname "$0")" && pwd)"
PLUGIN_DLL="$SRC/../ImprovedAI.dll"          # the ONE dll BepInEx should load
# Compile OUTSIDE BepInEx/plugins so BepInEx never discovers a second copy (dup GUID -> nondeterministic load).
OUT="$GAME/.build_improvedai"
mkdir -p "$OUT"

# Guard: BepInEx scans plugins/ recursively — a stray dll anywhere under the plugin folder gets loaded too.
STRAY="$(find "$SRC/.." -name ImprovedAI.dll ! -path "$PLUGIN_DLL" 2>/dev/null || true)"
if [ -n "$STRAY" ]; then echo "Removing stray dll(s) under the plugin folder:"; echo "$STRAY"; find "$SRC/.." -name ImprovedAI.dll ! -path "$PLUGIN_DLL" -delete; fi
rm -rf "$SRC/bin"                             # old build output location (was under plugins/ -> loaded as a duplicate)

dotnet "$CSC" \
  -nologo -nostdlib -noconfig -optimize+ -target:library -langversion:9.0 \
  -out:"$OUT/ImprovedAI.dll" \
  -reference:"$MANAGED/mscorlib.dll" \
  -reference:"$MANAGED/netstandard.dll" \
  -reference:"$MANAGED/System.Runtime.dll" \
  -reference:"$MANAGED/System.dll" \
  -reference:"$MANAGED/System.Core.dll" \
  -reference:"$MANAGED/UnityEngine.dll" \
  -reference:"$MANAGED/UnityEngine.CoreModule.dll" \
  -reference:"$MANAGED/UnityEngine.PhysicsModule.dll" \
  -reference:"$MANAGED/UnityEngine.UI.dll" \
  -reference:"$MANAGED/UnityEngine.IMGUIModule.dll" \
  -reference:"$MANAGED/Unity.TextMeshPro.dll" \
  -reference:"$MANAGED/UnityEngine.TextRenderingModule.dll" \
  -reference:"$MANAGED/Mirage.dll" \
  -reference:"$MANAGED/Newtonsoft.Json.dll" \
  -reference:"$MANAGED/Assembly-CSharp.dll" \
  -reference:"$CORE/BepInEx.dll" \
  -reference:"$CORE/0Harmony.dll" \
  "$SRC"/*.cs

# Deploy the single canonical dll. Fail loudly if the game holds it locked (avoids a silent stale copy).
if ! cp -f "$OUT/ImprovedAI.dll" "$PLUGIN_DLL"; then
  echo "DEPLOY FAILED — is the game running? Close it and re-run build.sh." >&2
  exit 1
fi
if ! cmp -s "$OUT/ImprovedAI.dll" "$PLUGIN_DLL"; then
  echo "DEPLOY MISMATCH — deployed dll != freshly built dll (game may have it locked)." >&2
  exit 1
fi
echo "BUILD OK + DEPLOYED -> $PLUGIN_DLL"
