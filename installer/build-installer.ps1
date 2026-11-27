# Builds installer\dist\TokenHub-Setup-<version>.exe — one self-extracting file
# that installs TokenHub for the current user. Neither the .NET runtime nor
# administrator rights are needed on the target machine.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 1.1.2
#
# Uses IExpress, which ships with Windows; nothing has to be installed first.
param(
    [string]$Version = '',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path $here -Parent

$appProject = Join-Path $root 'TokenHub\TokenHub.csproj'
$serviceProject = Join-Path $root 'TokenHub.PoolService\TokenHub.PoolService.csproj'

if (-not $Version) {
    $found = Select-String -LiteralPath $appProject -Pattern '<Version>([^<]+)</Version>'
    if (-not $found) { throw "No <Version> in $appProject." }
    $Version = $found.Matches[0].Groups[1].Value
}

# IExpress reads its script as ANSI, so the whole build runs in an ASCII-only
# folder even when the project itself lives under a Cyrillic path.
$work = Join-Path $env:TEMP 'TokenHub-installer'
$stage = Join-Path $work 'stage'
$dist = Join-Path $here 'dist'
$singleFile = Join-Path $root 'publish-singlefile'
$setupName = "TokenHub-Setup-$Version.exe"

Write-Host "TokenHub $Version — сборка установщика"

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage, $dist | Out-Null

# 1. The app: one self-contained executable, no .NET runtime required.
dotnet publish $appProject -c $Configuration -p:PublishProfile=SingleFile --nologo
if ($LASTEXITCODE -ne 0) { throw 'Сборка приложения не удалась.' }

# 2. The optional Windows service, also a single self-contained file.
dotnet publish $serviceProject -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw 'Сборка службы не удалась.' }

Copy-Item (Join-Path $singleFile 'TokenHub.exe') $stage -Force

# 3. Install scripts. IExpress flattens the archive, so they land next to the
#    executables; UTF-8 with BOM keeps the Russian text readable in PowerShell.
$bom = New-Object System.Text.UTF8Encoding($true)
foreach ($name in @('install.ps1', 'uninstall.ps1')) {
    $path = Join-Path $stage $name
    [System.IO.File]::WriteAllText($path, (Get-Content -LiteralPath (Join-Path $here "payload\$name") -Raw -Encoding UTF8), $bom)
}
Copy-Item (Join-Path $here 'payload\install.cmd') $stage -Force

# 4. The IExpress script.
$files = Get-ChildItem -LiteralPath $stage -File | Sort-Object Name
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('[Version]')
$lines.Add('Class=IEXPRESS')
$lines.Add('SEDVersion=3')
$lines.Add('[Options]')
$lines.Add('PackagePurpose=InstallApp')
$lines.Add('ShowInstallProgramWindow=1')
$lines.Add('HideExtractAnimation=1')
$lines.Add('UseLongFileName=1')
$lines.Add('InsideCompressed=0')
$lines.Add('CAB_FixedSize=0')
$lines.Add('CAB_ResvCodeSigning=0')
$lines.Add('RebootMode=N')
$lines.Add('InstallPrompt=')
$lines.Add('DisplayLicense=')
$lines.Add('FinishMessage=')
$lines.Add("TargetName=$(Join-Path $work $setupName)")
$lines.Add("FriendlyName=TokenHub $Version")
$lines.Add('AppLaunched=install.cmd')
$lines.Add('PostInstallCmd=<None>')
$lines.Add('AdminQuietInstCmd=')
$lines.Add('UserQuietInstCmd=')
$lines.Add('SourceFiles=SourceFiles')
$lines.Add('[Strings]')
for ($i = 0; $i -lt $files.Count; $i++) {
    $lines.Add("FILE$i=`"$($files[$i].Name)`"")
}
$lines.Add('[SourceFiles]')
$lines.Add("SourceFiles0=$stage\")
$lines.Add('[SourceFiles0]')
for ($i = 0; $i -lt $files.Count; $i++) {
    $lines.Add("%FILE$i%=")
}

$sed = Join-Path $work 'tokenhub.sed'
[System.IO.File]::WriteAllText($sed, ($lines -join "`r`n") + "`r`n", [System.Text.Encoding]::Default)

# 5. Build the package inside the ASCII work folder, then move it into dist:
#    IExpress writes TargetName as ANSI, so a Cyrillic path here would be lost.
$iexpress = Join-Path $env:SystemRoot 'System32\iexpress.exe'
if (-not (Test-Path $iexpress)) { $iexpress = 'iexpress.exe' }

$built = Join-Path $work $setupName

# IExpress is a GUI executable: PowerShell does not wait for it, and the package is
# still being written when the call returns. So wait for the file to appear and for
# its size to stop growing instead of trusting the exit code.
& $iexpress /N $sed

$deadline = (Get-Date).AddMinutes(15)
$lastSize = -1
$stableSince = Get-Date
while ((Get-Date) -lt $deadline) {
    if (Test-Path $built) {
        $size = (Get-Item $built).Length
        if ($size -ne $lastSize) {
            $lastSize = $size
            $stableSince = Get-Date
        }
        elseif ($size -gt 0 -and ((Get-Date) - $stableSince).TotalSeconds -ge 5) {
            break
        }
    }

    Start-Sleep -Milliseconds 500
}

if (-not (Test-Path $built)) { throw "Установщик не создан: $built" }

$setup = Join-Path $dist $setupName
Copy-Item $built $setup -Force

$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "Готово: $setup ($size МБ)"
