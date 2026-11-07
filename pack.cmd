@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

rem ============================================================
rem  打包 ClassIsland 插件为 .cipx
rem  -p:PowershellBinaryName=powershell 是必需的：
rem    SDK 的 targets 里把生成校验和的命令写死成 pwsh（PowerShell Core），
rem    而这台机器只有系统自带 Windows PowerShell，不加这个参数打包会报 MSB3073。
rem ============================================================

set DOTNET=dotnet
if exist "F:\dotnet\dotnet.exe" set DOTNET=F:\dotnet\dotnet.exe

echo 正在编译并打包...
"%DOTNET%" build -c Release -p:CreateCipx=true -p:PowershellBinaryName=powershell -v m --nologo

if errorlevel 1 (
    echo.
    echo 打包失败，看上面的报错。
    pause
    exit /b 1
)

echo.
echo 完成。插件包在 cipx\ClassIsland.SleepWatch.cipx
echo 上传到 Release 时改名为 ClassIsland.SleepWatch_for_ClassIsland_^(宿主版本，如 2.1.0.1^).cipx
echo 包名标的是适配的 ClassIsland 版本，不是插件版本；插件版本看 Release 的 tag
echo 校验和在同目录 checksums.md
pause
