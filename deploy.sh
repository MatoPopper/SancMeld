#!/usr/bin/env bash
#
# Deploy the API to a host that builds it itself.
#
#   ./deploy.sh                 deploy HEAD
#   ./deploy.sh rollback        put the previous image back
#
# What is deployed is the commit, not the working tree — the same as `git archive HEAD`.
# Anything not committed does not ship, which is the point: the host runs something that
# can be named.
#
# The host needs Docker and nothing else; the multi-stage Dockerfile builds there. There
# is no registry in between because there is one host, and a registry would be a second
# thing to keep secrets for. Secrets never pass through here at all: the connection string
# and the Google client id live in docker-compose.yml on the host, which this script does
# not touch.
#
# Migrations apply on start (SancMeld__Database__AutoMigrate=true on the host). EF Core
# runs each migration in a transaction on PostgreSQL, so a migration that fails leaves the
# schema as it was, and the rollback below then restores the previous image against it.
set -euo pipefail

cd "$(dirname "$0")"

# Where to, from deploy.env next to this script (git-ignored) or the environment. The
# script carries no host of its own: this repository is public, and an SSH user name next
# to a host name is half of a login on a box that accepts passwords. deploy.env.example
# lists the variables.
if [ -f deploy.env ]; then
    # shellcheck disable=SC1091
    . ./deploy.env
fi

: "${DEPLOY_HOST:?set DEPLOY_HOST (user@host) in deploy.env or the environment}"
: "${DEPLOY_URL:?set DEPLOY_URL (the public base URL) in deploy.env or the environment}"

host="$DEPLOY_HOST"
dir="${DEPLOY_DIR:-sancmeld}"            # relative to the remote home: ~/sancmeld
image="${DEPLOY_IMAGE:-sancmeld-api}"
port="${DEPLOY_PORT:-5210}"              # the host port in docker-compose.yml
public_url="${DEPLOY_URL%/}"

remote() { ssh -o BatchMode=no "$host" "$@"; }

health() {
    # Through the host's own port first, so a failure here means the container and not
    # Apache; the public URL afterwards, so the whole path is confirmed.
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

if upstream="$(git rev-parse --abbrev-ref '@{upstream}' 2>/dev/null)" && [ "$(git rev-parse "$upstream")" != "$sha" ]; then
    echo "HEAD ($short) is not what $upstream has. Deploying it anyway; push it so the host runs a commit that exists somewhere else too." >&2
fi

echo "Deploying $short to $host:~/$dir"

# 1. Sources. The directory is replaced, not merged, so a file deleted in git is deleted
#    on the host too.
git archive --format=tar HEAD | remote "rm -rf ~/$dir/app && mkdir -p ~/$dir/app && tar -x -C ~/$dir/app"

# 2. Build. The image that is about to be replaced is kept as :previous for the rollback,
#    and the commit goes on the new one as a label so `docker image inspect` can answer
#    what is actually running.
remote "if docker image inspect $image:latest >/dev/null 2>&1; then docker tag $image:latest $image:previous; fi
        docker build -q -t $image:latest -t $image:$short --label org.opencontainers.image.revision=$sha ~/$dir/app"

# 3. Start. compose only recreates the container when its image changed, which it did.
remote "cd ~/$dir && docker compose up -d"

# 4. Prove it. On failure the logs are what is needed, and the rollback is one command.
if ! health; then
    echo >&2
    echo "Health check failed. Last 50 lines of the container:" >&2
    remote "docker logs --tail 50 $image" >&2 || true
    echo >&2
    echo "Roll back with: ./deploy.sh rollback" >&2
    exit 1
fi

public="$(curl -s --max-time 20 "$public_url/health" || true)"
if [ "$public" != '{"status":"ok"}' ]; then
    echo "Container is healthy on the host but $public_url/health answered: ${public:-nothing}. Apache, not the API." >&2
    exit 1
fi

echo "Deployed $short: $public_url"
