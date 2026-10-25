"""Small executable checks for optional paper host wiring; no live services."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parent.parent


class PaperDeploymentTests(unittest.TestCase):
    def test_paper_health_uses_its_own_productive_heartbeat(self):
        with tempfile.TemporaryDirectory() as directory:
            heartbeat = Path(directory) / "paper-heartbeat"
            env = {"PATH": os.environ["PATH"], "Vessel__Worker__Modules__0": "paper",
                   "Vessel__Paper__HeartbeatFile": str(heartbeat),
                   "Vessel__Monitor__HeartbeatFile": str(Path(directory) / "missing-monitor")}
            command = ["sh", str(ROOT / "deploy/worker-health")]
            self.assertNotEqual(0, subprocess.run(command, env=env, capture_output=True).returncode)
            for contents, expected in ((f"{int(time.time())} 120", 0), ("1 120", 1),
                                       ("invalid 120", 1), (f"{int(time.time())} 120 extra", 1)):
                with self.subTest(contents=contents):
                    heartbeat.write_text(contents)
                    self.assertEqual(expected, subprocess.run(command, env=env, capture_output=True).returncode)

    def test_paper_overlay_adds_a_separate_private_deployment_without_credentials(self):
        # No interpolation and no .env reading: only inspect the repository model.
        command = ["docker", "compose", "--env-file", "/dev/null",
                   "-f", str(ROOT / "deploy/compose.yaml"),
                   "-f", str(ROOT / "deploy/compose.paper.yaml"),
                   "--profile", "paper", "config", "--no-interpolate", "--format", "json"]
        result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, check=True)
        services = json.loads(result.stdout)["services"]
        paper = services["worker-paper"]
        environment = paper["environment"]
        if isinstance(environment, list):
            environment = dict(setting.split("=", 1) if "=" in setting else (setting, None) for setting in environment)
        self.assertEqual("paper", environment["Vessel__Worker__Modules__0"])
        self.assertEqual("paper", environment["Vessel__Worker__Deployment"])
        self.assertIn("Vessel__Identity__Origins__0", environment)
        self.assertNotIn("ports", paper)
        self.assertNotIn("volumes", paper)
        self.assertNotIn("Vessel__Credentials__KeyFile", environment)
        self.assertNotIn("Vessel__Messaging__RabbitMqUri", environment)
        self.assertEqual(["CMD", "/app/worker-health"], paper["healthcheck"]["test"])
        self.assertEqual("monitor", services["worker"]["environment"]["Vessel__Worker__Modules__0"])
        self.assertEqual("notifications", services["worker-notifications"]["environment"]["Vessel__Worker__Modules__0"])

    def paper_config(self, **overrides):
        command = ["docker", "compose", "--env-file", "/dev/null",
                   "-f", str(ROOT / "deploy/compose.yaml"),
                   "-f", str(ROOT / "deploy/compose.paper.yaml"),
                   "--profile", "paper", "config", "--format", "json"]
        env = {"PATH": os.environ["PATH"], "POSTGRES_PASSWORD": "synthetic",
               "RABBITMQ_USER": "synthetic", "RABBITMQ_PASSWORD": "synthetic",
               "Vessel__Auth__OwnerId": "00000000-0000-4000-8000-000000000001",
               "VESSEL_VERSION": "synthetic-paper-build", "VESSEL_IDENTITY_ORIGIN": "https://paper.invalid", **overrides}
        return subprocess.run(command, cwd=ROOT, env={k: v for k, v in env.items() if v is not None},
                              capture_output=True, text=True)

    def test_paper_worker_shares_the_api_sign_in_settings_for_the_identity_directory(self):
        result = self.paper_config()
        self.assertEqual(0, result.returncode, result.stderr)
        services = json.loads(result.stdout)["services"]
        paper, api = services["worker-paper"]["environment"], services["api"]["environment"]
        for key in ("Vessel__Auth__OwnerId", "Vessel__Auth__OwnerName", "Vessel__Identity__Origins__0",
                    "Vessel__Identity__Registration", "Vessel__Identity__AllowLegacyDefaultOwner"):
            self.assertEqual(api[key], paper[key], key)
        self.assertEqual("https://paper.invalid", paper["Vessel__Identity__Origins__0"])
        self.assertEqual("Closed", paper["Vessel__Identity__Registration"])
        self.assertEqual(("Wallet", ""), (paper["Vessel__Auth__Mode"], paper["Vessel__Auth__Token"]))
        self.assertNotIn("Vessel__Messaging__RabbitMqUri", paper)
        # Leftovers of the removed Token sign-in reach the worker like the API, so both refuse to start.
        stale = self.paper_config(VESSEL_AUTH_MODE="Token", Vessel__Auth__Token="synthetic-leftover")
        self.assertEqual(0, stale.returncode, stale.stderr)
        services = json.loads(stale.stdout)["services"]
        for service in ("worker-paper", "api"):
            environment = services[service]["environment"]
            self.assertEqual(("Token", "synthetic-leftover"),
                             (environment["Vessel__Auth__Mode"], environment["Vessel__Auth__Token"]), service)

    def test_paper_overlay_fails_closed_without_an_identity_origin(self):
        for origin in (None, ""):
            with self.subTest(origin=origin):
                result = self.paper_config(VESSEL_IDENTITY_ORIGIN=origin)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("VESSEL_IDENTITY_ORIGIN", result.stderr)

if __name__ == "__main__":
    unittest.main()
