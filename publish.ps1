# Publishes all binaries self-contained for MSI packaging.
# Output: publish\teacher\, publish\student\

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$publishRoot = Join-Path $root "publish"

Write-Host "=== Cleaning previous publish output ===" -ForegroundColor Cyan
if (Test-Path $publishRoot) { Remove-Item $publishRoot -Recurse -Force }
New-Item -ItemType Directory -Path $publishRoot | Out-Null

$teacherDir = Join-Path $publishRoot "teacher"
$studentDir = Join-Path $publishRoot "student"
New-Item -ItemType Directory -Path $teacherDir | Out-Null
New-Item -ItemType Directory -Path $studentDir | Out-Null

$publishArgs = @(
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)

Write-Host "`n=== Publishing Teacher ===" -ForegroundColor Cyan
dotnet publish src\ClassroomCtrl.Teacher\ClassroomCtrl.Teacher.csproj `
    @publishArgs -o $teacherDir
if ($LASTEXITCODE -ne 0) { throw "Teacher publish failed" }

Write-Host "`n=== Publishing Student.Service ===" -ForegroundColor Cyan
dotnet publish src\ClassroomCtrl.Student.Service\ClassroomCtrl.Student.Service.csproj `
    @publishArgs -o $studentDir
if ($LASTEXITCODE -ne 0) { throw "Service publish failed" }

Write-Host "`n=== Publishing Student.Agent ===" -ForegroundColor Cyan
dotnet publish src\ClassroomCtrl.Student.Agent\ClassroomCtrl.Student.Agent.csproj `
    @publishArgs -o $studentDir
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed" }

Write-Host "`n=== Publishing Student.Watchdog ===" -ForegroundColor Cyan
dotnet publish src\ClassroomCtrl.Student.Watchdog\ClassroomCtrl.Student.Watchdog.csproj `
    @publishArgs -o $studentDir
if ($LASTEXITCODE -ne 0) { throw "Watchdog publish failed" }

Write-Host "`n=== Publish complete ===" -ForegroundColor Green
Write-Host "Teacher output: $teacherDir"
Write-Host "Student output: $studentDir"
Write-Host "Teacher files: $((Get-ChildItem $teacherDir -File).Count)"
Write-Host "Student files: $((Get-ChildItem $studentDir -File).Count)"