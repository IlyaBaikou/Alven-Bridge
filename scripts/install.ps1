$ErrorActionPreference = "Stop"

$repository = if ($env:ALVEN_BRIDGE_SOURCE_URL) { $env:ALVEN_BRIDGE_SOURCE_URL } else { "https://raw.githubusercontent.com/IlyaBaikou/Alven-Bridge" }
$version = if ($env:ALVEN_BRIDGE_VERSION) { $env:ALVEN_BRIDGE_VERSION } else { "main" }
$installDirectory = if ($env:ALVEN_BRIDGE_INSTALL_DIR) { $env:ALVEN_BRIDGE_INSTALL_DIR } else { Join-Path (Get-Location) "alven-bridge" }

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker Desktop is required. Install it and start Docker before running this installer."
}
docker compose version | Out-Null

if ((Test-Path $installDirectory) -and (Get-ChildItem $installDirectory -Force -ErrorAction SilentlyContinue) -and -not (Test-Path (Join-Path $installDirectory "compose.yaml"))) {
    throw "$installDirectory is not empty. Set ALVEN_BRIDGE_INSTALL_DIR to another folder."
}

New-Item -ItemType Directory -Force -Path (Join-Path $installDirectory "family-files") | Out-Null
Write-Host "Installing Alven Bridge in $installDirectory..."
Invoke-WebRequest "$repository/$version/compose.yaml" -OutFile (Join-Path $installDirectory "compose.yaml")
Invoke-WebRequest "$repository/$version/.env.example" -OutFile (Join-Path $installDirectory ".env.example")
Invoke-WebRequest "$repository/$version/scripts/alven-bridge.ps1" -OutFile (Join-Path $installDirectory "alven-bridge.ps1")
if (-not (Test-Path (Join-Path $installDirectory ".env"))) {
    Copy-Item (Join-Path $installDirectory ".env.example") (Join-Path $installDirectory ".env")
}

Push-Location $installDirectory
try {
    if ($env:ALVEN_BRIDGE_SKIP_PULL -ne "true") { docker compose pull }
    docker compose up -d
} finally {
    Pop-Location
}

Write-Host "Waiting for the local setup page..."
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    try {
        Invoke-WebRequest "http://127.0.0.1:7433/health/live" -UseBasicParsing | Out-Null
        Write-Host "Alven Bridge is running. Opening http://127.0.0.1:7433"
        if ($env:ALVEN_BRIDGE_SKIP_OPEN -ne "true") { Start-Process "http://127.0.0.1:7433" }
        exit 0
    } catch {
        Start-Sleep -Seconds 1
    }
}
throw "The container started but the local setup page did not become healthy. Run .\alven-bridge.ps1 doctor."
