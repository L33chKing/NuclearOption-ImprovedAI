# Improved AI — Windows build (mirrors build.sh). Compiles src/*.cs with csc, deploys the single
# canonical dll to BepInEx/plugins/ImprovedAI/ImprovedAI.dll. Fails loudly if the game holds it locked.
$GAME = "C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option"
$MANAGED = "$GAME\NuclearOption_Data\Managed"
$CORE = "$GAME\BepInEx\core"
$CSC = "C:\Program Files\dotnet\sdk\9.0.203\Roslyn\bincore\csc.dll"
$SRC = "$GAME\BepInEx\plugins\ImprovedAI\src"
$refs = @("mscorlib.dll", "netstandard.dll", "System.Runtime.dll", "System.dll", "System.Core.dll",
    "UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.PhysicsModule.dll", "UnityEngine.UI.dll",
    "UnityEngine.IMGUIModule.dll", "Unity.TextMeshPro.dll", "UnityEngine.TextRenderingModule.dll",
    "Mirage.dll", "Newtonsoft.Json.dll", "Assembly-CSharp.dll") | ForEach-Object { "-reference:$MANAGED\$_" }
$refs += "-reference:$CORE\BepInEx.dll"
$refs += "-reference:$CORE\0Harmony.dll"
$files = Get-ChildItem "$SRC\*.cs" | ForEach-Object { $_.FullName }
& dotnet $CSC -nologo -nostdlib -noconfig -optimize+ -target:library -langversion:9.0 `
    -out:"$GAME\.build_improvedai\ImprovedAI.dll" ($refs + $files)
if ($LASTEXITCODE -ne 0) { exit 1 }
try {
    Copy-Item -Force "$GAME\.build_improvedai\ImprovedAI.dll" "$GAME\BepInEx\plugins\ImprovedAI\ImprovedAI.dll" -ErrorAction Stop
    "BUILD+DEPLOY OK"
}
catch { "DEPLOY FAILED (game running?): $($_.Exception.Message)"; exit 1 }
