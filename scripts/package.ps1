# Packages one platform of the SQLite add-on into the zip the Larik index expects.
#
#   pwsh ./scripts/package.ps1 -Version 1.0.0 -Rid win-x64
#
# The caller restores first when the Larik.Data package is not on nuget.org yet:
#   dotnet restore -s <local-feed> -s https://api.nuget.org/v3/index.json

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [ValidateSet('win-x64', 'win-arm64')][string] $Rid = 'win-x64',
    [string] $Output = 'out'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Larik.Data.Sqlite.csproj'
$publish = Join-Path $root "out\publish\$Rid"

if (Test-Path $publish) {
    Remove-Item -Recurse -Force $publish
}

dotnet publish $project `
    -c Release `
    -r $Rid `
    --self-contained false `
    -p:Version=$Version `
    -o $publish `
    --no-restore

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for $Rid."
}

# The host owns the contract; shipping another copy would only invite a split identity.
Remove-Item (Join-Path $publish 'Larik.Data.dll') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $publish 'Larik.Data.pdb') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $publish 'Larik.Data.xml') -ErrorAction SilentlyContinue

if (-not (Test-Path (Join-Path $publish 'e_sqlite3.dll'))) {
    throw "The publish output has no e_sqlite3.dll; the native SQLite engine would be missing at runtime."
}

$manifest = Get-Content (Join-Path $root 'addon\larik-addon.json') -Raw | ConvertFrom-Json
$manifest.version = $Version
$manifest.platforms = @($Rid)

[System.IO.File]::WriteAllText(
    (Join-Path $publish 'larik-addon.json'),
    ($manifest | ConvertTo-Json -Depth 5),
    [System.Text.UTF8Encoding]::new($false))

Copy-Item (Join-Path $root 'LICENSE') $publish -Force
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $publish -Force

New-Item -ItemType Directory -Force -Path (Join-Path $root $Output) | Out-Null
$zip = Join-Path $root "$Output\larik.sqlite-$Version-$Rid.zip"
if (Test-Path $zip) {
    Remove-Item $zip -Force
}

Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zip).Length

Write-Host "zip:    $zip"
Write-Host "sha256: $hash"
Write-Host "bytes:  $size"
