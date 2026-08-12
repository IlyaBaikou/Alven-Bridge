param(
    [Parameter(Position=0)][string]$Command = "help",
    [Parameter(Position=1)][string]$Option = ""
)
$ErrorActionPreference = "Stop"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $scriptDirectory
try {
    switch ($Command) {
        "open" { Start-Process "http://127.0.0.1:7433" }
        "status" { docker compose ps bridge }
        "logs" { docker compose logs --tail $(if ($Option) { $Option } else { "100" }) bridge }
        "start" { docker compose up -d }
        "stop" { docker compose stop bridge }
        "restart" { docker compose restart bridge }
        "update" { docker compose pull; docker compose up -d; Write-Host "Updated. Run doctor after the first heartbeat." }
        "backup" {
            $backupDirectory = if ($Option) { $Option } else { Join-Path $scriptDirectory "backups" }
            New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
            $backupName = "alven-bridge-state-$((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')).tgz"
            docker compose stop bridge
            try {
                docker compose run --rm --no-deps -v "${backupDirectory}:/backup" --entrypoint sh bridge -c "tar -czf /backup/$backupName -C /var/lib/alven-bridge ."
            } finally {
                docker compose start bridge
            }
            Write-Host "Created $(Join-Path $backupDirectory $backupName)"
            Write-Host "This archive contains the installation credential. Store it privately."
        }
        "doctor" {
            docker compose version | Out-Null
            docker compose config --quiet
            Write-Host "OK: Docker and configuration"
            docker compose ps bridge
            Invoke-WebRequest "http://127.0.0.1:7433/health/live" -UseBasicParsing | Out-Null
            Write-Host "OK: Bridge process is healthy"
            try {
                Invoke-WebRequest "http://127.0.0.1:7433/health/ready" -UseBasicParsing | Out-Null
                Write-Host "OK: Pairing, Alven contact, and enabled capabilities are ready"
            } catch {
                Write-Host "ATTENTION: Setup is incomplete or a capability needs attention. Open the local page."
            }
        }
        "uninstall" {
            if ($Option -eq "--purge-local-state") {
                $confirmation = Read-Host "Type PURGE to remove the local pairing credential and receipts"
                if ($confirmation -ne "PURGE") { throw "Cancelled." }
                docker compose down --volumes
                Write-Host "Container and Bridge state removed. Family files were kept."
            } else {
                docker compose down
                Write-Host "Container removed. Bridge state and family files were kept."
            }
        }
        default {
            Write-Host "Usage: .\alven-bridge.ps1 <open|doctor|status|logs|start|stop|restart|update|backup|uninstall>"
            Write-Host "Use uninstall --purge-local-state only after revoking Bridge in Alven."
        }
    }
} finally {
    Pop-Location
}
