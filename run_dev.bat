@echo off
rem Launches the dev loop: bun watchers (TS + Tailwind) and dotnet watch (backend hot-reload).
rem Each process opens in its own console window; close with Ctrl+C or the window X.

setlocal
set ROOT=%~dp0

start "yobalog-frontend" cmd /k "cd /d ""%ROOT%src\YobaLog.Web"" && bun run dev"
start "yobalog-backend"  cmd /k "cd /d ""%ROOT%"" && set RunBunBuild=false&& dotnet watch --project src/YobaLog.Web"

echo.
echo Started two dev processes in separate windows:
echo   yobalog-frontend  — bun watchers (ts + tailwind)
echo   yobalog-backend   — dotnet watch  (hot-reload .cs/.cshtml)
echo.
echo RunBunBuild=false is exported to the backend window so MSBuild skips
echo the bun-build target — the frontend watcher already owns wwwroot/.
echo Ctrl+C in each window to stop, or close the window.
