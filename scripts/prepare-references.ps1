param([string]$Image='jellyfin/jellyfin:12.4')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$refPath=Join-Path $repoRoot '.build/references'
$serverPath=Join-Path $repoRoot '.build/server'
New-Item -ItemType Directory -Path $refPath,$serverPath -Force | Out-Null
$containerId=(& docker create $Image)
if($LASTEXITCODE -ne 0){throw 'Cannot create reference container'}
try {
    & docker cp "${containerId}:/jellyfin/." $serverPath
    if($LASTEXITCODE -ne 0){throw 'Cannot extract reference assemblies'}
    Get-ChildItem -LiteralPath $serverPath -Filter '*.dll' | Copy-Item -Destination $refPath
    if(-not(Test-Path -LiteralPath (Join-Path $refPath 'MediaBrowser.Controller.dll'))){throw 'Missing reference assemblies'}
} finally {
    & docker rm $containerId | Out-Null
}
