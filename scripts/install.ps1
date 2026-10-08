<#
.SYNOPSIS
Installs the tasker command line tool and the MCP server (tasker-mcpd) for the current Windows user.

.DESCRIPTION
Windows 10 version 1809 or newer, Windows PowerShell 5.1 or PowerShell 7. No administrator rights and no .NET are needed
(the build is self-contained). macOS and Linux: scripts/install.sh.

Where the files come from (one of; default: the release this script came with, that is the app folder next to it):
  -From PATH    a release archive (.zip) or a folder with the ready build (tasker.exe, tasker-mcpd.exe inside it or in its app folder)
  -Url URL      download a release archive; URL.sha256 next to it is checked

Layout (default -Prefix %LOCALAPPDATA%\Programs\Tasker):
  <Prefix>\app\   tasker.exe, tasker-mcpd.exe and libraries; this folder is added to the user PATH
What it does: copies the build (the old one stays until the new one works), adds <Prefix>\app to the user PATH (no administrator
rights; running windows are told about the change), enables the autostart of the MCP server (Task Scheduler task of the current user,
'tasker mcp autostart enable'), connects PowerShell Tab completion ('tasker completion pwsh --install'). A running MCP server is
stopped before the files are replaced (Windows locks running programs) and started again afterwards.
Your data (%LOCALAPPDATA%\Tasker: settings, logs) is never touched.

.PARAMETER Prefix
Install under this folder (default %LOCALAPPDATA%\Programs\Tasker).

.PARAMETER From
A release archive (.zip) or a folder with the ready build.

.PARAMETER Url
Address of a release archive; the checksum file URL.sha256 is downloaded and checked.

.PARAMETER NoPath
Do not add the program folder to the user PATH.

.PARAMETER NoCompletion
Do not connect PowerShell Tab completion.

.PARAMETER NoAutostart
Do not enable the autostart of the MCP server.

.PARAMETER NoVerify
With -Url: install even if the .sha256 file cannot be downloaded.

.PARAMETER Uninstall
Remove the tool: disables the autostart, stops the MCP server, removes the completion block, the PATH entry and the files.

.PARAMETER DryRun
Show what would be done and change nothing (the same as -WhatIf).

.EXAMPLE
.\install.ps1
.EXAMPLE
.\install.ps1 -From C:\Downloads\tasker-0.1.0-win-x64.zip -NoAutostart
.EXAMPLE
.\install.ps1 -Uninstall
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Prefix,
    [string]$From,
    [string]$Url,
    [switch]$NoPath,
    [switch]$NoCompletion,
    [switch]$NoAutostart,
    [switch]$NoVerify,
    [switch]$Uninstall,
    [switch]$DryRun
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- pure functions (no disk, no registry; tests load them)

# Windows architecture name (PROCESSOR_ARCHITECTURE) -> .NET RID; $null for architectures there is no build for.
function ConvertTo-WindowsRid {
    param([string]$Architecture)
    switch -Regex ($Architecture) {
        '^(AMD64|x64|X64)$' { return 'win-x64' }
        '^(ARM64|Arm64)$' { return 'win-arm64' }
        default { return $null }
    }
}

