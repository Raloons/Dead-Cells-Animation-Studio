@echo off
setlocal
REM Builds a single self-contained DeadCells.AnimationStudio.exe.
REM Output: publish\DeadCells.AnimationStudio.exe
REM Requires the .NET 10 SDK on the build machine only; the result needs no
REM runtime installed and no game files (default outfit assets are embedded).

set "ROOT=%~dp0.."
set "OUT=%ROOT%\publish"

echo Publishing single-file build...
dotnet publish "%ROOT%\src\DcAnim.App\DcAnim.App.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -o "%OUT%"

if errorlevel 1 (
  echo.
  echo Publish FAILED.
  exit /b 1
)

echo.
echo Done: %OUT%\DeadCells.AnimationStudio.exe
