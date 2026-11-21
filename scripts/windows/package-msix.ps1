[CmdletBinding()]
param(
    [string]$Channel = 'Dev',
    [string]$CertificatePath,
    [Security.SecureString]$CertificatePassword = [Security.SecureString]::new(),
    [string]$MakeAppxPath = 'makeappx.exe',
    [string]$SignToolPath = 'signtool.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Channel -cne 'Dev') {
    throw 'MSIX packaging supports only Dev. Preview and Production are rejected because Windows continuity is Dev-only.'
}
if ($env:OS -ne 'Windows_NT') { throw 'MSIX packaging requires Windows.' }
$makeappx = (Get-Command $MakeAppxPath -ErrorAction Stop).Source
$signtool = $null
if ($CertificatePath) {
    $CertificatePath = (Resolve-Path -LiteralPath $CertificatePath).Path
    $signtool = (Get-Command $SignToolPath -ErrorAction Stop).Source
}

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $repo 'artifacts/msix'
$build = Join-Path $output ([Guid]::NewGuid().ToString('N'))
$layout = Join-Path $build 'layout'
New-Item -ItemType Directory -Path $layout -Force | Out-Null

Push-Location $repo
try {
    & dotnet publish src/Openza.Tasks.Desktop/Openza.Tasks.Desktop.csproj -c Release -r win-x64 `
        --self-contained true -p:OpenzaWindowsContinuity=Dev -p:OpenzaChannel=Dev `
        -p:WindowsAppSDKSelfContained=true -o $layout
    if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed ($LASTEXITCODE)." }

    Copy-Item (Join-Path $repo 'packaging/windows/Package.Dev.appxmanifest') (Join-Path $layout 'AppxManifest.xml')
    Copy-Item (Join-Path $repo 'src/Openza.Tasks/Assets') (Join-Path $layout 'Assets') -Recurse
    foreach ($file in @('Openza.Tasks.Desktop.exe', 'coreclr.dll', 'Microsoft.WindowsAppRuntime.dll')) {
        if (-not (Test-Path (Join-Path $layout $file))) { throw "Self-contained publish is missing $file." }
    }

    $package = Join-Path $build 'Openza.Tasks.Desktop_0.1.1.0_x64.msix'
    & $makeappx pack /d $layout /p $package /o
    if ($LASTEXITCODE -ne 0) { throw "MSIX packing failed ($LASTEXITCODE)." }

    if ($CertificatePath) {
        $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertificatePassword)
        try {
            $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
            & $signtool sign /fd SHA256 /f $CertificatePath /p $password $package
            if ($LASTEXITCODE -ne 0) { throw "MSIX signing failed ($LASTEXITCODE)." }
        }
        finally {
            $password = $null
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
        }
        & $signtool verify /pa $package
        if ($LASTEXITCODE -ne 0) { throw "MSIX signature verification failed ($LASTEXITCODE)." }
    }
    Write-Output "MSIX: $package"
}
finally {
    Pop-Location
}
