#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

IMAGE="${IMAGE:-lampac:dev}"
CONTAINER="${CONTAINER:-lampac-dev}"
PORT="${PORT:-9118}"
HOST_DIR="${HOST_DIR:-$ROOT/lampac-docker}"
# Use repo-level config/init.conf directly (user already has it set up).
# Override with CONFIG_FILE=... if needed.
CONFIG_FILE="${CONFIG_FILE:-$ROOT/config/init.conf}"
PASSWD_FILE="${PASSWD_FILE:-$ROOT/config/passwd}"
PLUGINS_DIR="${PLUGINS_DIR:-$HOST_DIR/plugins}"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; NC='\033[0m'
log()  { echo -e "${BLUE}[•]${NC} $*"; }
ok()   { echo -e "${GREEN}[✓]${NC} $*"; }
warn() { echo -e "${YELLOW}[!]${NC} $*"; }
die()  { echo -e "${RED}[✗]${NC} $*" >&2; exit 1; }

usage() {
  cat <<EOF
Usage: $(basename "$0") <command>

Commands:
  build           Build local docker image '$IMAGE' from current source
  up              Start container '$CONTAINER' on port $PORT
  down            Stop and remove container
  restart         build + down + up (full rebuild & restart)
  reup            down + up (no rebuild)
  logs            Tail container logs
  shell           Open shell inside the running container
  status          Show container status + url
  init-config     Create lampac-docker/{config,plugins} skeleton with bestBalanser enabled

Env overrides:
  PORT=$PORT          Host port (and listen.port inside container)
  IMAGE=$IMAGE        Image tag
  CONTAINER=$CONTAINER Container name
  HOST_DIR=$HOST_DIR  Host volume root

Examples:
  ./dev-docker.sh init-config        # one-time skeleton
  ./dev-docker.sh build              # ~3-5 min (cached after first time)
  ./dev-docker.sh up                 # http://localhost:$PORT
  ./dev-docker.sh restart            # after editing source
  PORT=29118 ./dev-docker.sh up      # second instance on a different port
EOF
}

ensure_docker() {
  command -v docker >/dev/null 2>&1 || die "docker is not installed or not in PATH"
}

cmd_init_config() {
  mkdir -p "$PLUGINS_DIR"

  [[ -f "$CONFIG_FILE" ]] || die "$CONFIG_FILE not found. Create it (e.g. from config/example.init.conf) then re-run."

  if [[ ! -f "$PASSWD_FILE" ]]; then
    log "Creating $PASSWD_FILE (random root password)"
    head -c 16 /dev/urandom | base64 | tr -d '=+/' | head -c 16 > "$PASSWD_FILE"
    ok "Root password written to $PASSWD_FILE"
  else
    ok "$PASSWD_FILE already exists"
  fi

  # Ensure bestBalanser block exists in existing init.conf (idempotent).
  if command -v python3 >/dev/null 2>&1; then
    python3 - "$CONFIG_FILE" <<'PY'
import json, sys, pathlib
p = pathlib.Path(sys.argv[1])
raw = p.read_text()
try:
    data = json.loads(raw)
except Exception as e:
    print(f"[python] cannot parse {p}: {e}")
    sys.exit(0)

online = data.setdefault("online", {})
if "bestBalanser" in online:
    print(f"[python] bestBalanser already present in {p} — not touching")
else:
    online["bestBalanser"] = {
        "enable": True,
        "hideBroken": True,
        "totalTimeoutMs": 7000,
        "perProbeTimeoutMs": 5000,
        "successCacheMinutes": 30,
        "failureCacheMinutes": 3
    }
    p.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n")
    print(f"[python] bestBalanser added to {p}")
PY
  else
    warn "python3 not found — add to your $CONFIG_FILE manually:"
    warn '  "online": { ..., "bestBalanser": { "enable": true } }'
  fi

  if [[ ! -f "$PLUGINS_DIR/lampainit.js" ]]; then
    if [[ -f "$ROOT/Modules/LampaWeb/plugins/lampainit.js" ]]; then
      cp "$ROOT/Modules/LampaWeb/plugins/lampainit.js" "$PLUGINS_DIR/lampainit.js"
      ok "lampainit.js copied from Modules/LampaWeb"
    else
      touch "$PLUGINS_DIR/lampainit.js"
      ok "Empty $PLUGINS_DIR/lampainit.js created"
    fi
  fi

  ok "Done. Now run: ./dev-docker.sh build && ./dev-docker.sh up"
}

