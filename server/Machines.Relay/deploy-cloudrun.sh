#!/usr/bin/env bash
# Deploys the Machines relay to Google Cloud Run: has Cloud Build build its container into Artifact Registry and
# runs it as the service machines-relay in Frankfurt (europe-west3): one instance at most, asleep while no match runs,
# WebSockets held up to the 60 minutes Cloud Run allows (the game moves to a new socket before that).
#
#   server/Machines.Relay/deploy-cloudrun.sh <project-id> [--with-secrets] [--local-docker]
#
# Needs gcloud signed in (gcloud auth login) to a project with billing; see docs/DEVELOPMENT.md, "The relay online". The image
# is built from the committed state (HEAD) of the projects the relay is made of. --with-secrets also hands the service
# the relay's admin secret and token key from Secret Manager (machines-relay-admin, machines-relay-token-key). With
# --local-docker the image is built and pushed by Docker on this computer instead of Cloud Build.
set -euo pipefail

if [[ $# -lt 1 || "$1" == -* ]]; then
  sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'
  exit 1
fi
PROJECT="$1"
shift
SECRETS=0
LOCAL_DOCKER=0
for arg in "$@"; do
  case "$arg" in
    --with-secrets) SECRETS=1 ;;
    --local-docker) LOCAL_DOCKER=1 ;;
    *) echo "unknown option: $arg" >&2; exit 1 ;;
  esac
done

# In Git Bash on Windows gcloud finds no Python of its own; the one that comes with the Cloud SDK does.
if [[ -z "${CLOUDSDK_PYTHON:-}" ]] && command -v gcloud >/dev/null; then
  BUNDLED="$(dirname "$(command -v gcloud)")/../platform/bundledpython/python.exe"
  [[ -x "$BUNDLED" ]] && export CLOUDSDK_PYTHON="$BUNDLED"
fi

REGION=europe-west3
SERVICE=machines-relay
REPOSITORY=machines
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TAG="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || date +%Y%m%d%H%M%S)"
IMAGE="$REGION-docker.pkg.dev/$PROJECT/$REPOSITORY/$SERVICE:$TAG"

echo "== Services the relay needs"
gcloud services enable run.googleapis.com artifactregistry.googleapis.com secretmanager.googleapis.com   cloudbuild.googleapis.com --project "$PROJECT"

echo "== Artifact Registry repository $REPOSITORY in $REGION"
if ! gcloud artifacts repositories describe "$REPOSITORY" --location "$REGION" --project "$PROJECT" >/dev/null 2>&1; then
  gcloud artifacts repositories create "$REPOSITORY" --repository-format docker --location "$REGION" --project "$PROJECT" \
    --description "Machines Remake containers"
fi
echo "== Image $IMAGE"
PARTS=(Directory.Build.props import/Machines.Import sim/Machines.Sim net/Machines.Net server/Machines.Relay)
if [[ -n "$(git -C "$ROOT" status --porcelain -- "${PARTS[@]}")" ]]; then
  echo "note: uncommitted changes in the relay's projects are not in the image (it is built from HEAD)" >&2
fi
if [[ $LOCAL_DOCKER == 1 ]]; then
  gcloud auth configure-docker "$REGION-docker.pkg.dev" --quiet
  docker build -f "$ROOT/server/Machines.Relay/Dockerfile" -t "$IMAGE" "$ROOT"
  docker push "$IMAGE"
else
  # Only the relay's projects go up (a few hundred KB, not the repository with its game files and models), as they
  # stand in HEAD, with the Dockerfile at the root of that context.
  CONTEXT="$(mktemp -d)"
  trap 'rm -rf "$CONTEXT"' EXIT
  git -C "$ROOT" archive --format=tar HEAD "${PARTS[@]}" | tar -x -C "$CONTEXT"
  cp "$CONTEXT/server/Machines.Relay/Dockerfile" "$CONTEXT/Dockerfile"
  gcloud builds submit "$CONTEXT" --tag "$IMAGE" --project "$PROJECT"
fi

# Placeholders for later phases: the admin secret the matchmaker opens rooms with and the key join tokens are signed
# with live in Secret Manager, never in the game or this script.
SECRET_FLAGS=()
if [[ $SECRETS == 1 ]]; then
  SECRET_FLAGS=(--set-secrets "RELAY_ADMIN_SECRET=machines-relay-admin:latest,RELAY_TOKEN_KEY=machines-relay-token-key:latest")
fi

echo "== Cloud Run service $SERVICE"
# 1 GiB: the relay holds every running match's log of bundles (compressed, shared by every rebuild: some 40 MB for the
# largest match) and what each connection has not had acknowledged yet (P2 design §2.10).
gcloud run deploy "$SERVICE" \
  --project "$PROJECT" \
  --region "$REGION" \
  --image "$IMAGE" \
  --port 8080 \
  --max-instances 1 \
  --min-instances 0 \
  --timeout 3600 \
  --session-affinity \
  --concurrency 1000 \
  --cpu 1 \
  --memory 1Gi \
  --allow-unauthenticated \
  ${SECRET_FLAGS[@]+"${SECRET_FLAGS[@]}"}

URL="$(gcloud run services describe "$SERVICE" --project "$PROJECT" --region "$REGION" --format 'value(status.url)')"
echo
echo "The relay runs at $URL"
echo "  Is it up:      curl $URL/health"
echo "  In the game:   ${URL/https:/wss:}"
echo "  (type it under Multiplayer, or set MACHINES_RELAY, multiplayer/public_relay in settings.cfg or NetLink.PublicRelay)"
