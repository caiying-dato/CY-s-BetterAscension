@echo off
rem 包一层：本机 PowerShell 执行策略可能禁止直接跑 .ps1。
rem 优先用 pwsh（PowerShell 7），没有则回退到系统自带的 powershell.exe。
where pwsh >nul 2>nul
if %ERRORLEVEL%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1" %*
)
exit /b %ERRORLEVEL%
