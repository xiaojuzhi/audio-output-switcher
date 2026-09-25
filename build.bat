@echo off
setlocal
cd /d "%~dp0"

rem 使用 Windows 自带的 .NET Framework 编译器(Win10/11 免安装)
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found. Please make sure .NET Framework 4.x is installed.
  pause
  exit /b 1
)

if not exist "dist" mkdir "dist"

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /win32icon:"assets\icon.ico" /out:"dist\音频切换器.exe" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  "src\AudioOutputSwitcher.cs"

if errorlevel 1 (
  echo.
  echo [ERROR] Build failed.
  pause
  exit /b 1
)

echo.
echo [OK] Build finished: dist\音频切换器.exe
pause
