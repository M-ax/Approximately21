[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$GameDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Approximately Up'
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'Approximately21\Approximately21.csproj'
$bepInExDirectory = Join-Path $GameDirectory 'BepInEx'
$pluginDirectory = Join-Path $bepInExDirectory 'plugins\'

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Project file was not found: $projectPath"
}

if (-not (Test-Path -LiteralPath $bepInExDirectory -PathType Container)) {
    throw "BepInEx was not found in the game directory: $bepInExDirectory"
}

& dotnet build $projectPath --configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

$outputDirectory = Join-Path $PSScriptRoot "Approximately21\bin\$Configuration\net6.0"
$pluginAssembly = Join-Path $outputDirectory 'Approximately21.dll'

if (-not (Test-Path -LiteralPath $pluginAssembly -PathType Leaf)) {
    throw "Plugin assembly was not produced: $pluginAssembly"
}

New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
Copy-Item -LiteralPath $pluginAssembly -Destination $pluginDirectory -Force

$debugSymbols = [System.IO.Path]::ChangeExtension($pluginAssembly, '.pdb')
if (Test-Path -LiteralPath $debugSymbols -PathType Leaf) {
    Copy-Item -LiteralPath $debugSymbols -Destination $pluginDirectory -Force
}

Write-Host "Installed Approximately21 to $pluginDirectory"
