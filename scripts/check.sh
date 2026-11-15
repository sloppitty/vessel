#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

no_db=false
case "${1:-}" in
  --no-db) no_db=true; shift ;;
  --help|-h)
    echo "Usage: scripts/check.sh [--no-db]"
    echo "Requires disposable Vessel_TEST_POSTGRES and Node with full en-GB locale data."
    exit 0 ;;
esac
if (( $# )); then
  echo "Usage: scripts/check.sh [--no-db]" >&2
  exit 2
fi

if $no_db; then
  unset Vessel_TEST_POSTGRES
elif [[ -z "${Vessel_TEST_POSTGRES:-}" || "${Vessel_TEST_POSTGRES}" != *[![:space:]]* ]]; then
  echo 'Vessel_TEST_POSTGRES is required. Start throwaway PostgreSQL 18 with:' >&2
  echo '  bash scripts/test-db.sh' >&2
  echo 'Run its printed export, then pnpm check. Use pnpm check --no-db to skip database tests on purpose.' >&2
  exit 1
fi

if ! node -e "
  const value = new Intl.DateTimeFormat('en-GB', {
    day: 'numeric', month: 'short', timeZone: 'UTC'
  }).format(new Date('2026-10-15T12:00:00Z'));
  if (value !== '15 Oct') process.exit(1);
"; then
  echo "Node must support full-ICU en-GB dates: expected '15 Oct'. Use a full-ICU Node build such as node:24-bookworm-slim." >&2
  exit 1
fi

python3 -m unittest discover -s scripts -p 'test_*.py'
pnpm test:prototype
dotnet restore backend/Vessel.slnx --locked-mode
dotnet build backend/Vessel.slnx --no-restore --configuration Release
results=$(mktemp -d)
trap 'rm -rf "$results"' EXIT
backend_status=0
dotnet test backend/Vessel.slnx --no-build --configuration Release \
  --logger trx --results-directory "$results" || backend_status=$?
skipped=$(python3 - "$results" <<'PY'
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

reports = list(Path(sys.argv[1]).glob("*.trx"))
if not reports:
    sys.exit("Cannot count backend skips: no TRX reports were produced.")
skipped = 0
for report in reports:
    root = ET.parse(report)
    counters = root.find(".//{*}ResultSummary/{*}Counters")
    if counters is None:
        sys.exit("Cannot count backend skips: a TRX report has no counters.")
    skipped += sum(result.get("outcome") == "NotExecuted"
                   for result in root.findall("./{*}Results/{*}UnitTestResult"))
print(skipped)
PY
)
summary="Backend tests skipped: $skipped (see backend test output for reasons)."
if $no_db; then
  summary="Backend tests skipped: $skipped (--no-db disables PostgreSQL tests; other opt-in tests may also skip)."
fi
echo "$summary"
if (( backend_status )); then
  exit "$backend_status"
fi
pnpm install --frozen-lockfile
pnpm --filter vessel-frontend typecheck
pnpm --filter vessel-frontend lint
pnpm --filter vessel-frontend test
pnpm --filter vessel-frontend build
echo "$summary"
