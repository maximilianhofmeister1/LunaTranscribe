# Baut LunaTranscribe für Windows als Release und packt es als ZIP für GitHub Releases nach dist/ (im Repo-Wurzelordner,
# neben der DMG der macOS-Version).
#
#   .\windows\scripts\package-release.ps1
#
# Ergebnis: dist\LunaTranscribe-<Version>-windows-x64.zip + .sha256 (gleiches Format wie `shasum -a 256`).
# Self-contained: Nutzer brauchen kein installiertes .NET. Die EXE ist nicht signiert – Windows SmartScreen warnt beim
# ersten Start („Weitere Informationen“ → „Trotzdem ausführen“).
$ErrorActionPreference = "Stop"
$Windows = Join-Path $PSScriptRoot ".."
$Root = Resolve-Path (Join-Path $Windows "..")
Set-Location $Windows

$AppName = "LunaTranscribe"
$Project = "$AppName\$AppName.csproj"
$Version = ([xml](Get-Content $Project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $Version) { Write-Error "Version in $Project nicht gefunden"; exit 1 }

$Stage = Join-Path $Root "build\package-windows\$AppName"
$Dist = Join-Path $Root "dist"
$Zip = Join-Path $Dist "$AppName-$Version-windows-x64.zip"

Write-Host "> Release-Build $Version (dotnet publish)"
if (Test-Path (Split-Path $Stage)) { Remove-Item -Recurse -Force (Split-Path $Stage) }
dotnet publish $Project -c Release -o $Stage -nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "Build fehlgeschlagen"; exit 1 }
if (-not (Test-Path (Join-Path $Stage "mcp\luna-mcp.exe"))) { Write-Error "mcp\luna-mcp.exe fehlt im Publish-Ordner"; exit 1 }

Write-Host "> Schnelltest (luna-mcp --selftest)"
& (Join-Path $Stage "mcp\luna-mcp.exe") --selftest | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "luna-mcp-Selbsttest fehlgeschlagen"; exit 1 }

# Lesehilfe im ZIP: Installation ohne Skript, Lizenzen
Copy-Item (Join-Path $Root "LICENSE") (Join-Path $Stage "LICENSE.txt")
Copy-Item (Join-Path $Root "THIRD_PARTY_NOTICES.md") (Join-Path $Stage "THIRD_PARTY_NOTICES.md")
@"
LunaTranscribe $Version für Windows (x64)

1. Den Ordner „$AppName“ z. B. nach %LOCALAPPDATA%\Programs\ kopieren.
2. $AppName.exe starten – die App erscheint im Infobereich der Taskleiste.
   Windows SmartScreen warnt beim ersten Start, weil die App nicht signiert ist:
   „Weitere Informationen“ → „Trotzdem ausführen“.
3. Beim ersten Start wird das Spracherkennungsmodell geladen (~0,7 GB), danach läuft alles offline.

Anleitung, Datenschutz und Quellcode: https://github.com/maximilianhofmeister1/LunaTranscribe/tree/main/windows
"@ | Set-Content -Encoding utf8 (Join-Path $Stage "LIESMICH.txt")

Write-Host "> ZIP erstellen ($Zip)"
New-Item -ItemType Directory -Force $Dist | Out-Null
if (Test-Path $Zip) { Remove-Item -Force $Zip }
Compress-Archive -Path $Stage -DestinationPath $Zip -CompressionLevel Optimal

$Hash = (Get-FileHash -Algorithm SHA256 $Zip).Hash.ToLowerInvariant()
"$Hash  $(Split-Path -Leaf $Zip)" | Set-Content -NoNewline -Encoding ascii "$Zip.sha256"
Remove-Item -Recurse -Force (Split-Path $Stage)

Write-Host "Fertig: $Zip ($([math]::Round((Get-Item $Zip).Length / 1MB)) MB)"
Get-Content "$Zip.sha256"
