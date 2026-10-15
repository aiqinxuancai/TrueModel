param([Parameter(Mandatory)][ValidateSet('win-x64', 'osx-x64', 'osx-arm64')][string]$Rid)
$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot '../artifacts/single-file-app'
$executableName = if ($Rid -eq 'win-x64') { 'TrueModel.exe' } else { 'TrueModel' }
$allowedFiles = @($executableName, 'README.md', 'THIRD-PARTY-NOTICES.md', 'Start TrueModel.command')
if (!(Test-Path -LiteralPath (Join-Path $app $executableName) -PathType Leaf)) { throw 'Single-file executable missing; publish with the Desktop profile first.' }
if (Get-ChildItem -LiteralPath $app -Force | Where-Object { $_.PSIsContainer -or $_.Name -notin $allowedFiles }) {
    throw 'Unexpected files in publish output; the desktop application must be a single executable.'
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../README.md'),(Join-Path $PSScriptRoot '../THIRD-PARTY-NOTICES.md') -Destination $app
if ($Rid.StartsWith('osx')) {
    $launcher = Join-Path $app 'Start TrueModel.command'
    "#!/bin/sh`ncd `"`$(dirname `"`$0`")`"`nexec ./TrueModel`n" | Set-Content -LiteralPath $launcher -Encoding utf8 -NoNewline
    & chmod +x $launcher
    if ($LASTEXITCODE -ne 0) { throw 'Launcher permissions failed' }
}
$archive = Join-Path $PSScriptRoot "../artifacts/TrueModel-$Rid.zip"
if ($Rid.StartsWith('osx')) {
    & ditto -c -k --keepParent $app $archive
    if ($LASTEXITCODE -ne 0) { throw 'Archive failed' }
} else {
    Compress-Archive -Path "$app/*" -DestinationPath $archive -Force
}
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $archive -Leaf)" | Set-Content -LiteralPath "$archive.sha256" -Encoding utf8NoBOM
Write-Output "Packaged $archive"
