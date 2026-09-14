param([Parameter(Mandatory)][string]$Rid)
$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot '../artifacts/native-app'
$configurationPath = Join-Path $app 'appsettings.json'
$config = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json -AsHashtable
$config['Desktop'] = @{ Enabled = $true }
$config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configurationPath -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../README.md'),(Join-Path $PSScriptRoot '../THIRD-PARTY-NOTICES.md') -Destination $app
if ($Rid.StartsWith('osx')) {
    $launcher = Join-Path $app 'Start TrueModel.command'
    "#!/bin/sh`ncd `"`$(dirname `"`$0`")`"`nexec ./TrueModel`n" | Set-Content -LiteralPath $launcher -Encoding utf8 -NoNewline
    & chmod +x $launcher
}
$archive = Join-Path $PSScriptRoot "../artifacts/TrueModel-$Rid.zip"
if ($Rid.StartsWith('osx')) {
    & ditto -c -k --keepParent $app $archive
    if ($LASTEXITCODE -ne 0) { throw 'Archive failed' }
} else {
    Compress-Archive -Path "$app/*" -DestinationPath $archive -Force
}
Write-Output "Packaged $archive"
