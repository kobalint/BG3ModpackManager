$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\BG3ModpackManager.App\BG3ModpackManager.App.csproj"
$outDir = Join-Path $root "dist"

Write-Host "Publishing portable BG3ModpackManager.exe to $outDir..."
dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $outDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exePath = Join-Path $outDir "BG3ModpackManager.exe"
if (-not (Test-Path $exePath)) {
    throw "Expected executable not found at $exePath"
}

Write-Host "Published successfully: $exePath"
