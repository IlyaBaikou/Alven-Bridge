#!/usr/bin/env sh
set -eu

repository="${ALVEN_BRIDGE_SOURCE_URL:-https://raw.githubusercontent.com/IlyaBaikou/Alven-Bridge}"
version="${ALVEN_BRIDGE_VERSION:-main}"
install_directory="${ALVEN_BRIDGE_INSTALL_DIR:-${PWD}/alven-bridge}"

say() { printf '%s\n' "$*"; }
fail() { printf 'Alven Bridge: %s\n' "$*" >&2; exit 1; }

command -v docker >/dev/null 2>&1 || fail "Docker is required. Install Docker Desktop or Docker Engine first."
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is required."
command -v curl >/dev/null 2>&1 || fail "curl is required to download the release files."

if [ -d "$install_directory" ] && [ -n "$(find "$install_directory" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ] \
  && [ ! -f "$install_directory/compose.yaml" ]; then
  fail "${install_directory} is not empty. Choose ALVEN_BRIDGE_INSTALL_DIR or move those files."
fi

mkdir -p "$install_directory/family-files"
say "Installing Alven Bridge in ${install_directory}…"
curl --fail --silent --show-error --location \
  "${repository}/${version}/compose.yaml" --output "${install_directory}/compose.yaml"
curl --fail --silent --show-error --location \
  "${repository}/${version}/.env.example" --output "${install_directory}/.env.example"
curl --fail --silent --show-error --location \
  "${repository}/${version}/scripts/alven-bridge.sh" --output "${install_directory}/alven-bridge"
chmod 700 "${install_directory}/alven-bridge"

if [ ! -f "$install_directory/.env" ]; then
  cp "$install_directory/.env.example" "$install_directory/.env"
  chmod 600 "$install_directory/.env"
fi

(
  cd "$install_directory"
  if [ "${ALVEN_BRIDGE_SKIP_PULL:-false}" != "true" ]; then docker compose pull; fi
  docker compose up -d
)

say "Waiting for the local setup page…"
attempt=0
while [ "$attempt" -lt 30 ]; do
  if curl --fail --silent http://127.0.0.1:7433/health/live >/dev/null 2>&1; then
    say "Alven Bridge is running. Open http://127.0.0.1:7433"
    say "Later, run ${install_directory}/alven-bridge doctor to check it."
    if [ "${ALVEN_BRIDGE_SKIP_OPEN:-false}" != "true" ]; then
      if command -v open >/dev/null 2>&1; then open http://127.0.0.1:7433 >/dev/null 2>&1 || true
      elif command -v xdg-open >/dev/null 2>&1; then xdg-open http://127.0.0.1:7433 >/dev/null 2>&1 || true
      fi
    fi
    exit 0
  fi
  attempt=$((attempt + 1))
  sleep 1
done

(
  cd "$install_directory"
  docker compose ps
  docker compose logs --tail 30 bridge
)
fail "The container started but the local setup page did not become healthy. The status above is safe to share with support."
