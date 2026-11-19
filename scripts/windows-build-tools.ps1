# Resolve the pinned tools without changing the machine installation.
function Get-UrdBuildTools([string]$Source) {
  $ErrorActionPreference = 'Stop'
  $sdk = [string](Get-Content -LiteralPath (Join-Path $Source 'global.json') -Raw | ConvertFrom-Json).sdk.version
  $package = Get-Content -LiteralPath (Join-Path $Source 'ui/package.json') -Raw | ConvertFrom-Json
  $nodeVersion = [string]$package.engines.node
  $npmVersion = [string]$package.engines.npm
  foreach ($version in @($sdk, $nodeVersion, $npmVersion)) {
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Unsupported tool version: $version" }
  }
  if (-not $env:LOCALAPPDATA) { throw 'LOCALAPPDATA is required for the build tools cache.' }
  $cache = Join-Path $env:LOCALAPPDATA 'Urd/tools'

  function Test-UrdVersion([string]$Command, [string[]]$Arguments, [string]$Expected) {
    try {
      $result = & $Command @Arguments 2>$null
      return ($LASTEXITCODE -eq 0 -and ($result | Select-Object -Last 1) -eq $Expected)
    } catch { return $false }
  }

  function Receive-UrdFile([string]$Url, [string]$Path, [string]$Hash) {
    Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash
    if ($actual -ne $Hash) { throw "SHA-512 mismatch for $Url" }
  }

  # Populate a fresh directory, then publish it only after validation succeeds.
  function Install-UrdTool([string]$Destination, [scriptblock]$Install) {
    if (Test-Path -LiteralPath $Destination) { throw "Invalid cached tool: $Destination. Remove it and retry." }
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $stage = Join-Path $cache ('.install-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    try {
      & $Install $stage
      try { [IO.Directory]::Move($stage, $Destination) } catch {
        if (-not (Test-Path -LiteralPath $Destination -PathType Container)) { throw }
      }
    } finally {
      if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    }
  }

  $dotnet = $null
  $existingDotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($existingDotnet) {
    $installed = & $existingDotnet.Source --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and ($installed | Where-Object { $_ -match ('^' + [regex]::Escape($sdk) + ' \[') })) {
      $info = & $existingDotnet.Source --info 2>$null
      if ($LASTEXITCODE -eq 0 -and ($info | Where-Object { $_ -match '^\s*Architecture:\s*x64\s*$' })) { $dotnet = $existingDotnet.Source }
    }
  }
  if (-not $dotnet) {
    $sdkDir = Join-Path $cache "dotnet-$sdk"
    $dotnet = Join-Path $sdkDir 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
      Install-UrdTool $sdkDir {
        param($stage)
        $index = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json'
        $channel = ($sdk -split '\.')[0..1] -join '.'
        $entry = $index.'releases-index' | Where-Object { $_.'channel-version' -eq $channel } | Select-Object -First 1
        if (-not $entry) { throw "No .NET release channel for $sdk." }
        $releases = Invoke-RestMethod $entry.'releases.json'
        $file = $releases.releases | ForEach-Object { $_.sdks } | Where-Object { $_.version -eq $sdk } | ForEach-Object { $_.files } | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
        if (-not $file -or $file.hash -notmatch '^[a-fA-F0-9]{128}$') { throw "No verified Windows x64 .NET SDK $sdk found." }
        $archive = Join-Path $stage 'sdk.zip'
        Receive-UrdFile $file.url $archive $file.hash
        Expand-Archive -LiteralPath $archive -DestinationPath $stage
        Remove-Item -LiteralPath $archive
        if (-not (Test-Path -LiteralPath (Join-Path $stage "sdk/$sdk") -PathType Container)) { throw 'SDK archive has an unexpected layout.' }
      }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $sdkDir "sdk/$sdk") -PathType Container) -or -not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw "Invalid cached SDK: $sdkDir" }
  }

  $node = $null
  $existingNode = Get-Command node.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($existingNode -and (Test-UrdVersion $existingNode.Source @('--version') "v$nodeVersion") -and (Test-UrdVersion $existingNode.Source @('-p', 'process.arch') 'x64')) { $node = $existingNode.Source }
  if (-not $node) {
    $nodeDir = Join-Path $cache "node-$nodeVersion"
    $node = Join-Path $nodeDir 'node.exe'
    if (-not (Test-Path -LiteralPath $node -PathType Leaf)) {
      Install-UrdTool $nodeDir {
        param($stage)
        $base = "https://nodejs.org/dist/v$nodeVersion"
        $name = "node-v$nodeVersion-win-x64.zip"
        $sums = (Invoke-WebRequest -Uri "$base/SHASUMS256.txt" -UseBasicParsing).Content
        $line = ($sums -split "`n" | Where-Object { $_ -match ('^[a-fA-F0-9]{64}\s+\*?' + [regex]::Escape($name) + '\s*$') } | Select-Object -First 1)
        if (-not $line) { throw "No Node checksum for $name." }
        $archive = Join-Path $stage 'node.zip'
        Invoke-WebRequest -Uri "$base/$name" -OutFile $archive -UseBasicParsing
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne ($line -split '\s+')[0]) { throw 'Node SHA-256 mismatch.' }
        $unpacked = Join-Path $stage 'unpacked'
        Expand-Archive -LiteralPath $archive -DestinationPath $unpacked
        $contents = Join-Path $unpacked "node-v$nodeVersion-win-x64"
        if (-not (Test-Path -LiteralPath (Join-Path $contents 'node.exe') -PathType Leaf)) { throw 'Node archive has an unexpected layout.' }
        Get-ChildItem -LiteralPath $contents -Force | Move-Item -Destination $stage
        Remove-Item -LiteralPath $unpacked -Recurse -Force
        Remove-Item -LiteralPath $archive
      }
    }
    if (-not (Test-UrdVersion $node @('--version') "v$nodeVersion")) { throw "Invalid cached Node: $nodeDir" }
  }

  $nodeDir = Split-Path -Parent $node
  $npmCli = Join-Path $nodeDir 'node_modules/npm/bin/npm-cli.js'
  if (-not ((Test-Path -LiteralPath $npmCli -PathType Leaf) -and (Test-UrdVersion $node @($npmCli, '--version') $npmVersion))) {
    $npmDir = Join-Path $cache "npm-$npmVersion"
    $npmCli = Join-Path $npmDir 'bin/npm-cli.js'
    if (-not (Test-Path -LiteralPath $npmCli -PathType Leaf)) {
      Install-UrdTool $npmDir {
        param($stage)
        $metadata = Invoke-RestMethod "https://registry.npmjs.org/npm/$npmVersion"
        $integrity = [string]$metadata.dist.integrity
        if ($integrity -notmatch '^sha512-([A-Za-z0-9+/]+={0,2})$') { throw "Unsupported npm integrity for $npmVersion." }
        $hash = ([BitConverter]::ToString([Convert]::FromBase64String($Matches[1]))).Replace('-', '')
        $archive = Join-Path $stage 'npm.tgz'
        Receive-UrdFile $metadata.dist.tarball $archive $hash
        Push-Location $stage
        try {
          & (Join-Path $env:SystemRoot 'System32/tar.exe') -xzf 'npm.tgz'
          if ($LASTEXITCODE -ne 0) { throw 'Could not extract npm archive.' }
        } finally { Pop-Location }
        $contents = Join-Path $stage 'package'
        if (-not (Test-Path -LiteralPath (Join-Path $contents 'bin/npm.cmd') -PathType Leaf)) { throw 'npm archive has an unexpected layout.' }
        Get-ChildItem -LiteralPath $contents -Force | Move-Item -Destination $stage
        Remove-Item -LiteralPath $contents -Recurse -Force
        Remove-Item -LiteralPath $archive
      }
    }
    if (-not (Test-UrdVersion $node @($npmCli, '--version') $npmVersion)) { throw "Invalid cached npm: $npmDir" }
  }

  $env:DOTNET_ROOT = Split-Path -Parent $dotnet
  $env:PATH = "$nodeDir;$env:DOTNET_ROOT;$env:PATH"
  return [pscustomobject]@{ Dotnet = $dotnet; Node = $node; Npm = $npmCli }
}
