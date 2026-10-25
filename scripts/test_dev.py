import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import dev


class DevelopmentEnvironmentTests(unittest.TestCase):
    def test_credential_configuration_is_loaded_only_for_the_api(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".env").write_text(
                "ConnectionStrings__Vessel=test-connection\n"
                "Vessel__Credentials__ActiveKeyId=test-key\n"
                "Vessel__Credentials__Keys__test-key=test-secret\n"
                "Vessel__Credentials__KeyFile=/private/key-ring.json\n"
                "Vessel__Evidence__Storage__S3__SecretAccessKey=test-s3-secret\n"
                "Vessel__Evidence__Storage__Provider=S3\n"
                "Vessel__Paper__HeartbeatFile=/private/paper-heartbeat\n"
            )
            with patch.object(dev, "ROOT", root), patch.dict(os.environ, {}, clear=True):
                api = dev.configuration()
                self.assertEqual("test-secret", api["Vessel__Credentials__Keys__test-key"])
                self.assertEqual("/private/key-ring.json", api["Vessel__Credentials__KeyFile"])
                self.assertEqual("test-s3-secret", api["Vessel__Evidence__Storage__S3__SecretAccessKey"])
                self.assertEqual("S3", api["Vessel__Evidence__Storage__Provider"])
                self.assertEqual("/private/paper-heartbeat", api["Vessel__Paper__HeartbeatFile"])
                self.assertEqual({}, dev.frontend_environment())

    def test_explicit_environment_wins_without_leaking_to_vite(self):
        values = {
            "PATH": "/bin",
            "Vessel__Auth__OwnerId": "test-owner",
            "Vessel__Identity__Origins__0": "http://127.0.0.1:5180",
            "Vessel__Credentials__Keys__current": "test-master-key",
            "Vessel__Credentials__ActiveKeyId": "current",
            "Vessel__Credentials__KeyFile": "/private/keys.json",
            "VESSEL__CREDENTIALS__KEYS__uppercase": "test-uppercase",
            "vessel__credentials__keys__lowercase": "test-lowercase",
            "Vessel:Credentials:Keys:colon": "test-colon",
            "CONNECTIONSTRINGS__Vessel": "test-uppercase-connection",
            "ConnectionStrings__Vessel": "test-connection",
            "POSTGRES_PASSWORD": "test-password",
            "Vessel_TEST_POSTGRES": "test-database",
            "Vessel_TEST_EVENT_RABBIT": "test-broker",
            "RABBITMQ_USER": "test-user",
            "RABBITMQ_PASSWORD": "test-broker-password",
            "rabbitmq_password": "test-lowercase-broker-password",
            "Vessel__Messaging__RabbitMqUri": "amqp://test:secret@localhost",
            "INFISICAL_TOKEN": "test-infisical-access-token",
            "infisical_client_secret": "test-infisical-client-secret",
            "Vessel__Evidence__Storage__S3__SecretAccessKey": "test-s3-secret",
            "VESSEL_S3_SECRET_ACCESS_KEY": "test-compose-secret",
            "RUSTFS_SECRET_KEY": "test-rustfs-secret",
            "AWS_SECRET_ACCESS_KEY": "test-aws-secret",
        }
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".env").write_text("Vessel__Credentials__ActiveKeyId=old\n")
            with patch.object(dev, "ROOT", root), patch.dict(os.environ, values, clear=True):
                self.assertEqual("current", dev.configuration()["Vessel__Credentials__ActiveKeyId"])
                self.assertEqual({"PATH": "/bin"}, dev.frontend_environment())

    def test_verification_drops_colon_separated_owner_settings_and_compose_overrides(self):
        safe = {"PATH": "/bin", "DOTNET": "/private-sdk/dotnet", "DOCKER_HOST": "unix:///run/docker.sock",
                "DOTNET_ROOT": "/private-sdk", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                "ASPNETCORE_HTTP_PORTS": "8080"}
        unsafe = {
            "Vessel:Credentials:KeyFile": "/tmp/vessel-synthetic-nonexistent-key-ring.json",
            "vessel__credentials__keyfile": "/tmp/another-synthetic-key-ring.json",
            "ConnectionStrings:Vessel": "synthetic-database",
            "CONNECTIONSTRINGS__Vessel": "another-synthetic-database",
            "Vessel:Evidence:Storage:S3:Endpoint": "https://must-not-be-contacted.invalid",
            "VESSEL_S3_ENDPOINT": "https://must-not-be-contacted.invalid",
            "COMPOSE_PROFILES": "unused-storage",
            "compose_file": "/tmp/must-not-be-loaded.yaml",
            "POSTGRES_PASSWORD": "synthetic-password",
            "AWS_SECRET_ACCESS_KEY": "synthetic-secret",
            "DOTNET_Vessel__Credentials__KeyFile": "/tmp/host-prefixed-synthetic-ring.json",
            "aspnetcore_vessel:credentials:keyfile": "/tmp/webhost-prefixed-synthetic-ring.json",
            "DOTNET_ConnectionStrings__Vessel": "host-prefixed-synthetic-database",
            "DOTNET_CONTENTROOT": "/tmp/must-not-be-loaded",
            "ASPNETCORE_ENVIRONMENT": "CallerEnvironment",
            "DOTNET_ApplicationName": "CallerApplication",
            "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES": "CallerStartup",
        }
        with patch.dict(os.environ, {**safe, **unsafe}, clear=True):
            self.assertEqual(safe, dev.verification_environment())

    def test_connection_string_aliases_never_reach_frontend_or_verification(self):
        aliases = ("MYSQL", "SQLAZURE", "SQL", "CUSTOM", "POSTGRESQL", "APIHUB",
                   "DOCDB", "EVENTHUB", "NOTIFICATIONHUB", "REDISCACHE", "SERVICEBUS")
        for alias in aliases:
            for key in (f"{alias}CONNSTR_Vessel", f"{alias.lower()}connstr_Vessel",
                        f"DOTNET_{alias}CONNSTR_Vessel", f"ASPNETCORE_{alias}CONNSTR_Vessel"):
                with self.subTest(key=key), patch.dict(os.environ, {"PATH": "/bin", key: "synthetic-secret"}, clear=True):
                    self.assertEqual({"PATH": "/bin"}, dev.frontend_environment())
                    self.assertEqual({"PATH": "/bin"}, dev.verification_environment())

    def test_monitor_configuration_and_broker_secrets_stay_in_server_environment(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".env").write_text(
                "ConnectionStrings__Vessel=test-connection\n"
                "RABBITMQ_USER=local-user\nRABBITMQ_PASSWORD=space /@secret\n"
                "Vessel__Monitor__Interval=00:20:00\n"
            )
            with patch.object(dev, "ROOT", root), patch.dict(os.environ, {}, clear=True):
                environment = dev.configuration()
                self.assertEqual("00:20:00", environment["Vessel__Monitor__Interval"])
                self.assertEqual("amqp://local-user:space%20%2F%40secret@127.0.0.1:55672/",
                                 environment["Vessel__Messaging__RabbitMqUri"])
                self.assertEqual({}, dev.frontend_environment())

    def test_api_and_worker_modes_require_broker_credentials_before_starting_anything(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".env").write_text("ConnectionStrings__Vessel=test-connection\n")
            with patch.object(dev, "ROOT", root), patch.dict(os.environ, {}, clear=True), \
                    patch.object(dev.subprocess, "run") as run, patch.object(dev.subprocess, "Popen") as popen:
                for mode in ["run", "api", "worker", "migrate"]:
                    with self.subTest(mode=mode), patch.object(dev.sys, "argv", ["dev.py", mode]):
                        with self.assertRaises(SystemExit) as stopped:
                            dev.main()
                        self.assertIn("RABBITMQ_USER", str(stopped.exception))
                run.assert_not_called()
                popen.assert_not_called()

    def test_dev_modes_start_postgres_and_rabbitmq_then_the_api_or_both_workers(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".env").write_text(
                "ConnectionStrings__Vessel=test-connection\n"
                "RABBITMQ_USER=local-user\nRABBITMQ_PASSWORD=local-password\n"
            )
            started = []

            class Process:
                pid = 0
                returncode = 0
                def poll(self):
                    return 0
                def wait(self, timeout=None):
                    return 0

            def popen(command, **kwargs):
                started.append((command, kwargs["env"]))
                return Process()
            with patch.object(dev, "ROOT", root), patch.dict(os.environ, {}, clear=True), \
                    patch.object(dev.sys, "argv", ["dev.py", "api"]), \
                    patch.object(dev.subprocess, "run") as run, patch.object(dev.subprocess, "Popen", side_effect=popen), \
                    patch.object(dev.signal, "signal"):
                with self.assertRaises(SystemExit):
                    dev.main()
                run.assert_called_once_with(dev.SERVICES, cwd=root, check=True)
                self.assertEqual(["dotnet", "run", "--project", "backend/src/Vessel.Api"], started[0][0])
                self.assertEqual("amqp://local-user:local-password@127.0.0.1:55672/",
                                 started[0][1]["Vessel__Messaging__RabbitMqUri"])
                # Wallet sign-in accepts writes from the Vite page by default.
                self.assertEqual(("http://127.0.0.1:5180", "http://localhost:5180"),
                                 (started[0][1]["Vessel__Identity__Origins__0"], started[0][1]["Vessel__Identity__Origins__1"]))
            started.clear()
            with patch.object(dev, "ROOT", root), patch.dict(os.environ, {}, clear=True), \
                    patch.object(dev.sys, "argv", ["dev.py", "worker"]), \
                    patch.object(dev.subprocess, "run") as run, patch.object(dev.subprocess, "Popen", side_effect=popen), \
                    patch.object(dev.signal, "signal"):
                with self.assertRaises(SystemExit):
                    dev.main()
                # One build, then the monitor worker and the notifications worker from it.
                self.assertIn(["dotnet", "build", "backend/src/Vessel.Worker"], [call.args[0] for call in run.call_args_list])
                worker = ["dotnet", "run", "--project", "backend/src/Vessel.Worker", "--no-build", "--no-launch-profile"]
                self.assertEqual([worker, worker], [command for command, _ in started])
                self.assertNotIn("Vessel__Worker__Modules__0", started[0][1])
                # Workers read the same sign-in settings, so they run as Development with the plain-HTTP dev origin.
                self.assertEqual(["Development", "Development"], [env["DOTNET_ENVIRONMENT"] for _, env in started])
                self.assertEqual("http://127.0.0.1:5180", started[1][1]["Vessel__Identity__Origins__0"])
                self.assertEqual(("notifications", "notifications"), (started[1][1]["Vessel__Worker__Modules__0"],
                                                                      started[1][1]["Vessel__Worker__Deployment"]))
                self.assertEqual("/tmp/vessel-dev-notifications-heartbeat", started[1][1]["Vessel__Notifications__HeartbeatFile"])
            started.clear()
            with patch.object(dev, "ROOT", root), \
                    patch.dict(os.environ, {"Vessel_DEV_PAPER": "1",
                                            "Vessel__Worker__Modules__1": "verification"}, clear=True), \
                    patch.object(dev.sys, "argv", ["dev.py", "worker"]), \
                    patch.object(dev.subprocess, "run"), \
                    patch.object(dev.subprocess, "Popen", side_effect=popen), \
                    patch.object(dev.signal, "signal"):
                with self.assertRaises(SystemExit):
                    dev.main()
                self.assertEqual([worker, worker, worker], [command for command, _ in started])
                paper = started[2][1]
                self.assertEqual("paper", paper["Vessel__Worker__Modules__0"])
                self.assertEqual("paper", paper["Vessel__Worker__Deployment"])
                self.assertNotIn("Vessel__Worker__Modules__1", paper)
                self.assertNotIn("Vessel__Messaging__RabbitMqUri", paper)
                self.assertEqual("/tmp/vessel-dev-paper-heartbeat", paper["Vessel__Paper__HeartbeatFile"])

    def test_configured_identity_origins_replace_the_dev_default(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".env").write_text("ConnectionStrings__Vessel=test-connection\n"
                                       "Vessel__Identity__Origins__0=http://10.1.0.219:5180\n")
            with patch.object(dev, "ROOT", root), patch.dict(os.environ, {}, clear=True):
                environment = dev.with_identity_origins(dev.configuration(), "127.0.0.1")
                self.assertEqual("http://10.1.0.219:5180", environment["Vessel__Identity__Origins__0"])
                self.assertNotIn("Vessel__Identity__Origins__1", environment)
                self.assertEqual({}, dev.frontend_environment())
        self.assertEqual({"Vessel__Identity__Origins__0": "http://10.1.0.219:5180"},
                         dev.with_identity_origins({}, "10.1.0.219"))

    def test_identity_mode_runs_an_offline_identity_command_without_the_broker(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".env").write_text("ConnectionStrings__Vessel=test-connection\n")
            arguments = ["bind-wallet", "--user", "11111111-1111-1111-1111-111111111111", "--address", "0x" + "a" * 40]
            with patch.object(dev, "ROOT", root), patch.dict(os.environ, {}, clear=True), \
                    patch.object(dev.sys, "argv", ["dev.py", "identity", *arguments]), \
                    patch.object(dev.subprocess, "run") as run:
                run.return_value.returncode = 3
                with self.assertRaises(SystemExit) as stopped:
                    dev.main()
                self.assertEqual(3, stopped.exception.code)
                command, env = run.call_args_list[-1].args[0], run.call_args_list[-1].kwargs["env"]
                self.assertEqual(dev.identity_command(arguments), command)
                self.assertEqual("Development", env["ASPNETCORE_ENVIRONMENT"])
                self.assertEqual("http://127.0.0.1:5180", env["Vessel__Identity__Origins__0"])
                self.assertEqual("test-connection", env["ConnectionStrings__Vessel"])

    def test_migrate_applies_identity_then_journal_migrations_then_ensures_the_owner(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".env").write_text("ConnectionStrings__Vessel=test-connection\n"
                                       "RABBITMQ_USER=local-user\nRABBITMQ_PASSWORD=local-password\n")
            with patch.object(dev, "ROOT", root), patch.dict(os.environ, {}, clear=True), \
                    patch.object(dev.sys, "argv", ["dev.py", "migrate"]), \
                    patch.object(dev.subprocess, "run") as run:
                dev.main()
                commands = [call.args[0] for call in run.call_args_list]
                update = ["dotnet", "ef", "database", "update", "--project"]
                self.assertEqual([dev.SERVICES, ["dotnet", "tool", "restore"], update + ["src/Vessel.Identity"],
                                  update + ["src/Vessel.Persistence"], dev.identity_command(["ensure-owner"]),
                                  ["dotnet", "run", "--project", "backend/src/Vessel.Api", "--no-launch-profile", "--",
                                   "provision-messaging"]], commands)
                self.assertEqual("Development", run.call_args_list[4].kwargs["env"]["ASPNETCORE_ENVIRONMENT"])


if __name__ == "__main__":
    unittest.main()
