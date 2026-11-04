# Tab completion for tasker (PowerShell 5.1 and 7+): commands, options and values (projects, statuses, tasks...) of the current workspace.
# Printed by 'tasker completion pwsh'; 'tasker completion pwsh --install' dot-sources it from your $PROFILE.
# All the knowledge about commands lives in tasker: this script only asks it with the [suggest] directive and quotes the answer
# for PowerShell. cmd.exe has no programmable completion, so it is not supported there.
# The file is pure ASCII on purpose: Windows PowerShell 5.1 reads a script without a BOM in the ANSI code page.

Register-ArgumentCompleter -Native -CommandName 'tasker', 'tasker.exe' -ScriptBlock {
    param($wordToComplete, $commandAst, $cursorPosition)

    try {
        # The command line after the program name, up to the cursor, as typed (quotes and backticks kept).
        $text = $commandAst.Extent.Text
        $start = $commandAst.Extent.StartOffset
        $position = $cursorPosition - $start
        if ($position -lt 0) { return }
        if ($position -gt $text.Length) { $text = $text + (' ' * ($position - $text.Length)) }
        $text = $text.Substring(0, $position)
        $nameEnd = $commandAst.CommandElements[0].Extent.EndOffset - $start
        if ($nameEnd -gt $text.Length) { return }
        $line = $text.Substring($nameEnd).TrimStart()

        $program = Get-Command -Name 'tasker' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $program) { return }

        # One argument for a Windows command line (CommandLineToArgvW rules): the line may contain quotes and backslashes,
        # and Windows PowerShell 5.1 does not escape them when it starts a program.
        $quoteArgument = {
            param([string]$value)
            $builder = New-Object System.Text.StringBuilder
            [void]$builder.Append([char]'"')
            $slashes = 0
            foreach ($char in $value.ToCharArray()) {
                if ($char -eq [char]'\') { $slashes++; continue }
                if ($char -eq [char]'"') {
                    [void]$builder.Append([char]'\', $slashes * 2 + 1)
                } elseif ($slashes -gt 0) {
                    [void]$builder.Append([char]'\', $slashes)
                }
                [void]$builder.Append($char)
                $slashes = 0
            }
            [void]$builder.Append([char]'\', $slashes * 2)
            [void]$builder.Append([char]'"')
            $builder.ToString()
        }

        $info = New-Object System.Diagnostics.ProcessStartInfo
        $info.FileName = $program.Source
        $info.Arguments = '[suggest:' + $line.Length + ':pwsh] ' + (& $quoteArgument $line)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        # Set-Location does not change the directory of the PowerShell process: pass the current folder (the workspace) explicitly.
        $info.WorkingDirectory = (Get-Location -PSProvider FileSystem).ProviderPath

        $process = [System.Diagnostics.Process]::Start($info)
        $errorTask = $process.StandardError.ReadToEndAsync()
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        if (-not $outputTask.Wait(5000)) {
            try { $process.Kill() } catch { }
            return
        }
        $output = $outputTask.Result
        $process.Dispose()

        foreach ($candidate in ($output -split "`r?`n")) {
            if ($candidate.Length -eq 0) { continue }

            # Words with spaces or characters special to PowerShell go in single quotes. 'Name=' and 'TSK-' continue in the same
            # word, so their quote stays open.
            $insert = $candidate
            if ($candidate -match "[\s`"'`$&;|<>(){}@#,``\u2018-\u201F]") {
                $insert = "'" + ($candidate -replace "['\u2018\u2019\u201A\u201B]", '$0$0')
                if ($candidate -notmatch '[=:-]$') { $insert = $insert + "'" }
            }

            $type = if ($candidate.StartsWith('-')) { 'ParameterName' } else { 'ParameterValue' }
            [System.Management.Automation.CompletionResult]::new($insert, $candidate, $type, $candidate)
        }
    } catch {
        # Completion never prints errors: no answer means PowerShell offers file names, as usual.
    }
}
