# Runs the tests and publishes the app and the command line tool into dist\
#
#   .\build.ps1                  framework-dependent (small, needs the .NET 10 Desktop Runtime)
#   .\build.ps1 -SelfContained   bundles the runtime (a lot bigger)
#   .\build.ps1 -SkipTests

param(
    [switch]$SelfContained,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'tests failed, not publishing' }
}

# the version lives in Directory.Build.props
$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version

$folder = if ($SelfContained) { 'freeXplorer-selfcontained' } else { 'freeXplorer' }
$out = Join-Path $PSScriptRoot "dist\$folder"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

foreach ($project in 'src\FreeXplorer.App', 'src\FreeXplorer.Cli') {
    dotnet publish $project --configuration Release --runtime win-x64 `
        --self-contained:$($SelfContained.IsPresent) --output $out `
        -p:DebugType=None -p:DebugSymbols=false -p:SatelliteResourceLanguages=en --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $project" }
}

Copy-Item README.md, LICENSE, THIRD-PARTY.txt $out

$name = if ($SelfContained) { "freeXplorer-$version-win-x64-selfcontained.zip" } else { "freeXplorer-$version-win-x64.zip" }
$zip = Join-Path $PSScriptRoot "dist\$name"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "done: $zip"
