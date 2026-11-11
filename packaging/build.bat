@echo off
rem Build all three WinLogRotate installers. Run from this folder.
rem Usage:  build.bat <version>    e.g.  build.bat 0.1.0
rem Needs the .NET 11 SDK and NSIS (makensis on PATH). The same steps as ci.ymls
rem release job, which is what actually ships.
setlocal
if "%~1"=="" (
  echo Usage: build.bat ^<version^>   e.g.  build.bat 0.1.0
  exit /b 1
)
set CLI=..\src\WinLogRotate.Cli\WinLogRotate.Cli.csproj
set GUI=..\src\WinLogRotate.Gui\WinLogRotate.Gui.csproj

dotnet publish %CLI% -c Release -r win-x64 -p:PublishAot=true -p:TrimmerSingleWarn=false /warnaserror -p:Version=%~1 -o ..\publish-cli || exit /b 1
rem Full: self-contained console, needs nothing installed.
dotnet publish %GUI% -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=%~1 -o ..\publish-gui-full || exit /b 1
rem Minimal: framework-dependent console, needs the .NET 11 Desktop Runtime.
dotnet publish %GUI% -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:Version=%~1 -o ..\publish-gui-min || exit /b 1

copy /y "..\publish-cli\winlogrotate.exe" "winlogrotate.exe" >nul || exit /b 1
copy /y "..\publish-gui-full\winlogrotate-gui.exe" "winlogrotate-gui.exe" >nul || exit /b 1
makensis /WX /V2 /DVERSION=%~1 winlogrotate.nsi || exit /b 1
makensis /WX /V2 /DVERSION=%~1 /DFULL_ONLY winlogrotate.nsi || exit /b 1
copy /y "..\publish-gui-min\winlogrotate-gui.exe" "winlogrotate-gui.exe" >nul || exit /b 1
makensis /WX /V2 /DVERSION=%~1 /DMINIMAL_ONLY winlogrotate.nsi || exit /b 1

echo.
echo Built WinLogRotate-Setup-%~1.exe, WinLogRotate-Setup-%~1-full.exe and WinLogRotate-Setup-%~1-min.exe
