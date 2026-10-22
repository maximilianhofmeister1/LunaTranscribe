# Baut LunaTranscribe für Windows als Release und legt Installer und ZIP für GitHub Releases nach dist/ (im
# Repo-Wurzelordner, neben der DMG der macOS-Version).
#
#   .\windows\scripts\package-release.ps1
#
# Ergebnis (jeweils mit .sha256 im Format von `shasum -a 256`):
#   dist\LunaTranscribe-<Version>-windows-x64-setup.exe   Installer (Inno Setup, pro Benutzer, ohne Administrator)
#   dist\LunaTranscribe-<Version>-windows-x64.zip         portable Variante zum Entpacken
# Für den Installer wird Inno Setup 6 gebraucht: winget install JRSoftware.InnoSetup
# Self-contained: Nutzer brauchen kein installiertes .NET. Nicht signiert – Windows SmartScreen warnt beim ersten Start
# („Weitere Informationen“ → „Trotzdem ausführen“).
$ErrorActionPreference = "Stop"
$Windows = Resolve-Path (Join-Path $PSScriptRoot "..")
$Root = Resolve-Path (Join-Path $Windows "..")
Set-Location $Windows

$AppName = "LunaTranscribe"
$Project = "$AppName\$AppName.csproj"
$Version = ([xml](Get-Content $Project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $Version) { Write-Error "Version in $Project nicht gefunden"; exit 1 }

$Stage = Join-Path $Root "build\package-windows\$AppName"
$Dist = Join-Path $Root "dist"
$Zip = Join-Path $Dist "$AppName-$Version-windows-x64.zip"
$SetupName = "$AppName-$Version-windows-x64-setup"

# Inno-Setup-Compiler: Benutzerinstallation (winget) oder systemweit
$Iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) { Write-Error "Inno Setup 6 nicht gefunden – winget install JRSoftware.InnoSetup"; exit 1 }

function Write-Checksum([string]$File) {
    $Hash = (Get-FileHash -Algorithm SHA256 $File).Hash.ToLowerInvariant()
    "$Hash  $(Split-Path -Leaf $File)" | Set-Content -NoNewline -Encoding ascii "$File.sha256"
}

Write-Host "> Release-Build $Version (dotnet publish)"
if (Test-Path (Split-Path $Stage)) { Remove-Item -Recurse -Force (Split-Path $Stage) }
dotnet publish $Project -c Release -o $Stage -nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "Build fehlgeschlagen"; exit 1 }
if (-not (Test-Path (Join-Path $Stage "mcp\luna-mcp.exe"))) { Write-Error "mcp\luna-mcp.exe fehlt im Publish-Ordner"; exit 1 }

Write-Host "> Schnelltest (luna-mcp --selftest)"
& (Join-Path $Stage "mcp\luna-mcp.exe") --selftest | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "luna-mcp-Selbsttest fehlgeschlagen"; exit 1 }

# Lizenzen gehören in beide Varianten
Copy-Item (Join-Path $Root "LICENSE") (Join-Path $Stage "LICENSE.txt")
Copy-Item (Join-Path $Root "THIRD_PARTY_NOTICES.md") (Join-Path $Stage "THIRD_PARTY_NOTICES.md")
New-Item -ItemType Directory -Force $Dist | Out-Null

Write-Host "> Installer erstellen ($SetupName.exe)"
& $Iscc /Q "/DAppVersion=$Version" "/DSourceDir=$Stage" "/DOutputDir=$Dist" "/DOutputBaseFilename=$SetupName" "installer\$AppName.iss"
if ($LASTEXITCODE -ne 0) { Write-Error "Installer fehlgeschlagen"; exit 1 }
$Setup = Join-Path $Dist "$SetupName.exe"
Write-Checksum $Setup

# Lesehilfe nur im ZIP: Installation ohne Installer
@"
LunaTranscribe $Version für Windows (x64) – portable Variante

Bequemer ist der Installer (LunaTranscribe-$Version-windows-x64-setup.exe) – er legt Startmenü-Eintrag, Autostart und
Deinstallation an. Ohne Installer:

1. Den Ordner „$AppName“ z. B. nach %LOCALAPPDATA%\Programs\ kopieren.
2. $AppName.exe starten – die App erscheint im Infobereich der Taskleiste.
   Windows SmartScreen warnt beim ersten Start, weil die App nicht signiert ist:
   „Weitere Informationen“ → „Trotzdem ausführen“.
3. Beim ersten Start wird das Spracherkennungsmodell geladen (~0,7 GB), danach läuft alles offline.

Anleitung, Datenschutz und Quellcode: https://github.com/maximilianhofmeister1/LunaTranscribe/tree/main/windows
"@ | Set-Content -Encoding utf8 (Join-Path $Stage "LIESMICH.txt")

Write-Host "> ZIP erstellen ($Zip)"
if (Test-Path $Zip) { Remove-Item -Force $Zip }
Compress-Archive -Path $Stage -DestinationPath $Zip -CompressionLevel Optimal
Write-Checksum $Zip
Remove-Item -Recurse -Force (Split-Path $Stage)

foreach ($File in @($Setup, $Zip)) {
    Write-Host "Fertig: $File ($([math]::Round((Get-Item $File).Length / 1MB)) MB)"
    Get-Content "$File.sha256"
}
