# Builds the Release VSIX and publishes it to the Visual Studio Marketplace.
#
# Before the first run:
#   1. https://marketplace.visualstudio.com/manage adresinde bir yayıncı oluşturun.
#      Görünen ad "Codecraftopia" olmalı (VSIX'teki Publisher ile aynı); kimlik publishManifest.json'daki "publisher" ile aynı olmalı.
#   2. https://dev.azure.com > User settings > Personal access tokens: Organization = "All accessible organizations",
#      Scopes = Marketplace > Manage olan bir token oluşturun.
#
# Çalıştırma:  powershell -File marketplace\publish.ps1
# Token VS_MARKETPLACE_PAT ortam değişkeninden okunur; yoksa gizli olarak sorulur. Token hiçbir dosyaya yazılmaz.

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$manifest = Join-Path $PSScriptRoot "publishManifest.json"
$vsix = Join-Path $root "src\ClaudeCodeVS\bin\Release\ClaudeCodeVS.vsix"

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Workload.VisualStudioExtension -property installationPath
if (-not $vsPath) { throw "Visual Studio extension development iş yükü bulunamadı." }
$msbuild = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
$publisherTool = Join-Path $vsPath "VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe"

Write-Host "Release derleniyor..."
& $msbuild (Join-Path $root "ClaudeCodeVS.sln") /restore /p:Configuration=Release /p:DeployExtension=false /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Derleme başarısız." }

$publisher = (Get-Content $manifest -Raw | ConvertFrom-Json).publisher
[xml]$vsixManifest = Get-Content (Join-Path $root "src\ClaudeCodeVS\source.extension.vsixmanifest") -Raw
$identity = $vsixManifest.PackageManifest.Metadata.Identity
Write-Host ""
Write-Host "Yayınlanacak: $($vsixManifest.PackageManifest.Metadata.DisplayName) $($identity.Version)"
Write-Host "Yayıncı kimliği: $publisher   (VSIX yayıncı adı: $($identity.Publisher))"
Write-Host "Paket: $vsix"
Write-Host ""
$answer = Read-Host "Marketplace'te HERKESE AÇIK olarak yayınlansın mı? (evet/hayır)"
if ($answer -ne "evet") { Write-Host "İptal edildi."; return }

$token = $env:VS_MARKETPLACE_PAT
if (-not $token) {
    $secure = Read-Host "Marketplace erişim token'ı (PAT)" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

& $publisherTool publish -payload $vsix -publishManifest $manifest -personalAccessToken $token
if ($LASTEXITCODE -ne 0) { throw "Yayınlama başarısız (çıktıya bakın)." }
Write-Host "Yayınlandı: https://marketplace.visualstudio.com/items?itemName=$publisher.$($vsixManifest.PackageManifest.Metadata.DisplayName -replace '\s','')"
Write-Host "Not: Marketplace'in incelemesi ve listede görünmesi birkaç dakika sürebilir."
