# build_all.ps1 - Full pipeline: publish + compile installers
# Runs publish_teacher.ps1 + publish_student.ps1, then ISCC against both .iss
# files, then verifies the two .exe installers landed in installer\output\

$ErrorActionPreference = "Stop"
Set-Location C:\ClassroomCtrl

$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    Write-Host "[FAIL] Inno Setup not found at $iscc" -ForegroundColor Red
    Write-Host "Download from: https://jrsoftware.org/isinfo.php" -ForegroundColor Yellow
    exit 1
}

Write-Host "Step 1/3: Publishing..." -ForegroundColor Magenta
& .\publish_teacher.ps1
& .\publish_student.ps1

Write-Host ""
Write-Host "Step 2/3: Compiling installers..." -ForegroundColor Magenta
& $iscc "installer\teacher_setup.iss"
if ($LASTEXITCODE -ne 0) { Write-Host "[FAIL] Teacher installer failed" -ForegroundColor Red; exit 1 }

& $iscc "installer\student_setup.iss"
if ($LASTEXITCODE -ne 0) { Write-Host "[FAIL] Student installer failed" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "Step 3/3: Verifying outputs..." -ForegroundColor Magenta
$outputs = Get-ChildItem "installer\output" -Filter "*.exe"
if ($outputs.Count -eq 2) {
    Write-Host ""
    Write-Host "[OK] BUILD SUCCESSFUL - Output files:" -ForegroundColor Green
    $outputs | ForEach-Object {
        $size = [math]::Round($_.Length / 1MB, 1)
        Write-Host "  $($_.Name) ($size MB)" -ForegroundColor Green
    }
    Write-Host ""
    Write-Host "Ready to deliver to customer" -ForegroundColor Cyan
} else {
    Write-Host "[FAIL] Expected 2 .exe files, got $($outputs.Count)" -ForegroundColor Red
    exit 1
}
