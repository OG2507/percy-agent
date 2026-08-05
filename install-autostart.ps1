$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$startupDir = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startupDir "Percy Agent.lnk"
$targetPath = Join-Path $projectDir "start-percy-agent.cmd"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $targetPath
$shortcut.WorkingDirectory = $projectDir
$shortcut.Description = "Start Percy Agent with Windows"
$shortcut.Save()

Write-Host "Percy Agent will start when you sign in to Windows."
Write-Host "Shortcut: $shortcutPath"

