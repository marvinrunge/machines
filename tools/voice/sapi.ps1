# Speaks a list of lines with Windows' built-in SAPI voices into 48 kHz mono WAV files.
# Input: a UTF-8 JSON file [{ "text": "...", "culture": "de-DE", "rate": 0, "out": "C:/.../x.wav" }, ...]
# Used by tools/voice/placeholders.py; the user's own recordings replace these files' results later.
param([Parameter(Mandatory = $true)][string]$Jobs)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$list = Get-Content -Raw -Encoding UTF8 $Jobs | ConvertFrom-Json
$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(48000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$voices = (New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices() | ForEach-Object { $_.VoiceInfo }
foreach ($j in $list) {
    $v = $voices | Where-Object { $_.Culture.Name -eq $j.culture } | Select-Object -First 1
    if ($null -eq $v) { $v = $voices | Where-Object { $_.Culture.TwoLetterISOLanguageName -eq $j.culture.Substring(0, 2) } | Select-Object -First 1 }
    if ($null -eq $v) { throw "no SAPI voice for $($j.culture)" }
    $s = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $s.SelectVoice($v.Name)
    $s.Rate = [int]$j.rate
    $s.SetOutputToWaveFile($j.out, $fmt)
    $s.Speak([string]$j.text)
    $s.Dispose()
    Write-Output "$($v.Name): $($j.out)"
}
