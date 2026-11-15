"""Exercise operator entry points without a real SDK, database or Docker daemon."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent.parent
STUB = r"""
import json
import os
from pathlib import Path
import sys

tool, args = Path(sys.argv[0]).name, sys.argv[1:]
with open(os.environ["TOOL_LOG"], "a") as log:
    log.write(json.dumps([tool, args, os.environ.get("Vessel_TEST_POSTGRES")]) + "\n")
if tool == "python3" and args[:1] != ["-m"]:
    os.execv(os.environ["REAL_PYTHON"], [os.environ["REAL_PYTHON"], *args])
if tool == "node":
    sys.exit(int(os.environ.get("NODE_EXIT", "0")))
if tool == "dotnet" and args[0] == "test":
    destination = Path(args[args.index("--results-directory") + 1])
    for index, skipped in enumerate(json.loads(os.environ.get("SKIPS", "[3, 12]"))):
        # xUnit writes skipped outcomes while the TRX summary's notExecuted stays zero.
        results = '<UnitTestResult outcome="Passed" />' + \
            '<UnitTestResult outcome="NotExecuted" />' * skipped
        (destination / f"{index}.trx").write_text(
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            f'<Results>{results}</Results><ResultSummary>'
            f'<Counters total="{skipped + 1}" executed="1" notExecuted="0" /></ResultSummary></TestRun>')
    print("Intentionally irrelevant console summary: Skipped: 9999")
    sys.exit(int(os.environ.get("BACKEND_EXIT", "0")))
if tool == "pnpm":
    if args == ["--filter", "vessel-frontend", "build"]:
        sys.exit(int(os.environ.get("FRONTEND_EXIT", "0")))
if tool == "docker":
    stage = " ".join(args[:2]) if args[0] == "network" else args[0]
    if stage == os.environ.get("DOCKER_FAIL"):
        sys.exit(1)
    if stage == "port":
        print(os.environ.get("DOCKER_BINDING", "127.0.0.1:55432"))
