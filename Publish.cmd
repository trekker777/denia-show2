@echo off
REM =============================================================================
REM  Publish.cmd  -  copy this file AND Publish.ps1 into any .NET project folder
REM =============================================================================
REM
REM  HOW TO REUSE THIS IN ANOTHER PROJECT
REM  ------------------------------------
REM  1. Copy Publish.cmd and Publish.ps1 into that project's folder.
REM  2. Edit ONLY the "PROJECT SETTINGS" block below.
REM  3. Run:  Publish.cmd
REM
REM  You do not need to change Publish.ps1. It reads the values from here.
REM
REM  WHY A .CMD FILE?
REM  Windows blocks .ps1 scripts until you change a security setting. A .cmd
REM  file is allowed to run. This launcher starts PowerShell with
REM  -ExecutionPolicy Bypass for this one script only. Your PC policy is
REM  not changed.
REM
REM =============================================================================


REM ---------------------------------------------------------------------------
REM  PROJECT SETTINGS  (edit these when you copy the script to another project)
REM ---------------------------------------------------------------------------

REM Friendly name printed in the console banner. Spaces are fine.
set "APP_NAME=Slide Presenter"

REM The .csproj file in this folder. Use just the file name if it sits next
REM to this .cmd file. A full path also works.
set "PROJECT_FILE=libvlc-media.csproj"

REM Folder that will receive the .exe and .dll files. Created next to this
REM .cmd file. Deleted and recreated on every run so old files do not linger.
set "PUBLISH_DIR=Published"

REM Debug = larger, easier to diagnose.  Release = what you ship.
set "CONFIGURATION=Release"

REM win-x64 = 64-bit Windows. Other common values: win-x86, win-arm64.
REM This also tells NuGet to copy only that OS's native files (video, PDF, ...).
set "RUNTIME=win-x64"

REM false = smaller folder; the PC that runs the app needs the .NET Desktop
REM Runtime installed.  true = the runtime is copied in; the folder is bigger
REM but the app runs on a PC that does not have .NET.
set "SELF_CONTAINED=false"

REM The exe "dotnet publish" will produce. For SDK-style projects this is
REM usually the .csproj name with .exe. Leave this matching PROJECT_FILE.
set "EXE_NAME=libvlc-media.exe"

REM ---------------------------------------------------------------------------
REM  END OF SETTINGS  (you can ignore everything below this line)
REM ---------------------------------------------------------------------------


REM %~dp0 is the folder that contains THIS .cmd file, including a trailing
REM backslash. Using it means the script works even if you run it from a
REM different current directory.
set "SCRIPT_DIR=%~dp0"

REM Build the extra flag only when SELF_CONTAINED is true. PowerShell switch
REM parameters have no value: you either pass -SelfContained or you omit it.
set "SELF_CONTAINED_FLAG="
if /I "%SELF_CONTAINED%"=="true" set "SELF_CONTAINED_FLAG=-SelfContained"

REM %* is every extra argument you typed after Publish.cmd, forwarded through.
REM Example:  Publish.cmd -SelfContained   still works even when the setting
REM above is false, because -SelfContained is appended here.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Publish.ps1" ^
    -AppName "%APP_NAME%" ^
    -ProjectFile "%PROJECT_FILE%" ^
    -PublishDir "%PUBLISH_DIR%" ^
    -Configuration "%CONFIGURATION%" ^
    -Runtime "%RUNTIME%" ^
    -ExeName "%EXE_NAME%" ^
    %SELF_CONTAINED_FLAG% %*

exit /b %ERRORLEVEL%