# True when the PATH value ($PathValue, entries split by ';') already has $Directory (case-insensitive, trailing '\' ignored).
function Test-PathEntry {
    param([string]$PathValue, [string]$Directory)
    $wanted = $Directory.TrimEnd('\', '/')
    foreach ($entry in $PathValue.Split(';')) {
        if ($entry.Trim().TrimEnd('\', '/') -ieq $wanted) { return $true }
    }
    return $false
}

# PATH value with $Directory added at the end (unchanged if it is there already); other entries stay as they are.
function Add-PathEntry {
    param([string]$PathValue, [string]$Directory)
    if (Test-PathEntry -PathValue $PathValue -Directory $Directory) { return $PathValue }
    if ([string]::IsNullOrEmpty($PathValue)) { return $Directory }
    if ($PathValue.EndsWith(';')) { return $PathValue + $Directory }
    return $PathValue + ';' + $Directory
}

# PATH value without $Directory; other entries (and their order and spelling) stay as they are.
function Remove-PathEntry {
    param([string]$PathValue, [string]$Directory)
    $wanted = $Directory.TrimEnd('\', '/')
    $kept = @()
    foreach ($entry in $PathValue.Split(';')) {
        if ($entry.Trim().TrimEnd('\', '/') -ieq $wanted) { continue }
        $kept += $entry
    }
    return ($kept -join ';')
}

# One command line argument quoted for Windows (CommandLineToArgvW rules): quotes only when needed.
function ConvertTo-Argument {
    param([string]$Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

# Value of 'key=' from the text of release.txt (a file next to the build: version, rid, commit); $null if there is none.
function Get-ReleaseInfoValue {
    param([string]$Text, [string]$Key)
    foreach ($line in ($Text -split "`r?`n")) {
        if ($line.StartsWith($Key + '=')) { return $line.Substring($Key.Length + 1).Trim() }
    }
    return $null
}

# The first line of a .sha256 file ('<hash>  <name>'): the hash in lower case.
function Get-ExpectedHash {
    param([string]$Text)
    $first = ($Text -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1)
    if (-not $first) { return $null }
    return ($first.Trim() -split '\s+')[0].ToLowerInvariant()
}

# Parameters that cannot go together: the message, or $null.
function Get-ParameterProblem {
    param([string]$From, [string]$Url, [bool]$Uninstall)
    if ($From -and $Url) { return 'choose one of -From and -Url' }
    if ($Uninstall -and ($From -or $Url)) { return '-Uninstall does not take -From or -Url' }
    return $null
}

# ---------------------------------------------------------------- Windows-only parts

# Stops with a message (printed by the last lines of the script; 'finally' blocks still run): code 1 for a failure, 2 for wrong parameters.
function Stop-Install {
    param([string]$Message, [int]$Code = 1)
    $failure = New-Object System.Exception($Message)
    $failure.Data['TaskerExitCode'] = $Code
    throw $failure
}

function Test-Windows {
    return ($env:OS -eq 'Windows_NT')
}

function Get-DefaultPrefix {
    $local = $env:LOCALAPPDATA
    if (-not $local) { $local = [Environment]::GetFolderPath('LocalApplicationData') }
    if (-not $local) { Stop-Install 'cannot find the local application data folder (LOCALAPPDATA)' }
    return (Join-Path (Join-Path $local 'Programs') 'Tasker')
}

# Runs a program and waits; the output is captured (UTF-8). Environment: extra variables for this run only.
function Invoke-Native {
    param([string]$File, [string[]]$Arguments = @(), [hashtable]$Environment = @{})
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $File
    $info.Arguments = (($Arguments | ForEach-Object { ConvertTo-Argument $_ }) -join ' ')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    foreach ($name in $Environment.Keys) { $info.EnvironmentVariables[$name] = [string]$Environment[$name] }
    $process = [System.Diagnostics.Process]::Start($info)
    $out = $process.StandardOutput.ReadToEndAsync()
    $err = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    return New-Object psobject -Property @{ ExitCode = $process.ExitCode; Out = $out.Result; Err = $err.Result }
}

# The user's PATH exactly as it is stored (unexpanded %VARS% stay), and its registry value kind.
function Get-UserPath {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $false)
    if ($null -eq $key) { return '' }
    try {
        $value = $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($null -eq $value) { return '' }
        return [string]$value
    }
    finally { $key.Dispose() }
}

function Set-UserPath {
    param([string]$Value)
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
    try { $key.SetValue('Path', $Value, [Microsoft.Win32.RegistryValueKind]::ExpandString) }
    finally { $key.Dispose() }
    Send-EnvironmentChange
}

# Tells running programs (Explorer, new terminals) that the environment changed: WM_SETTINGCHANGE "Environment" to all windows.
function Send-EnvironmentChange {
    if (-not ('Tasker.Native.Broadcast' -as [type])) {
        Add-Type -Namespace Tasker.Native -Name Broadcast -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.UIntPtr wParam, string lParam, uint flags, uint timeout, out System.UIntPtr result);
'@
    }
    $result = [UIntPtr]::Zero
    # HWND_BROADCAST = 0xFFFF, WM_SETTINGCHANGE = 0x1A, SMTO_ABORTIFHUNG = 2
    [void][Tasker.Native.Broadcast]::SendMessageTimeout([IntPtr]0xFFFF, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$result)
}

# Folder with the build inside $Root: the folder itself (tasker.exe in it), its app folder (a release is laid out so) or one nested release folder.
function Find-AppDirectory {
    param([string]$Root)
    foreach ($candidate in @($Root, (Join-Path $Root 'app'))) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'tasker.exe')) { return $candidate }
    }
    foreach ($child in (Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue)) {
        foreach ($candidate in @((Join-Path $child.FullName 'app'), $child.FullName)) {
            if (Test-Path -LiteralPath (Join-Path $candidate 'tasker.exe')) { return $candidate }
        }
    }
    return $null
}

function Write-Plan {
    param([string]$Text)
    Write-Host ('[dry-run] ' + $Text)
}

# ---------------------------------------------------------------- main

function Invoke-Main {
    $dry = [bool]$DryRun -or [bool]$WhatIfPreference
    $problem = Get-ParameterProblem -From $From -Url $Url -Uninstall ([bool]$Uninstall)
    if ($problem) { Stop-Install $problem 2 }

    $onWindows = Test-Windows
    if (-not $onWindows -and -not $dry) {
        Stop-Install 'this script is for Windows: on macOS and Linux use scripts/install.sh (-DryRun works anywhere)'
    }

    if ($onWindows) {
        $version = [Environment]::OSVersion.Version
        if ($version.Major -lt 10 -or ($version.Major -eq 10 -and $version.Build -lt 17763)) {
            Stop-Install ('Windows 10 version 1809 (build 17763) or newer is required, this is ' + $version)
        }
    }

    $architecture = $env:PROCESSOR_ARCHITEW6432
    if (-not $architecture) { $architecture = $env:PROCESSOR_ARCHITECTURE }
    $rid = ConvertTo-WindowsRid $architecture
    if (-not $rid) { Stop-Install ('unsupported processor architecture: ' + $architecture + ' (x64 and arm64 are supported)') }

    if (-not $Prefix) { $Prefix = Get-DefaultPrefix }
    $Prefix = [System.IO.Path]::GetFullPath($Prefix).TrimEnd('\', '/')
    $app = Join-Path $Prefix 'app'
    $tasker = Join-Path $app 'tasker.exe'

    if ($Uninstall) { Invoke-Uninstall -Prefix $Prefix -App $app -Tasker $tasker -Dry $dry; return }

    # ---- where the build comes from
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ('tasker-install-' + [Guid]::NewGuid().ToString('N'))
    $sourceText = $null
    $buildSource = $null
    $archive = $null
    try {
        if ($Url) {
            $sourceText = $Url
            if ($dry) {
                Write-Plan ('download ' + $Url + ' and ' + $Url + '.sha256, check the SHA256, unpack it')
            }
            else {
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                New-Item -ItemType Directory -Path $temp -Force | Out-Null
                $archive = Join-Path $temp ([System.IO.Path]::GetFileName(([Uri]$Url).AbsolutePath))
                Write-Host ('Downloading ' + $Url + ' ...')
                Invoke-WebRequest -Uri $Url -OutFile $archive -UseBasicParsing
                $sumFile = $archive + '.sha256'
                $haveSum = $true
                try { Invoke-WebRequest -Uri ($Url + '.sha256') -OutFile $sumFile -UseBasicParsing } catch { $haveSum = $false }
                if ($haveSum) {
                    $expected = Get-ExpectedHash ([System.IO.File]::ReadAllText($sumFile))
                    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
                    if ($expected -ne $actual) { Stop-Install ('the checksum of the download does not match ' + $Url + '.sha256 (expected ' + $expected + ', got ' + $actual + '): nothing was installed') }
                    Write-Host 'SHA256 checked.'
                }
                elseif (-not $NoVerify) {
                    Stop-Install ('cannot download ' + $Url + '.sha256 to check the archive: nothing was installed (-NoVerify installs without the check)')
                }
                else { [Console]::Error.WriteLine('Warning: ' + $Url + '.sha256 is not available: installing without the checksum check (-NoVerify)') }
            }
        }
        elseif ($From) {
            $From = [System.IO.Path]::GetFullPath($From)
            $sourceText = $From
            if (Test-Path -LiteralPath $From -PathType Container) {
                $buildSource = Find-AppDirectory $From
                if (-not $buildSource) { Stop-Install ('no tasker.exe in ' + $From + ' (nor in its app folder)') }
            }
            elseif (Test-Path -LiteralPath $From -PathType Leaf) {
                $archive = $From
            }
            else { Stop-Install ($From + ' does not exist') }
        }
        else {
            $here = $PSScriptRoot
            $bundled = $null
            if ($here) { $bundled = Join-Path $here 'app' }
            if ($bundled -and (Test-Path -LiteralPath (Join-Path $bundled 'tasker.exe'))) {
                $buildSource = $bundled
                $sourceText = $bundled
            }
            else {
                Stop-Install 'nothing to install: run this script from an unpacked release, or pass -From FILE|FOLDER or -Url ADDRESS'
            }
        }

        if ($archive -and $dry -and $Url) {
            # dry run of a download: nothing to look into
        }
        elseif ($archive) {
            if (-not $archive.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Install ('unknown archive type: ' + $archive + ' (a .zip release archive or a folder is expected)')
            }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            if ($dry) {
                # look into the archive without unpacking it: only the platform check
                $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
                try {
                    $names = @($zip.Entries | ForEach-Object { $_.FullName })
                    if (-not ($names | Where-Object { $_ -match '(^|/)tasker\.exe$' })) { Stop-Install ('the archive ' + $archive + ' does not contain tasker.exe') }
                    $infoEntry = $zip.Entries | Where-Object { $_.FullName -match '(^|/)release\.txt$' } | Select-Object -First 1
                    if ($infoEntry) {
                        $reader = New-Object System.IO.StreamReader($infoEntry.Open())
                        try { $built = Get-ReleaseInfoValue -Text $reader.ReadToEnd() -Key 'rid' } finally { $reader.Dispose() }
                        if ($built -and $built -ne $rid) { Stop-Install ('this build is for ' + $built + ', but this machine is ' + $rid + ': download the ' + $rid + ' archive') }
                    }
                }
                finally { $zip.Dispose() }
            }
            else {
                New-Item -ItemType Directory -Path $temp -Force | Out-Null
                $unpacked = Join-Path $temp 'unpacked'
                [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $unpacked)
                $buildSource = Find-AppDirectory $unpacked
                if (-not $buildSource) { Stop-Install 'the archive does not contain tasker.exe' }
            }
        }

        if ($buildSource) {
            if (-not (Test-Path -LiteralPath (Join-Path $buildSource 'tasker-mcpd.exe'))) {
                Stop-Install ($buildSource + ' has no tasker-mcpd.exe: the MCP server is a part of every release')
            }
            foreach ($infoPath in @((Join-Path (Split-Path $buildSource -Parent) 'release.txt'), (Join-Path $buildSource 'release.txt'))) {
                if (Test-Path -LiteralPath $infoPath) {
                    $built = Get-ReleaseInfoValue -Text ([System.IO.File]::ReadAllText($infoPath)) -Key 'rid'
                    if ($built -and $built -ne $rid) { Stop-Install ('this build is for ' + $built + ', but this machine is ' + $rid + ': download the ' + $rid + ' archive') }
                    break
                }
            }
        }

        # ---- the plan (the whole run in a dry run)
        $daemonRunning = $false
        if ((Test-Path -LiteralPath $tasker)) {
            $status = Invoke-Native $tasker @('mcp', 'status')
            $daemonRunning = ($status.ExitCode -eq 0)
        }

        if ($dry) {
            Write-Plan ('platform: ' + $rid + ' (Windows PowerShell or PowerShell ' + $PSVersionTable.PSVersion + ')')
            Write-Plan ('install into: ' + $app)
            Write-Plan ('source: ' + $(if ($sourceText) { $sourceText } else { $Url }))
            if ($daemonRunning) { Write-Plan 'the running MCP server would be stopped before the files are replaced and started again afterwards' }
            Write-Plan ('copy the build to a staging folder next to ' + $app + ', run a smoke test (tasker project create in a temporary data folder), replace ' + $app + ' (the old one is kept until then)')
            if ($NoPath) { Write-Plan 'PATH: not changed (-NoPath)' }
            else {
                $current = ''
                if ($onWindows) { $current = Get-UserPath }
                if (Test-PathEntry -PathValue $current -Directory $app) { Write-Plan ('PATH: ' + $app + ' is in the user PATH already') }
                else { Write-Plan ('PATH: add ' + $app + ' to the user PATH and broadcast WM_SETTINGCHANGE') }
            }
            if ($NoCompletion) { Write-Plan 'completion: not connected (-NoCompletion)' }
            else { Write-Plan 'completion: run "tasker completion pwsh --install" (script in the Tasker data folder, one marked block in the PowerShell profiles)' }
            if ($NoAutostart) { Write-Plan 'autostart: not enabled (-NoAutostart)' }
            else { Write-Plan 'autostart: run "tasker mcp autostart enable" (Task Scheduler task of the current user) unless it is enabled already' }
            Write-Plan 'nothing was changed'
            return
        }

        # ---- install: stage, smoke test, swap
        New-Item -ItemType Directory -Path $Prefix -Force | Out-Null
        $staging = Join-Path $Prefix ('.staging-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        try {
            Write-Host ('Installing tasker from ' + $sourceText + ' (' + $rid + ')...')
            New-Item -ItemType Directory -Path $staging -Force | Out-Null
            Copy-Item -Path (Join-Path $buildSource '*') -Destination $staging -Recurse -Force
            # a download is marked as coming from the internet: remove the mark so that SmartScreen/PowerShell do not ask again
            Get-ChildItem -LiteralPath $staging -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue

            $smoke = Join-Path $temp 'smoke'
            New-Item -ItemType Directory -Path (Join-Path $smoke 'workspace') -Force | Out-Null
            $check = Invoke-Native (Join-Path $staging 'tasker.exe') @('project', 'create', 'Smoke', '--workspace', (Join-Path $smoke 'workspace')) @{ TASKER_HOME = (Join-Path $smoke 'home') }
            if ($check.ExitCode -ne 0) {
                [Console]::Error.WriteLine(($check.Out + $check.Err).Trim())
                Stop-Install 'the built tasker does not work: the smoke test failed (nothing was installed)'
            }
            $check = Invoke-Native (Join-Path $staging 'tasker-mcpd.exe') @('--help')
            if ($check.ExitCode -ne 0) { Stop-Install 'the built tasker-mcpd does not work: the smoke test failed (nothing was installed)' }

            # Windows locks a running program: stop the MCP server (it is started again below), then swap the folders.
            if ($daemonRunning) {
                Write-Host 'The MCP server is running: stopping it to replace the files (it is started again afterwards)...'
                [void](Invoke-Native $tasker @('mcp', 'stop'))
            }
            $old = $app + '.old'
            if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
            $moved = $false
            if (Test-Path -LiteralPath $app) {
                try { Move-Item -LiteralPath $app -Destination $old; $moved = $true }
                catch {
                    if ($daemonRunning) { [void](Invoke-Native $tasker @('mcp', 'start')) }
                    Stop-Install ('cannot replace ' + $app + ': a program from it is still running (close terminals using tasker and run this script again). ' + $_.Exception.Message)
                }
            }
            Move-Item -LiteralPath $staging -Destination $app
            if ($moved) { Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue }
        }
        finally {
            if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
        }

        $versionResult = Invoke-Native $tasker @('--version')
        $installed = $versionResult.Out.Trim()
        if (-not $installed) { $installed = 'unknown' }
        Write-Host ('Installed tasker ' + $installed)
        Write-Host ('  program: ' + $app)

        # ---- PATH
        if ($NoPath) {
            Write-Host 'PATH: not changed (-NoPath).'
        }
        else {
            $current = Get-UserPath
            if (Test-PathEntry -PathValue $current -Directory $app) {
                Write-Host ('PATH: ' + $app + ' is in your user PATH already')
            }
            else {
                Set-UserPath (Add-PathEntry -PathValue $current -Directory $app)
                Write-Host ('PATH: ' + $app + ' is added to your user PATH - open a new terminal window to use "tasker"')
            }
            # this window too
            if (-not (Test-PathEntry -PathValue $env:Path -Directory $app)) { $env:Path = Add-PathEntry -PathValue $env:Path -Directory $app }
        }

        # ---- Tab completion for PowerShell
        Write-Host ''
        if ($NoCompletion) {
            Write-Host 'Tab completion: not connected to PowerShell (-NoCompletion): your profiles were not touched.'
        }
        else {
            $completion = Invoke-Native $tasker @('completion', 'pwsh', '--install')
            Write-Host $completion.Out.TrimEnd()
            if ($completion.ExitCode -ne 0) {
                [Console]::Error.WriteLine('Warning: could not connect Tab completion (tasker completion pwsh --install failed): tasker itself is installed. ' + $completion.Err.Trim())
            }
        }

        # ---- the MCP server: autostart and the new build
        $restartDaemon = {
            Write-Host 'Starting the MCP server...'
            $started = Invoke-Native $tasker @('mcp', 'start')
            Write-Host ($started.Out + $started.Err).Trim()
            if ($started.ExitCode -ne 0) { [Console]::Error.WriteLine("Warning: the MCP server did not start: see 'tasker mcp status' and the log in %LOCALAPPDATA%\Tasker\logs") }
        }
        if ($NoAutostart) {
            if ($daemonRunning) { & $restartDaemon }
        }
        else {
            $autostartStatus = Invoke-Native $tasker @('mcp', 'autostart', 'status', '--json')
            if ($autostartStatus.Out -match '"enabled":\s*true') {
                Write-Host ''
                Write-Host 'MCP server autostart: already enabled, left as it is'
                if ($daemonRunning) { & $restartDaemon }
            }
            else {
                Write-Host ''
                Write-Host "MCP server autostart: enabling (a Task Scheduler task of the current user; it starts the MCP server now and at every login; turn it off with 'tasker mcp autostart disable')..."
                $enabled = Invoke-Native $tasker @('mcp', 'autostart', 'enable')
                Write-Host ($enabled.Out + $enabled.Err).Trim()
                if ($enabled.ExitCode -ne 0) {
                    [Console]::Error.WriteLine("Warning: autostart was not enabled; tasker itself is installed. You can retry with 'tasker mcp autostart enable' or start the server by hand: 'tasker mcp start'.")
                    if ($daemonRunning) { & $restartDaemon }
                }
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Invoke-Uninstall {
    param([string]$Prefix, [string]$App, [string]$Tasker, [bool]$Dry)

    $installed = (Test-Path -LiteralPath $App)
    if (-not $installed) {
        Write-Host ('Nothing to uninstall: tasker is not installed under ' + $Prefix)
        return
    }
    if ($Dry) {
        Write-Plan 'run "tasker mcp autostart disable" and "tasker mcp stop"'
        Write-Plan 'run "tasker completion pwsh --uninstall" (removes the marked block from the PowerShell profiles)'
        Write-Plan ('remove ' + $App + ' from the user PATH')
        Write-Plan ('delete ' + $App + ' (and ' + $Prefix + ' if it is empty)')
        Write-Plan 'your data (%LOCALAPPDATA%\Tasker) is kept; nothing was changed'
        return
    }

    if (Test-Path -LiteralPath $Tasker) {
        # The Task Scheduler task and git hooks keep the path to the program: switch the task off while the program is still there.
        [void](Invoke-Native $Tasker @('mcp', 'autostart', 'disable'))
        [void](Invoke-Native $Tasker @('mcp', 'stop'))
        $completion = Invoke-Native $Tasker @('completion', 'pwsh', '--uninstall')
        if ($completion.Out.Trim()) { Write-Host $completion.Out.TrimEnd() }
    }

    $current = Get-UserPath
    if (Test-PathEntry -PathValue $current -Directory $App) {
        Set-UserPath (Remove-PathEntry -PathValue $current -Directory $App)
        Write-Host ('Removed ' + $App + ' from your user PATH')
    }
    try { Remove-Item -LiteralPath $App -Recurse -Force }
    catch { Stop-Install ('cannot delete ' + $App + ': a program from it is still running (close terminals using tasker and run this script again). ' + $_.Exception.Message) }
    Remove-Item -LiteralPath ($App + '.old') -Recurse -Force -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $Prefix) -and -not (Get-ChildItem -LiteralPath $Prefix -Force)) { Remove-Item -LiteralPath $Prefix -Force }

    Write-Host ('tasker is uninstalled from ' + $Prefix)
    Write-Host 'Your data in %LOCALAPPDATA%\Tasker is kept.'
    Write-Host "Git hooks installed with 'tasker hooks install' stay in place and do nothing without tasker: remove them with 'tasker hooks uninstall' before uninstalling if you do not need them."
}

# Dot-sourcing (. .\install.ps1) only defines the functions: that is how the tests check them.
if ($MyInvocation.InvocationName -ne '.') {
    try {
        Invoke-Main
    }
    catch {
        $code = 1
        if ($_.Exception.Data.Contains('TaskerExitCode')) { $code = [int]$_.Exception.Data['TaskerExitCode'] }
        [Console]::Error.WriteLine('Error: ' + $_.Exception.Message)
        if ($code -eq 2) { [Console]::Error.WriteLine('Run "Get-Help .\install.ps1" for the parameters.') }
        exit $code
    }
}