"""


class ScriptTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / "scripts").mkdir()
        (self.root / "bin").mkdir()
        for name in ("check.sh", "test-db.sh"):
            shutil.copy(ROOT / "scripts" / name, self.root / "scripts" / name)
        self.log = self.root / "tools.jsonl"
        # Only a fixture. Scripts must not read it or inherit a development target.
        (self.root / ".env").write_text("ConnectionStrings__Vessel=must-not-be-used\n")
        self.env = {
            "PATH": f"{self.root / 'bin'}:{os.defpath}",
            "TOOL_LOG": str(self.log),
            "REAL_PYTHON": sys.executable,
        }
        for name in ("python3", "node", "dotnet", "pnpm", "docker", "sleep"):
            path = self.root / "bin" / name
            path.write_text(f"#!{sys.executable}\n{STUB}")
            path.chmod(0o755)

    def run_script(self, name, *args, timeout=20, **env):
        return subprocess.run(
            ["bash", str(self.root / "scripts" / name), *args],
            env={**self.env, **env}, text=True, capture_output=True, timeout=timeout)

    def calls(self, tool=None):
        calls = [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []
        return [call for call in calls if tool is None or call[0] == tool]


class CheckTests(ScriptTests):
    def test_missing_or_blank_database_fails_before_any_tool_runs(self):
        for env in ({}, {"Vessel_TEST_POSTGRES": ""}, {"Vessel_TEST_POSTGRES": " \t\n"}):
            with self.subTest(env=env):
                result = self.run_script("check.sh", **env)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("PostgreSQL 18", result.stderr)
                self.assertIn("scripts/test-db.sh", result.stderr)
                self.assertIn("--no-db", result.stderr)
                self.assertEqual([], self.calls())

    def test_unknown_option_does_not_run_tools(self):
        result = self.run_script("check.sh", "--skip-db")
        self.assertEqual(2, result.returncode)
        self.assertEqual([], self.calls())

    def test_small_icu_fails_before_tests_restore_or_build(self):
        result = self.run_script("check.sh", Vessel_TEST_POSTGRES="disposable", NODE_EXIT="1")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("full-ICU", result.stderr)
        self.assertIn("15 Oct", result.stderr)
        self.assertEqual(["node"], [call[0] for call in self.calls()])
        self.assertIn("en-GB", self.calls("node")[0][1][1])

    def test_database_run_sums_fresh_trx_and_reports_after_backend_and_at_end(self):
        result = self.run_script("check.sh", Vessel_TEST_POSTGRES="disposable")
        self.assertEqual(0, result.returncode, result.stderr)
        summary = "Backend tests skipped: 15 (see backend test output for reasons)."
        self.assertEqual(2, result.stdout.count(summary))
        self.assertEqual(summary, result.stdout.splitlines()[-1])
        dotnet = self.calls("dotnet")
        self.assertEqual(["restore", "build", "test"], [call[1][0] for call in dotnet])
        self.assertIn("--locked-mode", dotnet[0][1])
        self.assertIn("--no-restore", dotnet[1][1])
        self.assertIn("--no-build", dotnet[2][1])
        self.assertIn("trx", dotnet[2][1])
        self.assertTrue(all(call[2] == "disposable" for call in self.calls()))
        results = Path(dotnet[2][1][-1])
        self.assertFalse(results.exists(), "TRX directory should be cleaned up")
        self.assertEqual(["--filter", "vessel-frontend", "build"], self.calls("pnpm")[-1][1])

    def test_no_db_clears_even_a_supplied_target_and_reports_actual_skips(self):
        result = self.run_script("check.sh", "--no-db", Vessel_TEST_POSTGRES="must-not-be-used",
                                 SKIPS="[800, 39]")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(
            "Backend tests skipped: 839 (--no-db disables PostgreSQL tests; other opt-in tests may also skip).",
            result.stdout.splitlines()[-1])
        self.assertTrue(all(call[2] is None for call in self.calls()))

    def test_backend_failure_still_reports_skips_and_stops_frontend(self):
        result = self.run_script("check.sh", Vessel_TEST_POSTGRES="invalid-target", BACKEND_EXIT="7")
        self.assertEqual(7, result.returncode)
        self.assertIn("Backend tests skipped: 15", result.stdout)
        self.assertEqual([["pnpm", ["test:prototype"], "invalid-target"]], self.calls("pnpm"))

    def test_missing_reports_fail_rather_than_claim_zero_skips(self):
        result = self.run_script("check.sh", "--no-db", SKIPS="[]")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("no TRX reports", result.stderr)
        self.assertNotIn("Backend tests skipped:", result.stdout)

    def test_zero_skips_is_reported_and_frontend_failure_is_not_hidden(self):
        result = self.run_script("check.sh", Vessel_TEST_POSTGRES="disposable",
                                 SKIPS="[0, 0]", FRONTEND_EXIT="9")
        self.assertEqual(9, result.returncode)
        self.assertEqual(1, result.stdout.count("Backend tests skipped: 0"))


class TestDatabaseTests(ScriptTests):
    def test_helper_uses_only_its_private_bridge_loopback_and_tmpfs(self):
        result = self.run_script("test-db.sh", Vessel_TEST_POSTGRES="must-not-be-used",
                                 ConnectionStrings__Vessel="must-not-be-used", POSTGRES_DB="development")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(
            "export Vessel_TEST_POSTGRES='Host=127.0.0.1;Port=55432;Database=vessel_tests;"
            "Username=vessel;Password=vessel-tests-only'\n", result.stdout)
        calls = [call[1] for call in self.calls("docker")]
        network = calls[0]
        self.assertEqual(["network", "create", "--driver", "bridge"], network[:-1])
        name = network[-1]
        self.assertTrue(name.startswith("vessel-test-db-"))
        container = calls[1]
        self.assertEqual(name, container[container.index("--network") + 1])
        self.assertEqual(name, container[container.index("--name") + 1])
        self.assertEqual("postgres:18-alpine", container[-1])
        self.assertEqual("127.0.0.1::5432", container[container.index("--publish") + 1])
        self.assertEqual("/var/lib/postgresql", container[container.index("--tmpfs") + 1])
        self.assertIn("--rm", container)
        self.assertIn("POSTGRES_DB=vessel_tests", container)
        self.assertEqual(["exec", name, "pg_isready", "-h", "127.0.0.1", "-U", "vessel", "-d", "vessel_tests"], calls[2])
        self.assertNotIn("must-not-be-used", result.stdout + result.stderr + str(calls))
        self.assertNotIn("compose", str(calls))
        self.assertIn(f"docker rm --force --volumes {name} && docker network rm {name}", result.stderr)
        self.assertFalse(any(call[0] == "rm" for call in calls))

    def test_network_failure_does_not_remove_existing_resources(self):
        result = self.run_script("test-db.sh", DOCKER_FAIL="network create")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(1, len(self.calls("docker")))
        self.assertEqual("", result.stdout)

    def test_container_failure_removes_only_its_created_network(self):
        result = self.run_script("test-db.sh", DOCKER_FAIL="run")
        self.assertNotEqual(0, result.returncode)
        calls = [call[1] for call in self.calls("docker")]
        self.assertEqual(["network", "rm", calls[0][-1]], calls[-1])
        self.assertEqual("", result.stdout)

    def test_readiness_timeout_removes_container_and_network(self):
        result = self.run_script("test-db.sh", DOCKER_FAIL="exec", timeout=120)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("60 seconds", result.stderr)
        calls = [call[1] for call in self.calls("docker")]
        name = calls[0][-1]
        self.assertEqual(60, sum(call[0] == "exec" for call in calls))
        self.assertEqual(["rm", "--force", "--volumes", name], calls[-2])
        self.assertEqual(["network", "rm", name], calls[-1])
        self.assertEqual("", result.stdout)

    def test_unexpected_port_binding_cleans_up_without_exporting(self):
        result = self.run_script("test-db.sh", DOCKER_BINDING="0.0.0.0:55432")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("loopback-only", result.stderr)
        self.assertEqual("", result.stdout)
        self.assertEqual(["rm", "network"], [call[1][0] for call in self.calls("docker")[-2:]])

    def test_helper_accepts_no_existing_target(self):
        result = self.run_script("test-db.sh", "existing-db")
        self.assertEqual(2, result.returncode)
        self.assertEqual([], self.calls())


if __name__ == "__main__":
    unittest.main()
