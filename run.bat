@echo off
setlocal
cd /d "%~dp0"
dotnet run --project native\Ez9Router.Native\Ez9Router.Native.csproj
endlocal