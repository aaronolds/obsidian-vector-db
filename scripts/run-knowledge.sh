#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

for tool in dotnet docker; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "Required command missing: $tool" >&2
    exit 1
  }
done

docker compose version >/dev/null
docker info >/dev/null 2>&1 || {
  echo "Docker is not running. Start Docker Desktop and try again." >&2
  exit 1
}

command_name="${1:-index}"
if (( $# > 0 )); then
  shift
fi

# Paths supplied to this wrapper are relative to the repository root.
config="knowledge.local.yaml"
command_args=("$command_name")
while (( $# > 0 )); do
  case "$1" in
    --config)
      if (( $# < 2 )) || [[ -z "$2" ]]; then
        echo "--config requires a file path." >&2
        exit 2
      fi
      config="$2"
      shift 2
      ;;
    *)
      command_args+=("$1")
      shift
      ;;
  esac
done

[[ -f "$config" ]] || {
  echo "Configuration file not found: $config" >&2
  exit 2
}

dotnet restore Knowledge.sln
dotnet build Knowledge.sln --no-restore

docker compose -f docker/docker-compose.yml up -d qdrant

cli=(
  dotnet run
  --project src/Knowledge.Indexer/Knowledge.Indexer.csproj
  --no-build --no-restore --
)

echo "Waiting for Qdrant..."
ready=false
for (( attempt=1; attempt<=12; attempt++ )); do
  status_code=0
  status_output="$("${cli[@]}" status --config "$config")" \
    || status_code=$?

  if (( status_code == 2 )); then
    exit 2
  elif (( status_code != 0 && status_code != 1 )); then
    exit "$status_code"
  fi

  # A missing collection is expected before the first index run.
  if [[ "$status_output" == *"Qdrant reachable: true"* ]]; then
    ready=true
    break
  fi

  sleep 2
done

if [[ "$ready" != true ]]; then
  echo "Qdrant did not become reachable. Check Docker logs and configuration." >&2
  exit 1
fi

exec "${cli[@]}" "${command_args[@]}" --config "$config"
