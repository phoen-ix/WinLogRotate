#!/usr/bin/env sh
# Build all three WinLogRotate installers. Run from this folder.
# Usage:  ./build.sh <version>    e.g.  ./build.sh 0.1.0
# Needs the .NET 10 SDK and makensis. Windows only (Git Bash): the CLI is NativeAOT, which
# cannot cross-compile. The same steps as ci.yml's release job, which is what actually ships.
set -e
[ -n "$1" ] || { echo "Usage: ./build.sh <version>  (e.g. ./build.sh 0.1.0)"; exit 1; }

cli=../src/WinLogRotate.Cli/WinLogRotate.Cli.csproj
gui=../src/WinLogRotate.Gui/WinLogRotate.Gui.csproj

dotnet publish "$cli" -c Release -r win-x64 -p:PublishAot=true -p:TrimmerSingleWarn=false \
  /warnaserror -p:Version="$1" -o ../publish-cli
# Full: self-contained console, needs nothing installed.
dotnet publish "$gui" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version="$1" -o ../publish-gui-full
# Minimal: framework-dependent console, needs the .NET 10 Desktop Runtime.
dotnet publish "$gui" -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:DebugType=none -p:Version="$1" -o ../publish-gui-min

cp ../publish-cli/winlogrotate.exe winlogrotate.exe
cp ../publish-gui-full/winlogrotate-gui.exe winlogrotate-gui.exe
makensis -WX -V2 -DVERSION="$1" winlogrotate.nsi
makensis -WX -V2 -DVERSION="$1" -DFULL_ONLY winlogrotate.nsi
cp ../publish-gui-min/winlogrotate-gui.exe winlogrotate-gui.exe
makensis -WX -V2 -DVERSION="$1" -DMINIMAL_ONLY winlogrotate.nsi
echo "Built WinLogRotate-Setup-$1.exe, WinLogRotate-Setup-$1-full.exe and WinLogRotate-Setup-$1-min.exe"
