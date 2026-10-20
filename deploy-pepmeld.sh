#!/usr/bin/env bash
#
# Deploy the PepMeld API to a host that builds it itself, beside the sanctions API.
#
#   ./deploy-pepmeld.sh                 deploy HEAD
#   ./deploy-pepmeld.sh rollback        put the previous image back
#
# The same shape as deploy.sh and the same rules: the commit is deployed, not the working
# tree; the host needs Docker and nothing else; secrets live in docker-compose.yml on
# the host, which this script never touches. Its own directory, image and port, so a
# PepMeld deploy cannot touch the sanctions containers, and vice versa.
set -euo pipefail

cd "$(dirname "$0")"

if [ -f deploy.env ]; then
    # shellcheck disable=SC1091
    . ./deploy.env
fi

: "${DEPLOY_HOST:?set DEPLOY_HOST (user@host) in deploy.env or the environment}"

host="$DEPLOY_HOST"
dir="${PEPMELD_DEPLOY_DIR:-pepmeld}"          # ~/pepmeld on the host
image="${PEPMELD_DEPLOY_IMAGE:-pepmeld-api}"
port="${PEPMELD_DEPLOY_PORT:-5212}"           # the host port docker-compose.yml publishes 8080 on
public_url="${PEPMELD_DEPLOY_URL:-}"          # optional; /health is checked there when set

remote() { ssh -o BatchMode=no "$host" "$@"; }

health() {
    remote "for i in \$(seq 1 30); do
                body=\$(curl -s --max-time 5 http://127.0.0.1:$port/health || true)
                [ \"\$body\" = '{\"status\":\"ok\"}' ] && exit 0
                sleep 2
            done
            exit 1"
}

if [ "${1:-}" = "rollback" ]; then
    echo "Rolling $image back to the previous image on $host"
    remote "docker image inspect $image:previous >/dev/null 2>&1 || { echo 'No previous image to roll back to.'; exit 1; }
            docker tag $image:previous $image:latest
            cd ~/$dir && docker compose up -d"
    health && echo "Rolled back: $(remote "docker image inspect $image:latest --format '{{index .Config.Labels \"org.opencontainers.image.revision\"}}'")"
    exit
fi

sha="$(git rev-parse HEAD)"
short="${sha:0:7}"

if ! git diff-index --quiet HEAD --; then
    echo "Working tree has uncommitted changes. They will NOT be deployed; HEAD ($short) will." >&2
fi

echo "Deploying PepMeld $short to $host:~/$dir"

git archive --format=tar HEAD | remote "rm -rf ~/$dir/app && mkdir -p ~/$dir/app && tar -x -C ~/$dir/app"

remote "if docker image inspect $image:latest >/dev/null 2>&1; then docker tag $image:latest $image:previous; fi
        docker build -q -f ~/$dir/app/Dockerfile.pepmeld -t $image:latest -t $image:$short --label org.opencontainers.image.revision=$sha ~/$dir/app"

remote "cd ~/$dir && docker compose up -d"

if ! health; then
    echo >&2
    echo "Health check failed. Last 50 lines of the container:" >&2
    remote "docker logs --tail 50 $image" >&2 || true
    echo >&2
    echo "Roll back with: ./deploy-pepmeld.sh rollback" >&2
    exit 1
fi

if [ -n "$public_url" ]; then
    public="$(curl -s --max-time 20 "${public_url%/}/health" || true)"
    if [ "$public" != '{"status":"ok"}' ]; then
        echo "Container is healthy on the host but $public_url/health answered: ${public:-nothing}. Apache, not the API." >&2
        exit 1
    fi
fi

echo "Deployed PepMeld $short"
