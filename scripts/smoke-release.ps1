param([Parameter(Mandatory)][ValidateSet('win-x64', 'osx-x64', 'osx-arm64')][string]$Rid)
$ErrorActionPreference = 'Stop'
$archive = (Resolve-Path (Join-Path $PSScriptRoot "../artifacts/TrueModel-$Rid.zip")).Path
$expectedHash = ((Get-Content -LiteralPath "$archive.sha256" -Raw) -split '\s+')[0]
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $expectedHash) { throw 'Archive checksum mismatch' }
$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$workspace = Join-Path $tempBase ('truemodel-smoke-' + [guid]::NewGuid())
$unpacked = Join-Path $workspace 'unpacked'
$isolated = Join-Path $workspace 'isolated'
$data = Join-Path $workspace 'data'
New-Item -ItemType Directory -Path $unpacked, $isolated, $data | Out-Null
if ($IsWindows) {
    Expand-Archive -LiteralPath $archive -DestinationPath $unpacked
} else {
    & ditto -x -k $archive $unpacked
    if ($LASTEXITCODE -ne 0) { throw 'Archive extraction failed' }
}
$executableName = if ($IsWindows) { 'TrueModel.exe' } else { 'TrueModel' }
$executables = @(Get-ChildItem -LiteralPath $unpacked -Filter $executableName -Recurse -File)
if ($executables.Count -ne 1) { throw 'Expected exactly one application executable in the archive' }
if (!$IsWindows) {
    foreach ($file in @($executables[0].FullName, (Join-Path $executables[0].DirectoryName 'Start TrueModel.command'))) {
        & /bin/test -x $file
        if ($LASTEXITCODE -ne 0) { throw "Executable permissions missing: $file" }
    }
}
# Only copy the executable, so missing bundled assets or native libraries fail here.
Copy-Item -LiteralPath $executables[0].FullName -Destination $isolated
$executable = Join-Path $isolated $executableName
$environmentNames = @('DataDirectory', 'Desktop__Enabled', 'Desktop__OpenBrowser', 'Admin__Username', 'Admin__Password', 'ConnectionStrings__Default', 'DOTNET_BUNDLE_EXTRACT_BASE_DIR')
$originalEnvironment = @{}
foreach ($name in $environmentNames) { $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$success = $false
try {
    foreach ($name in $environmentNames) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
    $env:DataDirectory = $data
    $env:Desktop__OpenBrowser = 'false'
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $workspace 'bundle'
    $password = 'Smoke-' + [guid]::NewGuid().ToString()
    for ($iteration = 0; $iteration -lt 2; $iteration++) {
        $log = Join-Path $data "stdout-$iteration.log"
        $err = Join-Path $data "stderr-$iteration.log"
        $parameters = @{ FilePath = $executable; WorkingDirectory = $data; RedirectStandardOutput = $log; RedirectStandardError = $err; PassThru = $true }
        if ($IsWindows) { $parameters['WindowStyle'] = 'Hidden' }
        $process = Start-Process @parameters
        try {
            $url = $null
            for ($attempt = 0; $attempt -lt 100; $attempt++) {
                $process.Refresh()
                if ($process.HasExited) { throw 'Application exited during startup' }
                $text = Get-Content -LiteralPath $log -Raw -ErrorAction SilentlyContinue
                if ($text -match 'Now listening on: (http://127\.0\.0\.1:\d+)') { $url = $Matches[1]; break }
                Start-Sleep -Milliseconds 300
            }
            if (!$url) { throw 'No loopback listener found' }
            if ((Invoke-RestMethod "$url/health").status -ne 'ok') { throw 'Health check failed' }
            if ((Invoke-WebRequest $url -TimeoutSec 10).Content -notmatch 'https://github.com/aiqinxuancai/TrueModel') { throw 'Homepage GitHub link missing' }
            foreach ($asset in @('/app.css', '/app.js', '/vendor/lucide.min.js', '/favicon.ico')) {
                if ((Invoke-WebRequest "$url$asset" -TimeoutSec 10).RawContentLength -eq 0) { throw "Bundled asset missing: $asset" }
            }
            $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
            $csrf = (Invoke-RestMethod "$url/api/session" -WebSession $session).token
            $headers = @{ 'X-CSRF-TOKEN' = $csrf }
            $credentials = @{ username = 'admin'; password = $password } | ConvertTo-Json
            $setup = Invoke-RestMethod "$url/api/setup"
            if ($iteration -eq 0) {
                if (!$setup.required) { throw 'First-run setup missing' }
                Invoke-RestMethod "$url/api/setup" -Method Post -WebSession $session -Headers $headers -ContentType 'application/json' -Body $credentials | Out-Null
            } elseif ($setup.required) { throw 'Administrator did not persist' }
            Invoke-RestMethod "$url/api/login" -Method Post -WebSession $session -Headers $headers -ContentType 'application/json' -Body $credentials | Out-Null
            $headers['X-CSRF-TOKEN'] = (Invoke-RestMethod "$url/api/session" -WebSession $session).token
            if ($iteration -eq 0) {
                Invoke-RestMethod "$url/api/settings" -Method Put -WebSession $session -Headers $headers -ContentType 'application/json' -Body '{"challengeCount":6,"intervalMinutes":0,"maxConcurrency":2,"timeoutSeconds":240}' | Out-Null
                $site = Invoke-RestMethod "$url/api/sites" -Method Post -WebSession $session -Headers $headers -ContentType 'application/json' -Body '{"name":"Smoke site","baseUrl":"https://example.test/v1"}'
                $key = Invoke-RestMethod "$url/api/sites/$($site.id)/keys" -Method Post -WebSession $session -Headers $headers -ContentType 'application/json' -Body '{"name":"Smoke key","value":"smoke-placeholder"}'
                Invoke-RestMethod "$url/api/keys/$($key.id)/models" -Method Post -WebSession $session -Headers $headers -ContentType 'application/json' -Body '{"name":"smoke-model"}' | Out-Null
            }
            if ((Invoke-RestMethod "$url/api/settings" -WebSession $session).challengeCount -ne 6) { throw 'Challenge count did not persist' }
            $sites = @(Invoke-RestMethod "$url/api/sites" -WebSession $session)
            if ($sites.Count -ne 1 -or $sites[0].keys[0].models[0].name -ne 'smoke-model') { throw 'Site, key or model did not persist' }
            $banks = @(Invoke-RestMethod "$url/api/banks" -WebSession $session)
            if ($banks.Count -ne 1 -or !$banks[0].active) { throw 'Bundled fingerprint bank missing' }
            if (!(Test-Path (Join-Path $data 'truemodel.db'))) { throw 'SQLite missing' }
        } catch {
            Get-Content -LiteralPath $log, $err -ErrorAction SilentlyContinue | Write-Host
            throw
        } finally {
            if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
            $process.Dispose()
        }
    }
    $success = $true
} finally {
    foreach ($name in $environmentNames) {
        if ($null -eq $originalEnvironment[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process') }
    }
    if ($success) {
        $resolvedWorkspace = (Resolve-Path -LiteralPath $workspace).Path
        if (!$resolvedWorkspace.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolvedWorkspace -Leaf) -notlike 'truemodel-smoke-*') { throw 'Unexpected smoke-test directory' }
        Remove-Item -LiteralPath $resolvedWorkspace -Recurse -Force
    } else {
        Write-Host "Smoke-test files retained at $workspace"
    }
}
Write-Output 'Verified archive checksum, isolated single-file startup, first-run administrator, login, bundled assets and fingerprint bank, and settings/site/key/model persistence after restart.'
