<#
    Builds the classic MuggleBuds installer (MSI) with the .NET SDK alone, no Visual Studio. It
    goes to artifacts\release\v<Version>.

    The MSI isn't signed, so Windows shows "Unknown publisher" when it's run. It installs just for
    you, or for everyone from the wizard's Advanced step. For the MSIX packages and the signed
    installers use scripts/Build-Release.ps1 instead, which needs Visual Studio and a certificate.

    The WiX Toolset comes in as a NuGet package: https://wixtoolset.org/docs/intro/
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+\.0$')]
    [string] $Version,

    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',

    # Leaves the pictures of the buds (they belong to Nothing) out of the installer
    [switch] $WithoutPictures
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$artifacts = Join-Path $root 'artifacts'
$release = Join-Path $artifacts "release\v$Version"
New-Item -ItemType Directory -Force -Path $release | Out-Null

# License Page For The Installer (plain text to RTF)
$licenseText = (Get-ChildItem $root -Filter 'LICENSE*' | Sort-Object Name | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n`n"
$rtfBody = $licenseText -replace '\\', '\\' -replace '\{', '\{' -replace '\}', '\}' -replace "`r?`n", "\par`n"
$licenseRtf = Join-Path $artifacts 'License.rtf'
Set-Content $licenseRtf "{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs18 $rtfBody}" -Encoding ascii

# Unpackaged Publish
$publish = Join-Path $artifacts "publish\$Platform"
if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

dotnet publish (Join-Path $root 'src\MuggleBuds.App\MuggleBuds.App.csproj') -c Release `
    -p:Platform=$Platform `
    -p:WindowsPackageType=None `
    -p:Version=$Version `
    -p:PublishDir="$publish\\" `
    -nologo -v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "Publishing the classic build failed for $Platform ($LASTEXITCODE)."
}

if ($WithoutPictures) {
    Get-ChildItem (Join-Path $publish 'Assets') -Filter 'CMF Buds Pro 2*.png' -ErrorAction SilentlyContinue | Remove-Item -Force
}

# The MSI Around It
$msiOut = Join-Path $artifacts "msi\$Platform"
dotnet build (Join-Path $root 'installer\MuggleBuds.Installer.wixproj') -c Release `
    -p:Platform=$Platform `
    -p:ProductVersion=$Version `
    -p:PublishDir="$publish\\" `
    -p:LicenseRtf=$licenseRtf `
    -o $msiOut -nologo -v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "Building the MSI failed for $Platform ($LASTEXITCODE)."
}

$msi = Join-Path $release "MuggleBuds_${Version}_$($Platform.ToLower()).msi"
Copy-Item (Join-Path $msiOut 'MuggleBuds.msi') $msi
Write-Output $msi
