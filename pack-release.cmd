@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
cd /d "%~dp0"

rem ============================================================
rem  一键出全套发版产物（1.5.3.0 起使用）
rem
rem  产物都在 release\ 目录：
rem    ClassIsland.SleepWatch_for_ClassIsland_2.1.0.1.cipx   正式版（net8）
rem    ClassIsland.SleepWatch_for_ClassIsland_2.1.1.1.cipx   2.2 预览版（net10）
rem    SleepWatch.Calibrator.v1.5.3.0.zip                   数据校准器（exe + 一键脚本）
rem    MD5SUMS.txt                                          全部产物的 MD5
rem
rem  依赖：
rem    - .NET 8 SDK 与 .NET 10 SDK 都要装（两套构建目标不同框架）
rem    - Python 装好 torch / ultralytics / onnxruntime / flask / pyinstaller
rem    - 只有 Windows PowerShell 时，PowershellBinaryName 必须传 powershell
rem ============================================================

set "DOTNET=dotnet"
where dotnet >nul 2>nul || (
    echo 找不到 dotnet，请先安装 .NET 8 SDK 与 .NET 10 SDK。
    pause & exit /b 1
)
set "PY=python"

if not exist "release" mkdir "release"

echo [1/5] 构建正式版（net8 / apiVersion 2.1.0.1）
"%DOTNET%" build -c Release -p:CreateCipx=true -p:PowershellBinaryName=powershell -v m --nologo
if errorlevel 1 goto :fail
copy /y "cipx\ClassIsland.SleepWatch.cipx" "release\ClassIsland.SleepWatch_for_ClassIsland_2.1.0.1.cipx" >nul

echo [2/5] 构建预览版（net10 / apiVersion 2.1.1.1）
"%DOTNET%" build -c Release -p:PreviewBuild=true -p:CreateCipx=true -p:PowershellBinaryName=powershell -v m --nologo
if errorlevel 1 goto :fail
"%PY%" pack-preview.py
if errorlevel 1 goto :fail
copy /y "cipx\ClassIsland.SleepWatch_for_ClassIsland_2.1.1.1.cipx" "release\ClassIsland.SleepWatch_for_ClassIsland_2.1.1.1.cipx" >nul

echo [3/5] 打包数据校准器 exe
"%PY%" "tools\sleepwatch-lab\validator\build_exe.py"
if errorlevel 1 goto :fail
copy /y "tools\sleepwatch-lab\validator\dist\SleepWatch.Calibrator.exe" "release\SleepWatch.Calibrator.exe" >nul
copy /y "tools\sleepwatch-lab\一键启动数据校准器.cmd" "release\一键启动数据校准器.cmd" >nul 2>nul
copy /y "tools\sleepwatch-lab\使用说明.txt" "release\使用说明.txt" >nul 2>nul

echo [4/5] 打压缩包
"%PY%" -c "import zipfile,os;z=zipfile.ZipFile('release/SleepWatch.Calibrator.v1.5.3.0.zip','w',zipfile.ZIP_DEFLATED,compresslevel=6);[z.write('release/'+f,f) for f in ('SleepWatch.Calibrator.exe','一键启动数据校准器.cmd','使用说明.txt') if os.path.exists('release/'+f)];z.close()"
"%PY%" -c "import zipfile,glob,os;z=zipfile.ZipFile('release/SleepWatch.TrainingData.v1.5.3.0.zip','w',zipfile.ZIP_DEFLATED,compresslevel=9);[z.write('training/deliverables/'+f,f) for f in ('threshold_balanced.json','threshold_unbalanced.json') if os.path.exists('training/deliverables/'+f)];z.close()"

echo [5/5] 计算校验和
"%PY%" -c "import hashlib,glob,os;out=open('release/MD5SUMS.txt','w',encoding='utf-8');[ (lambda p: out.write(hashlib.md5(open(p,'rb').read()).hexdigest().upper()+'  '+os.path.basename(p)+'\n'))(p) for p in sorted(glob.glob('release/*')) if os.path.isfile(p) and os.path.basename(p)!='MD5SUMS.txt'];out.close()"

echo.
echo 完成，产物在 release\ 目录：
dir /b "release"
echo.
echo 下一步：把 release\ 里的文件上传到对应 tag 的 Release。
pause
exit /b 0

:fail
echo.
echo 构建失败，看上面的报错。
pause
exit /b 1
