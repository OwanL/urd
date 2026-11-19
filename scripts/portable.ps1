param(
  [Parameter(Mandatory = $true)]
  [ValidateSet('Build', 'Package', 'Help')]
  [string]$Action
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ($Action -eq 'Help') {
  Write-Host 'Build: capture source and stage a Windows x64 build without changing app or data.'
  Write-Host 'Package: build from a fresh capture and create a ZIP under builds.'
  Write-Host 'Uses pinned build tools, fetching missing tools into the per-user cache.'
  exit 0
}

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
  & $Command @Arguments
  if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

# These are project inputs, not a mirror of the checkout. Apply exclusions at every depth.
function Copy-Inputs([string]$From, [string]$To) {
  $excludedDirectories = @('.git', '.tools', 'node_modules', 'bin', 'obj', 'dist', 'build', 'builds', 'artifacts', 'coverage', 'testresults', 'scratch', 'data', 'logs', 'log', 'cache', '.cache', 'tmp', 'temp', 'backup', 'backups', 'staged', '.vs', '.vscode', '.idea')
  New-Item -ItemType Directory -Path $To -Force | Out-Null
  foreach ($item in Get-ChildItem -LiteralPath $From -Force) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Cannot capture link: $($item.FullName)" }
    if ($item.PSIsContainer) {
      if ($excludedDirectories -contains $item.Name) { continue }
      Copy-Inputs $item.FullName (Join-Path $To $item.Name)
      continue
    }
    $name = $item.Name
    if ($name -match '(?i)^(\.env($|\.)|(secrets?|credentials?|passwords?|tokens?|settings|auth)\.(json|toml|yaml|yml|ini|txt)$)' -or
        $name -match '(?i)(\.db(-journal|-shm|-wal)?|\.sqlite\d*(-journal|-shm|-wal)?|\.log|\.bak|\.backup|\.old|\.tmp|\.zip|\.7z|\.tar|\.gz|\.pfx|\.p12|\.pem|\.key)$') { continue }
    Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $To $name)
  }
}

function Capture([string]$Destination) {
  New-Item -ItemType Directory -Path $Destination | Out-Null
  foreach ($directory in @('source', 'docs', 'scripts')) {
    $path = Join-Path $root $directory
    if (Test-Path -LiteralPath $path -PathType Container) { Copy-Inputs $path (Join-Path $Destination $directory) }
  }
  foreach ($name in @('README.md', 'LICENSE', 'AGENTS.md', '.editorconfig', '.gitattributes', '.gitignore', 'Build.cmd', 'Run Staged Build.cmd', 'Package.cmd')) {
    $path = Join-Path $root $name
    if (Test-Path -LiteralPath $path -PathType Leaf) { Copy-Item -LiteralPath $path -Destination (Join-Path $Destination $name) }
  }
}

