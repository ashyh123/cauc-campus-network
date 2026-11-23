param(
    [string]$Version = "2.0.0",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$WindowsRoot = $PSScriptRoot
$SlnPath = Join-Path $WindowsRoot "CAUCCampusNetwork.sln"
$AppProjPath = Join-Path $WindowsRoot "src\CAUCCampusNetwork\CAUCCampusNetwork.csproj"
$DistDir = Join-Path $WindowsRoot "dist"
$PublishDir = Join-Path $DistDir "publish-win-x64"
$ZipName = "CAUC-Campus-Network-$Version-Windows-x64.zip"
$ZipPath = Join-Path $DistDir $ZipName
$SumsPath = Join-Path $DistDir "SHA256SUMS.txt"

if (-not $SkipTests) {
    Write-Host "Running Windows core unit tests..."
    dotnet test $SlnPath -c Release --verbosity normal
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE"
    }
}

if (Test-Path $DistDir) {
    Remove-Item -Recurse -Force $DistDir
}
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

Write-Host "Publishing self-contained Windows x64 executable (v$Version)..."
dotnet publish $AppProjPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    "-p:Version=$Version" `
    "-p:AssemblyVersion=$Version.0" `
    "-p:FileVersion=$Version.0" `
    "-p:PublishSingleFile=true" `
    "-p:IncludeNativeLibrariesForSelfExtract=true" `
    "-p:EnableCompressionInSingleFile=true" `
    "-p:DebugType=none" `
    "-p:DebugSymbols=false" `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$ExePath = Join-Path $PublishDir "CAUCCampusNetwork.exe"
if (-not (Test-Path $ExePath)) {
    throw "Expected published executable not found: $ExePath"
}

Write-Host "Packaging $ZipPath..."
Compress-Archive -Path $ExePath -DestinationPath $ZipPath -Force

$Hash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$Hash  $ZipName" | Set-Content -Path $SumsPath -Encoding ascii

Write-Host "Built: $ZipPath"
Write-Host "SHA256: $Hash"
