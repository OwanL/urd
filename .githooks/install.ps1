$ErrorActionPreference = 'Stop'
$architecture = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
if ($env:OS -ne 'Windows_NT' -or $architecture -ne 'AMD64') { throw 'This installer requires Windows x64.' }
$repo = Split-Path -Parent $PSScriptRoot
$root = git -C $repo rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate the repository.' }
if ([IO.Path]::GetFullPath($root) -ne [IO.Path]::GetFullPath($repo)) { throw 'Installer must be in the repository .githooks directory.' }
$hooksPath = git -C $repo config --path --get core.hooksPath
if ($LASTEXITCODE -notin 0, 1) { throw 'Cannot read core.hooksPath.' }
if ($LASTEXITCODE -eq 0) {
    if ([string]::IsNullOrEmpty($hooksPath)) { throw 'Refusing to replace empty core.hooksPath; reconcile hooks manually.' }
    $resolvedHooks = if ([IO.Path]::IsPathRooted($hooksPath)) { $hooksPath } else { Join-Path $repo $hooksPath }
    if ([IO.Path]::GetFullPath($resolvedHooks) -ne (Join-Path $repo '.githooks')) { throw "Refusing to replace existing core.hooksPath '$hooksPath'; reconcile hooks manually." }
}
$gitDir = git -C $repo rev-parse --path-format=absolute --git-common-dir
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate default Git hooks.' }
$defaultHooks = Join-Path $gitDir 'hooks'
if (Test-Path -LiteralPath $defaultHooks) {
    $existing = Get-ChildItem -LiteralPath $defaultHooks -File -Force | Where-Object { $_.Name -notlike '*.sample' }
    if ($existing) { throw 'Refusing to disable non-sample default Git hooks; reconcile hooks manually.' }
}
$temp = Join-Path ([IO.Path]::GetTempPath()) ('urd-gitleaks-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $archive = Join-Path $temp 'gitleaks.zip'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_windows_x64.zip' -OutFile $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($hash -ne 'd29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e') { throw 'Gitleaks archive SHA256 mismatch.' }
    $unpacked = Join-Path $temp 'unpacked'
    Expand-Archive -LiteralPath $archive -DestinationPath $unpacked
    $files = @((Join-Path $unpacked 'gitleaks.exe'), (Join-Path $unpacked 'LICENSE'))
    foreach ($file in $files) {
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Expected archive file missing: $file" }
    }
    $install = Join-Path $repo '.tools/gitleaks'
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    Copy-Item -LiteralPath $files -Destination $install -Force
    git -C $repo config --local core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) { throw 'Cannot set repository-local core.hooksPath.' }
    Write-Host 'Gitleaks 8.30.1 installed; repository-local secret commit gate enabled.'
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}
