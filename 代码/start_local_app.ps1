param([switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$python = Join-Path $projectDir '.venv\Scripts\python.exe'
$url = 'http://127.0.0.1:5000/login'
$listener = Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue
if ($listener) {
    $page = Invoke-WebRequest -Uri $url -TimeoutSec 3 -UseBasicParsing
    if ($page.Content -notmatch 'login-form') {
        throw 'Port 5000 is used by another application.'
    }
    Write-Host "Chat server already running: $url"
    if (-not $NoBrowser) { Start-Process $url }
    exit 0
}
if (-not (Test-Path -LiteralPath $python)) { throw 'Project Python environment is missing.' }
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUNBUFFERED = '1'
$env:CHAPTER2_HOST = '127.0.0.1'
$env:CHAPTER2_PORT = '5000'
$logDir = Join-Path $projectDir 'model_output\server_logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outLog = Join-Path $logDir "$stamp.stdout.log"
$errLog = Join-Path $logDir "$stamp.stderr.log"
$server = Start-Process -FilePath $python -ArgumentList '-u','run_local_app.py' `
    -WorkingDirectory $projectDir -WindowStyle Hidden `
    -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru
$server.Id | Set-Content -LiteralPath (Join-Path $logDir 'server.pid')
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    if ($server.HasExited) {
        Get-Content -LiteralPath $errLog -Tail 30
        throw 'Chat server exited during model loading.'
    }
    try {
        $page = Invoke-WebRequest -Uri $url -TimeoutSec 1 -UseBasicParsing
        if ($page.StatusCode -eq 200) {
            Write-Host "Local AI chat ready: $url"
            Write-Host "Logs: $logDir"
            if (-not $NoBrowser) { Start-Process $url }
            exit 0
        }
    } catch {}
    Start-Sleep -Seconds 1
}
throw "Server is still loading or failed. Inspect $errLog"
