# NTY Classroom Control -- Student MSI build wrapper.
#
# Handles the publish-then-stage-then-build sequence the WiX MSI needs:
#   1. .\publish.ps1                                publish all binaries
#   2. mv publish\student\Service.exe -> publish\student-svc\
#      (so the wildcard <Files> in StudentFiles.wxs doesn't double-harvest the
#      file already pinned by the <ServiceInstall> component)
#   3. dotnet build installers\Student.Installer    produce the .msi
#
# Output: installers\Student.Installer\bin\<Configuration>\NTY-ClassroomCtrl-Student-1.0.0.msi

param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try
{
    Write-Host "=== Step 1: publish all binaries ===" -ForegroundColor Cyan
    & "$repoRoot\publish.ps1"
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed" }

    Write-Host "`n=== Step 2: stage Service.exe out of publish\student\ ===" -ForegroundColor Cyan
    $studentDir = Join-Path $repoRoot "publish\student"
    $svcStaging = Join-Path $repoRoot "publish\student-svc"
    if (Test-Path $svcStaging) { Remove-Item $svcStaging -Recurse -Force }
    New-Item -ItemType Directory -Path $svcStaging | Out-Null

    $svcExe = Join-Path $studentDir "ClassroomCtrl.Student.Service.exe"
    if (-not (Test-Path $svcExe)) { throw "Service.exe missing at $svcExe -- did publish run?" }
    Move-Item -Force $svcExe (Join-Path $svcStaging "ClassroomCtrl.Student.Service.exe")
    Write-Host "  Moved Service.exe to $svcStaging"

    Write-Host "`n=== Step 3: build Student.msi ($Configuration) ===" -ForegroundColor Cyan
    dotnet build "$repoRoot\installers\Student.Installer\Student.Installer.wixproj" -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

    $msi = Get-ChildItem "$repoRoot\installers\Student.Installer\bin\$Configuration" -Filter "*.msi" |
           Sort-Object LastWriteTime -Descending |
           Select-Object -First 1
    if ($msi)
    {
        $sizeMB = [Math]::Round($msi.Length / 1MB, 1)
        Write-Host "`n=== MSI built ===" -ForegroundColor Green
        Write-Host "  Path: $($msi.FullName)"
        Write-Host "  Size: $sizeMB MB"
    }
}
finally
{
    Pop-Location
}
