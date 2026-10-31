<#
.SYNOPSIS
    Compiles a .NET project and copies the runnable files into a publish folder.

.DESCRIPTION
    This is the engine. The project-specific names (app title, .csproj, exe,
    output folder, and so on) normally come from Publish.cmd sitting next to
    this file. Edit the settings block at the top of Publish.cmd when you
    copy these two files into another project.

    "dotnet build" only compiles. "dotnet publish" compiles AND gathers every
    file the app needs to run (the .exe, .dll files, native libraries, JSON
    config, and so on) into one output folder.

.PARAMETER AppName
    Friendly name printed in the banner. Example: "Slide Presenter"

.PARAMETER ProjectFile
    The .csproj file name, or a full path to it.

.PARAMETER PublishDir
    Folder that will hold the published exe and dlls. A relative path is
    created next to this script.

.PARAMETER Configuration
    Debug or Release.

.PARAMETER Runtime
    .NET Runtime Identifier, such as win-x64.

.PARAMETER ExeName
    Expected exe file name after publish, such as MyApp.exe.
    If you leave this empty, it is derived from the .csproj file name.

.PARAMETER SelfContained
    A "switch" parameter. You either pass it or you do not; it has no value.
    If you include -SelfContained, the .NET runtime is copied into the
    publish folder. The folder gets much larger, but the target PC does not
    need .NET installed.

.EXAMPLE
    .\Publish.cmd

    Uses the settings at the top of Publish.cmd.

.EXAMPLE
    .\Publish.cmd -SelfContained

    Same settings, but include the .NET runtime.

.NOTES
    Prefer Publish.cmd over running this .ps1 directly. Windows often blocks
    .ps1 files until you change execution policy. Publish.cmd bypasses that
    for this one script without changing your PC settings.

    If you do want .ps1 files to run directly:

        Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
#>

# ---------------------------------------------------------------------------
# param() MUST be the first executable statement in a script (only a comment
# or the <# help block #> may appear above it). It is how a script accepts
# arguments, the same way a C# method accepts parameters.
#
# [CmdletBinding()] turns this script into an "advanced function". That gives
# you extra common parameters such as -Verbose and -WhatIf for free.
#
# Defaults below match THIS project so you can still run Publish.ps1 by
# itself after changing execution policy. When you copy the scripts to
# another project, change the block at the top of Publish.cmd instead.
# ---------------------------------------------------------------------------
[CmdletBinding()]
param(
    [string]$AppName = 'Slide Presenter',

    [string]$ProjectFile = 'libvlc-media.csproj',

    [string]$PublishDir = 'Published',

    # ValidateSet restricts the value. If someone types -Configuration Foo,
    # PowerShell stops with a clear error instead of passing junk to dotnet.
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    [string]$ExeName = 'libvlc-media.exe',

    # [switch] is a boolean flag. $SelfContained is $true when the caller
    # wrote -SelfContained, otherwise $false.
    [switch]$SelfContained
)

# ---------------------------------------------------------------------------
# These two lines make scripts safer.
#
# Set-StrictMode: treat uninitialized variables as errors, similar to C#
# nullable / definite-assignment checks. "Latest" means use the newest
# rules this version of PowerShell knows about.
#
# $ErrorActionPreference = 'Stop': most cmdlets throw a terminating error
# instead of printing a warning and continuing. Without this, a failed
# Remove-Item could be missed and you would publish into a half-old folder.
# Native programs such as "dotnet" still need a $LASTEXITCODE check; they
# do not honor this preference.
# ---------------------------------------------------------------------------
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# $PSScriptRoot is an automatic variable. PowerShell fills it in with the
# folder that contains THIS .ps1 file. That matters because the person
# running the script might have a different "current directory".
#
# Join-Path glues folder pieces together with the correct slash for the OS.
# Prefer Join-Path over "$folder\$file" string concatenation.
#
# [IO.Path]::IsPathRooted is true for "C:\foo\bar.csproj" and false for
# "libvlc-media.csproj". Relative names are resolved next to this script.
# ---------------------------------------------------------------------------
$ProjectRoot = $PSScriptRoot

if (-not [System.IO.Path]::IsPathRooted($ProjectFile)) {
    $ProjectFile = Join-Path -Path $ProjectRoot -ChildPath $ProjectFile
}

if (-not [System.IO.Path]::IsPathRooted($PublishDir)) {
    $PublishDir = Join-Path -Path $ProjectRoot -ChildPath $PublishDir
}

# If ExeName was left blank, use the .csproj file name with .exe
# Split-Path -Leaf is the last piece of a path (the file name).
# [IO.Path]::GetFileNameWithoutExtension strips ".csproj".
if ([string]::IsNullOrWhiteSpace($ExeName)) {
    $projectLeaf = Split-Path -Path $ProjectFile -Leaf
    $ExeName = [System.IO.Path]::GetFileNameWithoutExtension($projectLeaf) + '.exe'
}

Write-Host ''
Write-Host "$AppName  -  publish script" -ForegroundColor Cyan
Write-Host '----------------------------------'
Write-Host "Project : $ProjectFile"
Write-Host "Output  : $PublishDir"
Write-Host "Config  : $Configuration"
Write-Host "Runtime : $Runtime"
Write-Host "Exe     : $ExeName"
Write-Host "Mode    : $(if ($SelfContained) { 'self-contained (runtime included)' } else { 'framework-dependent (needs the .NET Desktop Runtime)' })"
Write-Host ''

