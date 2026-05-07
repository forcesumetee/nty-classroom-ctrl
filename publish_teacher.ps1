# publish_teacher.ps1
# Self-contained Teacher publish (includes .NET 10 runtime)
# Output: publish\Teacher\ — folder ready for Inno Setup [Files] section

$ErrorActionPreference = "Stop"
Set-Location C:\ClassroomCtrl

Write-Host "===============================================" -ForegroundColor Cyan
Write-Host "  Publishing Teacher (self-contained)" -ForegroundColor Cyan
Write-Host "===============================================" -ForegroundColor Cyan

Remove-Item "publish\Teacher" -Recurse -Force -EA SilentlyContinue

dotnet publish src\ClassroomCtrl.Teacher `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:PublishTrimmed=false `
    -o publish\Teacher

if ($LASTEXITCODE -eq 0) {
    $size = (Get-ChildItem "publish\Teacher" -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
    Write-Host "[OK] Teacher published - Size: $([math]::Round($size, 1)) MB" -ForegroundColor Green
} else {
    Write-Host "[FAIL] Publish failed (code $LASTEXITCODE)" -ForegroundColor Red
    exit 1
}
