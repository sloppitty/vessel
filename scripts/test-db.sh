#!/usr/bin/env bash
set -euo pipefail

if (( $# )); then
  echo 'Usage: bash scripts/test-db.sh' >&2
  exit 2
fi

# Never load .env, use Compose, reuse a container, or accept a database target.
name="vessel-test-db-$(date +%s)-$$-$RANDOM"
network_created=false
container_created=false
keep=false
cleanup() {
  if ! $keep; then
    if $container_created; then docker rm --force --volumes "$name" >/dev/null || true; fi
    if $network_created; then docker network rm "$name" >/dev/null || true; fi
  fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

docker network create --driver bridge "$name" >/dev/null
network_created=true
docker run --detach --rm --name "$name" --network "$name" \
  --publish 127.0.0.1::5432 --tmpfs /var/lib/postgresql \
  --env POSTGRES_DB=vessel_tests --env POSTGRES_USER=vessel \
  --env POSTGRES_PASSWORD=vessel-tests-only postgres:18-alpine >/dev/null
container_created=true

ready=false
for (( attempt=0; attempt<60; attempt++ )); do
  if docker exec "$name" pg_isready -h 127.0.0.1 -U vessel -d vessel_tests >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 1
done
if ! $ready; then
  echo 'Disposable PostgreSQL did not become ready within 60 seconds.' >&2
  exit 1
fi
binding=$(docker port "$name" 5432/tcp)
if [[ ! "$binding" =~ ^127\.0\.0\.1:([0-9]+)$ ]]; then
  echo 'Disposable PostgreSQL has no loopback-only port binding.' >&2
  exit 1
fi
port=${BASH_REMATCH[1]}
echo "Disposable PostgreSQL 18 is ready. Remove it and its private network after testing:" >&2
echo "  docker rm --force --volumes $name && docker network rm $name" >&2
printf "export Vessel_TEST_POSTGRES='Host=127.0.0.1;Port=%s;Database=vessel_tests;Username=vessel;Password=vessel-tests-only'\n" "$port"
keep=true
