@echo off
rem build.cmd -- 用系统自带的 .NET Framework 4.8 编译器编译 DshPetFloat.exe
rem 不需要 NuGet / MSBuild / Visual Studio，双击或在命令行执行即可。
setlocal
cd /d "%~dp0"

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [FAIL] 找不到 csc.exe，请确认已安装 .NET Framework 4.x
  exit /b 1
)

if not exist "bin" mkdir "bin"
if exist "bin\DshPetFloat.exe" del /f /q "bin\DshPetFloat.exe"

rem csc.rsp 默认已引用 System.dll / System.Core.dll / System.Drawing.dll / System.Windows.Forms.dll
"%CSC%" /nologo /target:winexe /optimize+ /warn:4 /out:bin\DshPetFloat.exe ^
  Json.cs ^
  Util.cs ^
  Native.cs ^
  Model.cs ^
  PetArt.cs ^
  BallWindow.cs ^
  PanelWindow.cs ^
  PetApp.cs ^
  Program.cs

if errorlevel 1 (
  echo.
  echo [FAIL] 编译失败
  exit /b 1
)

echo.
echo [OK] 已生成 bin\DshPetFloat.exe
echo 演示运行: bin\DshPetFloat.exe --demo sample-state.json
endlocal