# ---------------------------------------------------------------------------
# Test-Path returns $true if the file or folder exists. -PathType Leaf means
# "this must be a file, not a folder". We fail early with a readable message
# instead of letting "dotnet publish" print a cryptic MSBuild error.
# ---------------------------------------------------------------------------
if (-not (Test-Path -Path $ProjectFile -PathType Leaf)) {
    throw "Cannot find the project file. Expected it at: $ProjectFile"
}

# ---------------------------------------------------------------------------
# Get-Command looks up an executable the same way the shell does (PATH).
# -ErrorAction SilentlyContinue is local to this one call and overrides the
# script-level Stop preference, because "not found" is a situation we want
# to handle ourselves.
#
# The result is stored in $dotnet. If it is $null, dotnet is not installed
# (or not on PATH). .Source is the full path to dotnet.exe.
# ---------------------------------------------------------------------------
$dotnet = Get-Command -Name dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw @"
dotnet was not found on PATH.
Install the .NET SDK from https://dotnet.microsoft.com/download
then open a NEW PowerShell window and run this script again.
"@
}

Write-Host "Using dotnet at: $($dotnet.Source)"
& dotnet --version
Write-Host ''

# ---------------------------------------------------------------------------
# Clean the publish folder so leftover files from an older publish do not
# sit next to the new ones (renamed dlls, old native plugins, etc.).
#
# Test-Path here uses the default PathType, which matches files OR folders.
# Remove-Item -Recurse deletes the folder and everything inside it.
# -Force lets PowerShell delete read-only files.
#
# If the app is still running, Windows will lock the .exe and this
# Remove-Item will fail. That is useful: better to stop and tell you, than
# to publish a mix of old and new files.
# ---------------------------------------------------------------------------
if (Test-Path -Path $PublishDir) {
    Write-Host "Removing existing folder: $PublishDir"
    try {
        Remove-Item -Path $PublishDir -Recurse -Force
    }
    catch {
        # $_ is the current error object inside catch. $_.Exception.Message
        # is the human-readable part. throw re-raises so the script stops.
        throw "Could not delete '$PublishDir'. Close the app (and anything using that folder) and try again. Details: $($_.Exception.Message)"
    }
}

# New-Item -ItemType Directory creates the folder. Out-Null swallows the
# DirectoryInfo object that New-Item would otherwise print to the screen.
New-Item -ItemType Directory -Path $PublishDir | Out-Null

# ---------------------------------------------------------------------------
# Splatting: instead of one very long command line, we build an ARRAY of
# arguments and pass it with @name (note the @, not $). Each element becomes
# one argument to the program. This is easier to read and to comment.
#
# "dotnet publish" (not "dotnet build") is the command that produces a
# runnable folder of exe + dlls.
#
# --self-contained true/false must be a string of "true" or "false".
# PowerShell's $true / $false are booleans; we convert them with .ToString()
# and then .ToLower() because dotnet wants lowercase true/false.
#
# --nologo hides the big .NET header so the output is easier to scan.
# ---------------------------------------------------------------------------
$publishArgs = @(
    'publish'
    $ProjectFile
    '--configuration',     $Configuration
    '--runtime',           $Runtime
    '--self-contained',    $SelfContained.ToString().ToLower()
    '--output',            $PublishDir
    '--nologo'
)

Write-Host 'Running:' -ForegroundColor DarkGray
Write-Host ("    dotnet " + ($publishArgs -join ' ')) -ForegroundColor DarkGray
Write-Host ''

# ---------------------------------------------------------------------------
# The call operator & runs a command stored in a variable. We use
# $dotnet.Source (full path) so we call the same exe Get-Command found.
#
# After an external program returns, $LASTEXITCODE is its integer exit
# code. 0 means success. PowerShell does NOT throw on a non-zero exit from
# native programs, even with $ErrorActionPreference = 'Stop', so we check
# it ourselves.
# ---------------------------------------------------------------------------
& $dotnet.Source @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# ---------------------------------------------------------------------------
# Confirm the exe landed where we expected. -Filter is faster than
# piping every file through Where-Object when the folder is huge.
# ---------------------------------------------------------------------------
$exe = Get-ChildItem -Path $PublishDir -Filter $ExeName -File |
       Select-Object -First 1

if ($null -eq $exe) {
    throw "Publish finished, but $ExeName was not found in $PublishDir"
}

$dllCount = @(Get-ChildItem -Path $PublishDir -Filter '*.dll' -File -Recurse).Count
$fileCount = @(Get-ChildItem -Path $PublishDir -File -Recurse).Count

# ---------------------------------------------------------------------------
# Write-Host prints to the screen. It is for humans. Write-Output (or a
# bare value) goes to the pipeline and can be captured by another script.
# For a "run this and look at it" script, Write-Host is the right choice.
#
# -ForegroundColor is optional decoration. It does not affect the files.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host 'Publish succeeded.' -ForegroundColor Green
Write-Host "  EXE     : $($exe.FullName)"
Write-Host "  Size    : $([Math]::Round($exe.Length / 1KB, 1)) KB"
Write-Host "  DLLs    : $dllCount"
Write-Host "  Files   : $fileCount"
Write-Host ''
Write-Host 'Run the app from that folder with:'
Write-Host "    & '$($exe.FullName)'" -ForegroundColor Yellow
Write-Host ''

if (-not $SelfContained) {
    Write-Host 'This copy needs the .NET Desktop Runtime on the PC that runs it.'
    Write-Host 'If that PC does not have .NET, publish again with:'
    Write-Host '    .\Publish.cmd -SelfContained' -ForegroundColor Yellow
    Write-Host ''
}
