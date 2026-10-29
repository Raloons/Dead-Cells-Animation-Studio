@echo off
REM Launcher for Dead Cells Animation Studio (AS-M1).
REM Requires the .NET SDK. Uses your own local Dead Cells installation (auto-detected).
dotnet run --project "%~dp0..\src\DcAnim.App\DcAnim.App.csproj" -c Release -- %*
