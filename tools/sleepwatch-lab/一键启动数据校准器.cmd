@echo off
chcp 936 >nul 2>nul
setlocal
cd /d "%~dp0"
set "EXE=SleepWatch.Calibrator.exe"
set "URL=http://127.0.0.1:8765/"
title SleepWatch 数据校准器 1.5.3.0

if not exist "%EXE%" goto no_exe

echo ================================================================
echo   SleepWatch 数据校准器 1.5.3.0
echo ----------------------------------------------------------------
echo   浏览器会自动打开 %URL%
echo   没自动打开的话，手动访问上面的地址就行
echo   关掉这个黑色窗口就是停止服务
echo ================================================================
echo.
echo   正在启动，首次运行要解包约 320 MB 运行时，可能要等十几秒
echo.

"%EXE%"
if errorlevel 1 goto run_failed

echo.
echo   校准器已经退出。
echo.
pause
exit /b 0

:no_exe
echo.
echo   [错误] 当前文件夹里没有 %EXE%
echo   请把本脚本和 %EXE% 放在同一个文件夹里，再双击运行。
echo.
pause
exit /b 1

:run_failed
echo.
echo   [提示] 校准器异常退出，常见原因：
echo     1) 8765 端口被占用，先关掉已经打开的校准器窗口
echo     2) 杀毒软件拦截了 exe 首次解包，加入白名单后重试
echo     3) 系统缺少 VC++ 运行库
echo.
pause
exit /b 1
