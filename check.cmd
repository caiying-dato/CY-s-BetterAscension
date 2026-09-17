@echo off
rem 只跑 IL 校验器：把上限字面量的重写逻辑跑在真实 sts2.dll 的 IL 上，
rem 断言"该改的都改了、没有残留"。改过 Transpiler 后必跑。
cd /d "%~dp0"
dotnet run --project tools\TranspilerCheck\TranspilerCheck.csproj -c Release
exit /b %ERRORLEVEL%
