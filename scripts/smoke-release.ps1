$ErrorActionPreference = 'Stop'
$app = (Resolve-Path (Join-Path $PSScriptRoot '../artifacts/native-app')).Path
$executable = Join-Path $app $(if ($IsWindows) { 'TrueModel.exe' } else { 'TrueModel' })
$data = Join-Path ([System.IO.Path]::GetTempPath()) ('truemodel-smoke-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $data | Out-Null
$env:DataDirectory = $data
$env:Desktop__OpenBrowser = 'false'
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
            if ($process.HasExited) { throw "Startup failed: $(Get-Content -LiteralPath $err -Raw)" }
            $text = Get-Content -LiteralPath $log -Raw -ErrorAction SilentlyContinue
            if ($text -match 'Now listening on: (http://127\.0\.0\.1:\d+)') { $url = $Matches[1]; break }
            Start-Sleep -Milliseconds 300
        }
        if (!$url) { throw 'No loopback listener found' }
        if ((Invoke-RestMethod "$url/health").status -ne 'ok') { throw 'Health check failed' }
        if ((Invoke-WebRequest $url).Content -notmatch 'TrueModel') { throw 'Static assets missing' }
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
        }
        if ((Invoke-RestMethod "$url/api/settings" -WebSession $session).challengeCount -ne 6) { throw 'Challenge count did not persist' }
        if (!(Test-Path (Join-Path $data 'truemodel.db'))) { throw 'SQLite missing' }
    } finally {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    }
}
Write-Output 'Verified standalone startup, first-run administrator, login, assets and settings persistence after restart.'
