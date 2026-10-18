param([string]$Downloads)
# One-time elevated setup for MicFX: install Equalizer APO + ReaPlugs + RNNoise,
# and point Equalizer APO's config at C:\ProgramData\MicFX\active.txt (owned by the user; written by the tray app).
# No permissions are changed.
$ErrorActionPreference = 'Continue'
Start-Transcript -Path "$env:TEMP\micfx-setup.log" -Force | Out-Null

Write-Host "== Installing ReaPlugs (ReaComp)"
Start-Process "$Downloads\dl_reaplugs\reaplugs236_x64-install.exe" -ArgumentList '/S' -Wait

Write-Host "== Installing Equalizer APO"
if (-not (Test-Path "$env:ProgramFiles\EqualizerAPO\EqualizerAPO.dll")) {
    Start-Process "$Downloads\dl_eqapo\EqualizerAPO-x64-1.4.2.exe" -ArgumentList '/S' -Wait
}

Write-Host "== Installing RNNoise VST"
$vst = "$env:ProgramFiles\EqualizerAPO\VSTPlugins"
New-Item -ItemType Directory -Force $vst | Out-Null
Copy-Item "$Downloads\dl_rnnoise\x\win-rnnoise\vst\rnnoise_mono.dll", "$Downloads\dl_rnnoise\x\win-rnnoise\vst\rnnoise_stereo.dll" $vst -Force

Write-Host "== Pointing Equalizer APO at the MicFX preset file"
$cfg = "$env:ProgramFiles\EqualizerAPO\config\config.txt"
if (Test-Path $cfg) { Copy-Item $cfg "$cfg.before-micfx" -Force }
Set-Content -Path $cfg -Encoding ASCII -Value @(
    '# Managed by MicFX tray app. Original saved as config.txt.before-micfx',
    'Include: C:\ProgramData\MicFX\active.txt'
)

Write-Host "== Done"
Get-ChildItem "$env:ProgramFiles\EqualizerAPO" | Select-Object -ExpandProperty Name
Get-ChildItem "$env:ProgramFiles\VSTPlugins", $vst -Recurse -Filter *.dll -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName
Stop-Transcript | Out-Null
