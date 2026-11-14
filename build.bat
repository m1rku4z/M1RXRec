@echo off
setlocal
cd /d "%~dp0"
title M1RXRec Build
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
echo ==========================================
echo   M1RXRec - build and install
echo ==========================================
echo Folder: %cd%
echo.
if not exist M1RXRec.csproj (
  echo ERROR: M1RXRec.csproj not found.
  echo You must Extract All from the zip first, then run build.bat inside the extracted folder.
  goto fail
)

set "DN=dotnet"
dotnet --list-sdks 2>nul | findstr /b "8." >nul
if errorlevel 1 (
  echo [1/4] .NET 8 SDK not found. Downloading it into this folder, about 1-3 minutes, please wait...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1; & ./dotnet-install.ps1 -Channel 8.0 -InstallDir '%~dp0.dotnet'"
  set "DN=%~dp0.dotnet\dotnet.exe"
) else (
  echo [1/4] .NET 8 SDK found.
)
"%DN%" --version >nul 2>nul
if errorlevel 1 (
  echo ERROR: could not install .NET. Check your internet and try again.
  goto fail
)

echo [2/4] Building the program, first time takes 2-5 minutes, please wait...
"%DN%" publish -c Release -o out
if errorlevel 1 goto fail

if not exist out\ffmpeg.exe (
  echo [3/4] Downloading ffmpeg, about 1-2 minutes...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Invoke-WebRequest 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip' -OutFile ff.zip; Expand-Archive ff.zip -DestinationPath ffx -Force; Copy-Item (Get-ChildItem ffx -Recurse -Filter ffmpeg.exe | Select-Object -First 1).FullName out\ffmpeg.exe; Remove-Item ff.zip,ffx -Recurse -Force"
) else (
  echo [3/4] ffmpeg already there.
)
if not exist out\ffmpeg.exe (
  echo ERROR: ffmpeg download failed.
  goto fail
)

echo [4/4] Creating Desktop shortcut...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\M1RXRec.lnk'); $s.TargetPath='%~dp0out\M1RXRec.exe'; $s.WorkingDirectory='%~dp0out'; $s.Save()"
echo.
echo ===== DONE! M1RXRec icon is on your Desktop =====
start "" "%~dp0out\M1RXRec.exe"
pause
exit /b 0

:fail
echo.
echo ===== FAILED. Take a screenshot of this window and send it to Claude =====
pause
exit /b 1
