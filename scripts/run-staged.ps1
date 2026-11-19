param([switch]$NoOpen, [switch]$NoTray)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$staged = Join-Path $root 'staged'
$custom = Join-Path $root 'custom'
$lockPath = Join-Path $root 'data/urd.lock'
$temporaryLauncher = Join-Path $root ('Urd-staged-launch-' + [guid]::NewGuid().ToString('N') + '.exe')
$next = Join-Path $root ('.custom-next-' + [guid]::NewGuid().ToString('N'))
$previous = Join-Path $root ('.custom-previous-' + [guid]::NewGuid().ToString('N'))
$hasPrevious = $false
$result = 1

try {
  if (Test-Path -LiteralPath $lockPath) {
    try {
      $lock = [IO.File]::Open($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
      $lock.Dispose()
    } catch [IO.IOException] {
      throw 'Urd is running and holds data/urd.lock. Stop it before activating a staged build.'
    } catch {
      throw "Cannot verify data/urd.lock; refusing to activate: $($_.Exception.Message)"
    }
  }

  if (-not (Test-Path -LiteralPath (Join-Path $staged 'Start.exe') -PathType Leaf) -or
      -not (Test-Path -LiteralPath (Join-Path $staged 'app/Urd.Server.exe') -PathType Leaf) -or
      -not (Test-Path -LiteralPath (Join-Path $staged 'snapshot') -PathType Container)) {
    throw 'No complete staged build found. Run Build first.'
  }

  # Stage on the root volume so promotion uses same-volume directory moves.
  Copy-Item -LiteralPath $staged -Destination $next -Recurse
  if (-not (Test-Path -LiteralPath (Join-Path $next 'Start.exe') -PathType Leaf) -or
      -not (Test-Path -LiteralPath (Join-Path $next 'app/Urd.Server.exe') -PathType Leaf) -or
      -not (Test-Path -LiteralPath (Join-Path $next 'snapshot') -PathType Container)) {
    throw 'The staged build copy is incomplete; the current custom build was not changed.'
  }

  try {
    if (Test-Path -LiteralPath $custom) {
      Move-Item -LiteralPath $custom -Destination $previous
      $hasPrevious = $true
    }
    Move-Item -LiteralPath $next -Destination $custom
    Copy-Item -LiteralPath (Join-Path $custom 'Start.exe') -Destination $temporaryLauncher
  } catch {
    $activationError = $_
    if ($hasPrevious -and (Test-Path -LiteralPath $previous)) {
      try {
        $failedCustom = Join-Path $root ('.custom-failed-' + [guid]::NewGuid().ToString('N'))
        if (Test-Path -LiteralPath $custom) {
          Move-Item -LiteralPath $custom -Destination $failedCustom
        }
        Move-Item -LiteralPath $previous -Destination $custom
        $hasPrevious = $false
        if (Test-Path -LiteralPath $failedCustom) {
          Remove-Item -LiteralPath $failedCustom -Recurse -Force -ErrorAction SilentlyContinue
        }
      } catch {
        throw "Activation failed and the previous custom build remains at '$previous'. Activation error: $($activationError.Exception.Message) Restore error: $($_.Exception.Message)"
      }
    }
    throw $activationError
  }

  if ($hasPrevious) {
    try {
      Remove-Item -LiteralPath $previous -Recurse -Force
      $hasPrevious = $false
    } catch {
      Write-Warning "The previous custom build remains at '$previous'."
    }
  }

  # Run a root-level copy so the launcher can continue resolving custom/app.
  $arguments = @('--staged')
  if ($NoOpen) { $arguments += '--no-open' }
  if ($NoTray) { $arguments += '--no-tray' }
  & $temporaryLauncher @arguments
  $result = $LASTEXITCODE
} catch {
  Write-Host "Could not run staged build: $($_.Exception.Message)"
  $result = 1
} finally {
  if (Test-Path -LiteralPath $temporaryLauncher) {
    Remove-Item -LiteralPath $temporaryLauncher -Force -ErrorAction SilentlyContinue
  }
  if (Test-Path -LiteralPath $next) {
    Remove-Item -LiteralPath $next -Recurse -Force -ErrorAction SilentlyContinue
  }
}

exit $result
