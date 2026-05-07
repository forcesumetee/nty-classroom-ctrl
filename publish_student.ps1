# publish_student.ps1
# Self-contained Student components (Service + Agent + Watchdog)
# Output: publish\Student.{Service|Agent|Watchdog}\

$ErrorActionPreference = "Stop"
Set-Location C:\ClassroomCtrl

Write-Host "===============================================" -ForegroundColor Cyan
Write-Host "  Publishing Student components" -ForegroundColor Cyan
Write-Host "===============================================" -ForegroundColor Cyan

@("Student.Service", "Student.Agent", "Student.Watchdog") | ForEach-Object {
    Remove-Item "publish\$_" -Recurse -Force -EA SilentlyContinue
}

@(
    @{ Name = "Service";  Project = "ClassroomCtrl.Student.Service" },
    @{ Name = "Agent";    Project = "ClassroomCtrl.Student.Agent" },
    @{ Name = "Watchdog"; Project = "ClassroomCtrl.Student.Watchdog" }
) | ForEach-Object {
    Write-Host "Publishing $($_.Name)..." -ForegroundColor Yellow
    dotnet publish "src\$($_.Project)" `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o "publish\Student.$($_.Name)"

    if ($LASTEXITCODE -ne 0) {
        Write-Host "[FAIL] $($_.Name) publish failed" -ForegroundColor Red
        exit 1
    }
}

$totalSize = 0
@("publish\Student.Service", "publish\Student.Agent", "publish\Student.Watchdog") | ForEach-Object {
    $totalSize += (Get-ChildItem $_ -Recurse | Measure-Object -Property Length -Sum).Sum
}
Write-Host "[OK] All Student components published - Total: $([math]::Round($totalSize / 1MB, 1)) MB" -ForegroundColor Green