function Require-File([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file missing: $Path" }
}

function Build-Capture([string]$Capture, [string]$Work, [string]$Output) {
  Copy-Item -LiteralPath $Capture -Destination $Work -Recurse
  $source = Join-Path $Work 'source'
  $ui = Join-Path $source 'ui'
  . (Join-Path $Work 'scripts/windows-build-tools.ps1')
  Push-Location $source
  try {
    $tools = Get-UrdBuildTools $source
    Push-Location $ui
    try {
      Invoke-Checked $tools.Node @($tools.Npm, 'ci', '--ignore-scripts', '--no-audit', '--no-fund')
      Invoke-Checked $tools.Node @($tools.Npm, 'run', 'build')
    } finally { Pop-Location }
    Require-File (Join-Path $ui 'dist/index.html')
    $server = Join-Path $source 'hosting/server/Urd.Server.csproj'
    $launcher = Join-Path $source 'hosting/launcher/Urd.Launcher.csproj'
    $restore = @('--locked-mode', '--runtime', 'win-x64', '-p:SelfContained=true', '-p:UrdWindowsPackage=true', '--disable-parallel', '-m:1', '-nodeReuse:false')
    Invoke-Checked $tools.Dotnet (@('restore', $server) + $restore)
    Invoke-Checked $tools.Dotnet (@('restore', $launcher, '-p:PublishSingleFile=true') + $restore)
    $app = Join-Path $Output 'app'
    $launchOutput = Join-Path $Work 'launcher-output'
    $publish = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-p:UrdWindowsPackage=true', '--disable-build-servers', '-m:1', '-p:UseSharedCompilation=false')
    Invoke-Checked $tools.Dotnet (@('publish', $server, '-o', $app) + $publish)
    Invoke-Checked $tools.Dotnet (@('publish', $launcher, '-p:PublishSingleFile=true', '-o', $launchOutput) + $publish)
    Require-File (Join-Path $app 'Urd.Server.exe')
    Require-File (Join-Path $app 'Urd.Server.dll')
    Require-File (Join-Path $app 'Urd.Server.runtimeconfig.json')
    Require-File (Join-Path $app 'wwwroot/index.html')
    Require-File (Join-Path $app 'fd/win-x64/fd.exe')
    Require-File (Join-Path $app '.playwright/node/win32_x64/node.exe')
    Require-File (Join-Path $app '.playwright/package/cli.js')
    Require-File (Join-Path $app '.playwright/package/browsers.json')
    Require-File (Join-Path $app 'hostfxr.dll')
    Require-File (Join-Path $launchOutput 'Start.exe')
    Copy-Item -LiteralPath (Join-Path $launchOutput 'Start.exe') -Destination (Join-Path $Output 'Start.exe')
  } finally { Pop-Location }
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('urd-build-' + [guid]::NewGuid().ToString('N'))
try {
  New-Item -ItemType Directory -Path $temp | Out-Null
  $capture = Join-Path $temp 'capture'
  $output = Join-Path $temp 'output'
  Capture $capture
  New-Item -ItemType Directory -Path $output | Out-Null
  Build-Capture $capture (Join-Path $temp 'work') $output
  if ($Action -eq 'Build') {
    Copy-Item -LiteralPath $capture -Destination (Join-Path $output 'snapshot') -Recurse
    # Prepare on the destination volume before replacing the previous staged pair.
    $next = Join-Path $root ('staged-next-' + [guid]::NewGuid().ToString('N'))
    $previous = Join-Path $root ('staged-previous-' + [guid]::NewGuid().ToString('N'))
    $staged = Join-Path $root 'staged'
    try {
      Copy-Item -LiteralPath $output -Destination $next -Recurse
      if (Test-Path -LiteralPath $staged) { Move-Item -LiteralPath $staged -Destination $previous }
      try { Move-Item -LiteralPath $next -Destination $staged } catch {
        if (Test-Path -LiteralPath $previous) { Move-Item -LiteralPath $previous -Destination $staged }
        throw
      }
      if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
    } finally {
      if (Test-Path -LiteralPath $next) { Remove-Item -LiteralPath $next -Recurse -Force }
    }
    Write-Host "Staged build and matching snapshot: $staged"
  } else {
    Copy-Item -LiteralPath (Join-Path $capture 'source') -Destination (Join-Path $output 'source') -Recurse
    foreach ($item in Get-ChildItem -LiteralPath $capture -Force | Where-Object { $_.Name -ne 'source' }) {
      Copy-Item -LiteralPath $item.FullName -Destination $output -Recurse
    }
    $builds = Join-Path $root 'builds'
    New-Item -ItemType Directory -Path $builds -Force | Out-Null
    $zip = Join-Path $builds ('Urd-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.zip')
    # Compress-Archive may skip hidden inputs; preserve the entire staged tree.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($output, $zip)
    Write-Host "Package: $zip"
  }
} catch {
  Write-Error $_
  exit 1
} finally {
  if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
