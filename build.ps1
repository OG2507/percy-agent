$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $projectDir "publish"
$env:DOTNET_CLI_HOME = Join-Path $projectDir ".dotnet-home"

dotnet publish $projectDir --configuration Release --runtime win-x64 --self-contained false --output $publishDir

Write-Host ""
Write-Host "Percy Agent built successfully."
Write-Host "Run: $projectDir\start-percy-agent.cmd"