cmd_build() {
  ensure_docker
  log "Building $IMAGE for $(uname -m) (this may take a few minutes the first time)"
  # Use BuildKit, no cache busting beyond layer cache. Local single-platform build.
  DOCKER_BUILDKIT=1 docker build \
    -f "$ROOT/Dockerfile" \
    -t "$IMAGE" \
    "$ROOT"
  ok "Built $IMAGE"
}

cmd_down() {
  ensure_docker
  if docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER"; then
    log "Removing container $CONTAINER"
    docker rm -f "$CONTAINER" >/dev/null
    ok "Removed $CONTAINER"
  else
    ok "No container $CONTAINER to remove"
  fi
}

cmd_up() {
  ensure_docker

  if ! docker image inspect "$IMAGE" >/dev/null 2>&1; then
    die "Image $IMAGE not found. Run: ./dev-docker.sh build"
  fi

  [[ -f "$CONFIG_FILE" ]] || die "$CONFIG_FILE not found. Run: ./dev-docker.sh init-config"

  if docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
    warn "Container $CONTAINER already running. Use 'restart' or 'reup'."
    cmd_status
    return 0
  fi

  cmd_down

  log "Starting $CONTAINER on http://localhost:$PORT"

  local mounts=()
  mounts+=("-v" "$CONFIG_FILE:/lampac/init.conf")
  [[ -f "$PASSWD_FILE" ]] && mounts+=("-v" "$PASSWD_FILE:/lampac/passwd")
  [[ -f "$PLUGINS_DIR/lampainit.js" ]] && mounts+=("-v" "$PLUGINS_DIR/lampainit.js:/lampac/plugins/override/lampainit.js")

  # Optional cache & db persistence (so probe results & module state survive restarts)
  mkdir -p "$HOST_DIR/cache" "$HOST_DIR/database"
  mounts+=("-v" "$HOST_DIR/cache:/lampac/cache")
  mounts+=("-v" "$HOST_DIR/database:/lampac/database")

  docker run -d \
    --name "$CONTAINER" \
    --restart unless-stopped \
    -p "$PORT:$PORT" \
    --shm-size=1g \
    "${mounts[@]}" \
    "$IMAGE" >/dev/null

  ok "Started"
  cmd_status
}

cmd_reup() {
  cmd_down
  cmd_up
}

cmd_restart() {
  cmd_build
  cmd_down
  cmd_up
}

cmd_logs() {
  ensure_docker
  local tail_arg="${1:-200}"
  if [[ "${2:-}" == "follow" || "${2:-}" == "-f" ]]; then
    docker logs -f --tail="$tail_arg" "$CONTAINER"
  else
    docker logs --tail="$tail_arg" "$CONTAINER"
  fi
}

cmd_test() {
  ensure_docker
  log "Sending test request..."
  curl -s -o /dev/null -w "HTTP %{http_code} in %{time_total}s\n" \
    "http://localhost:$PORT/lite/events?id=tt0111161&imdb_id=tt0111161&serial=0"
  echo ""
  log "Last 50 log lines:"
  docker logs --tail=50 "$CONTAINER" | grep -E "best-balanser|/lite/events" || warn "no matching log lines"
}

cmd_shell() {
  ensure_docker
  docker exec -it "$CONTAINER" bash || docker exec -it "$CONTAINER" sh
}

cmd_status() {
  ensure_docker
  if docker ps --format '{{.Names}}\t{{.Status}}\t{{.Ports}}' | grep -P "^$CONTAINER\t"; then
    ok "Open: http://localhost:$PORT"
    ok "Test: curl 'http://localhost:$PORT/lite/events?id=tt0111161&imdb_id=tt0111161&serial=0&best=1' | jq ."
  else
    warn "Container $CONTAINER not running"
  fi
}

case "${1:-}" in
  build)        cmd_build ;;
  up)           cmd_up ;;
  down)         cmd_down ;;
  reup)         cmd_reup ;;
  restart)      cmd_restart ;;
  logs)         cmd_logs ;;
  shell)        cmd_shell ;;
  status)       cmd_status ;;
  init-config)  cmd_init_config ;;
  ""|-h|--help) usage ;;
  *)            usage; die "Unknown command: $1" ;;
esac
