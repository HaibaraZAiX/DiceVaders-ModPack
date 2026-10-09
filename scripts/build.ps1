param([string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\DiceVaders')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx/interop/ScriptAssembly.dll'))) { throw 'Game interop is missing. Run BepInEx once before building.' }
$modules = @('ModKit', 'ConstellationTool', 'ExtraRewardOption', 'NativeSandbox', 'QoL', 'ResourceButtons', 'ShopInfo')
foreach ($module in $modules) {
    $project = Join-Path $repoRoot ('src/' + $module + '/DiceVaders.' + $module + '.csproj')
    & dotnet build $project -c Release --nologo -v minimal ('-p:GameDir=' + $GameDir)
    if ($LASTEXITCODE -ne 0) { throw ('Build failed: ' + $module) }
}
