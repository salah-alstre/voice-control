<#
.SYNOPSIS
  Builds the release of Voice Commander and produces the Windows installer in target\release\bundle.
.DESCRIPTION
  One command: clean, build (Release), test, publish (self-contained win-x64), compile the Inno Setup installer.
    target\release\publish\   intermediate: the self-contained app the installer is built from
    target\release\bundle\    final output: VoiceCommander-<version>-Setup.exe
  The version comes from Directory.Build.props. Speech models are never packaged; users download them in the app.
  Use -Portable to also add the portable ZIP and SHA256SUMS.txt to the bundle folder.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Portable
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "Voice Commander $version" -ForegroundColor Cyan

$release = "$root\target\release"
$publish = "$release\publish"
$bundle = "$release\bundle"

# 1. Clean old release output (including the legacy artifacts\ folder that earlier versions of this script generated).
foreach ($dir in @($release, "$root\artifacts")) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
New-Item -ItemType Directory -Force $publish, $bundle | Out-Null

# 2. Build
dotnet build "$root\VoiceCommander.sln" -c $Configuration -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# 3. Tests
if (-not $SkipTests) {
    Write-Host "Running tests..." -ForegroundColor Cyan
    dotnet test "$root\tests\VoiceCommander.Tests\VoiceCommander.Tests.csproj" -c $Configuration -nologo --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed - not packaging." }
}

# 4. Publish (intermediate)
Write-Host "Publishing (self-contained, win-x64)..." -ForegroundColor Cyan
dotnet publish "$root\src\VoiceCommander.App\VoiceCommander.App.csproj" `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false `
    -o $publish -nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

# Keep only what a win-x64 install needs: other native runtimes, symbols and anything that looks like user data or a model are removed.
Get-ChildItem "$publish\runtimes" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne "win-x64" } | Remove-Item -Recurse -Force
Get-ChildItem $publish -Recurse -File |
    Where-Object { $_.Extension -in ".pdb", ".log", ".part", ".bin", ".gguf", ".ggml" -or $_.Name -in "settings.json", "commands.json", "applications.json", "history.jsonl" } |
    Remove-Item -Force

# 5. Installer (Inno Setup 6) -> bundle
$setup = "$bundle\VoiceCommander-$version-Setup.exe"
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isinfo.php and run this script again." }
& $iscc "/DAppVersion=$version" "/DSourceDir=$publish" "/DOutDir=$bundle" "$root\installer\VoiceCommander.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }

# 6. Optional portable ZIP + checksums (extracts into one folder, VoiceCommander-<version>\)
if ($Portable) {
    $zip = "$bundle\VoiceCommander-$version-win-x64-portable.zip"
    $stage = "$release\_stage"
    New-Item -ItemType Directory -Force "$stage\VoiceCommander-$version" | Out-Null
    Copy-Item "$publish\*" "$stage\VoiceCommander-$version" -Recurse
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Entries are added one by one with '/' separators: Windows PowerShell's CreateFromDirectory writes '\', which 7-Zip/unzip/macOS extract wrongly.
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $stagePath = (Resolve-Path $stage).Path.TrimEnd('\')
        Get-ChildItem $stagePath -Recurse -File | ForEach-Object {
            $entry = $_.FullName.Substring($stagePath.Length + 1).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
    Remove-Item $stage -Recurse -Force
    $lines = foreach ($f in @($zip, $setup)) { "{0}  {1}" -f (Get-FileHash $f -Algorithm SHA256).Hash.ToLower(), (Split-Path $f -Leaf) }
    Set-Content "$bundle\SHA256SUMS.txt" $lines -Encoding ASCII
}

# 7. Verify the installer and report
if (-not (Test-Path $setup)) { throw "Installer was not created: $setup" }
$info = (Get-Item $setup).VersionInfo
# Inno Setup pads the version-info strings with trailing whitespace, hence Trim().
$productName = "$($info.ProductName)".Trim()
$productVersion = "$($info.ProductVersion)".Trim()
if ($productName -ne "Voice Commander" -or $productVersion -ne $version) {
    throw "Installer metadata mismatch: '$productName' / '$productVersion' (expected 'Voice Commander' / '$version')."
}
$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ""
Write-Host "FINAL BUNDLE:    $bundle" -ForegroundColor Green
Write-Host "FINAL INSTALLER: $setup  ($sizeMb MB, $productName $productVersion)" -ForegroundColor Green
