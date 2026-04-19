# Launches the dev loop: bun watchers (TS + Tailwind) and dotnet watch (backend hot-reload).
# Two new PowerShell windows open with named titles; close each with Ctrl+C or the window X.
#
# RunBunBuild=false is exported to the backend window so MSBuild skips the bun-build target —
# the frontend watcher already owns wwwroot/ and we don't want the two fighting over output.

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot

$frontCmd = '$Host.UI.RawUI.WindowTitle = ''yobalog-frontend''; Set-Location ''{0}\src\YobaLog.Web''; bun run dev' -f $Root
$backCmd  = '$Host.UI.RawUI.WindowTitle = ''yobalog-backend''; Set-Location ''{0}''; $env:RunBunBuild = ''false''; dotnet watch --project src/YobaLog.Web' -f $Root

Start-Process powershell -ArgumentList '-NoExit', '-Command', $frontCmd
Start-Process powershell -ArgumentList '-NoExit', '-Command', $backCmd

Write-Host ''
Write-Host 'Started two dev processes in separate windows:'
Write-Host '  yobalog-frontend  - bun watchers (ts + tailwind)'
Write-Host '  yobalog-backend   - dotnet watch (hot-reload .cs/.cshtml)'
Write-Host ''
Write-Host 'Ctrl+C in each window to stop, or close the window.'
