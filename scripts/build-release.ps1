# Baut LunaTranscribe als Release, installiert nach %LOCALAPPDATA%\Programs\LunaTranscribe und startet die App.
#
#   .\scripts\build-release.ps1
#
# Kein Admin nötig (Installation im Benutzerprofil). Self-contained: die fertige App braucht kein
# installiertes .NET. Zum Bauen wird das .NET 8 SDK benötigt:
#   winget install Microsoft.DotNet.SDK.8
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$AppName = "LunaTranscribe"
$Dest = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$Publish = "publish"

Write-Host "> Release-Build (dotnet publish)"
if (Test-Path $Publish) { Remove-Item -Recurse -Force $Publish }
dotnet publish "$AppName\$AppName.csproj" -c Release -o $Publish -nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "Build fehlgeschlagen"; exit 1 }

Write-Host "> Laufende Instanz beenden"
Get-Process -Name $AppName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "> Installieren nach $Dest"
New-Item -ItemType Directory -Force $Dest | Out-Null
# Nur den Inhalt ersetzen – Einstellungen und Modelle liegen unter %APPDATA% / %LOCALAPPDATA% und bleiben erhalten.
Get-ChildItem $Dest | Remove-Item -Recurse -Force
Copy-Item -Recurse -Force (Join-Path $Publish "*") $Dest
Remove-Item -Recurse -Force $Publish

Write-Host "> Startmenü-Verknüpfung"
$Shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "$AppName.lnk"
$Shell = New-Object -ComObject WScript.Shell
$Link = $Shell.CreateShortcut($Shortcut)
$Link.TargetPath = Join-Path $Dest "$AppName.exe"
$Link.WorkingDirectory = $Dest
$Link.Description = "Lokale Diktierfunktion (offline)"
$Link.Save()

Write-Host "> Starten"
Start-Process (Join-Path $Dest "$AppName.exe")
Write-Host "Fertig. Autostart: Einstellungen -> Allgemein -> 'Beim Anmelden automatisch starten'."
