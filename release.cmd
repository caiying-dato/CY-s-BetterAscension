@echo off
rem 生成可上传 GitHub Releases 的发布包（release\BetterAscension-<版本>.zip）。
rem 会先把当前源码编译部署一遍，再打包 —— 保证发布包与源码一致。
where pwsh >nul 2>nul
if %ERRORLEVEL%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" %*
)
exit /b %ERRORLEVEL%
