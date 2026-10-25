#!/usr/bin/env python3
"""RustFS/PostgreSQL S3 tests in a disposable Compose project. Never reads the owner's .env/storage."""
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import uuid
from dev import verification_environment

ROOT = Path(__file__).resolve().parent.parent


def main():
    with tempfile.TemporaryDirectory(prefix="vessel-storage-check-") as directory:
        path = Path(directory)
        project = "vessel-storage-check-" + secrets.token_hex(5)
        access, secret = secrets.token_hex(16), secrets.token_hex(32)
        postgres_password = secrets.token_hex(32)
        for file in ("compose.yaml", "compose.storage-local.yaml", "compose.storage-external.yaml"):
            shutil.copyfile(ROOT / "deploy" / file, path / file)
        # Compose interpolates every service; only rustfs, postgres and storage-init start here.
        (path / ".env").write_text(
            f"POSTGRES_PASSWORD={postgres_password}\nRABBITMQ_USER=vessel\nRABBITMQ_PASSWORD={secrets.token_hex(24)}\n"
            f"Vessel__Auth__OwnerId={uuid.uuid4()}\nVESSEL_IDENTITY_ORIGIN=https://localhost\n"
            f"VESSEL_S3_ACCESS_KEY_ID={access}\nVESSEL_S3_SECRET_ACCESS_KEY={secret}\n")
        (path / ".env").chmod(0o600)
        (path / "test.yaml").write_text("""services:
  rustfs:
    ports: ["127.0.0.1::9000"]
  postgres:
    ports: ["127.0.0.1::5432"]
networks:
  default:
    ipam: !reset {}
""")
        base = ["docker", "compose", "--project-directory", directory, "-p", project, "-f", str(path / "compose.yaml")]
        compose = [*base, "-f", str(path / "test.yaml")]
        compose_env = verification_environment()
        try:
            config = json.loads(subprocess.check_output([*base, "config", "--format", "json"], env=compose_env))
            assert not config["services"]["rustfs"].get("ports")
            assert config["services"]["rustfs"]["environment"]["RUSTFS_CONSOLE_ENABLE"] == "false"
            # Local and external configurations must be usable without any RustFS dependency.
            for overlay in ("compose.storage-local.yaml", "compose.storage-external.yaml"):
                result = subprocess.run([*base, "-f", str(path / overlay), "config", "--services"], check=True,
                                        capture_output=True, text=True, env={**compose_env, "VESSEL_S3_ACCESS_KEY_ID": "", "VESSEL_S3_SECRET_ACCESS_KEY": ""})
                assert "rustfs" not in result.stdout and "storage-init" not in result.stdout
            subprocess.run([*compose, "up", "-d", "--wait", "rustfs", "postgres"], check=True, env=compose_env)
            subprocess.run([*compose, "run", "--rm", "storage-init"], check=True, env=compose_env)
            # Repeating initialization must not erase existing storage.
            subprocess.run([*compose, "run", "--rm", "storage-init"], check=True, env=compose_env)
            address = subprocess.check_output([*compose, "port", "rustfs", "9000"], text=True, env=compose_env).strip()
            postgres_address = subprocess.check_output(
                [*compose, "port", "postgres", "5432"], text=True, env=compose_env).strip()
            postgres_host, postgres_port = postgres_address.rsplit(":", 1)
            assert postgres_host == "127.0.0.1" and postgres_port.isdecimal()
            env = {**compose_env, "VESSEL_TEST_S3_ENDPOINT": "http://" + address,
                   "VESSEL_TEST_S3_ACCESS_KEY": access, "VESSEL_TEST_S3_SECRET_KEY": secret,
                   "Vessel_TEST_POSTGRES": (
                       f"Host={postgres_host};Port={postgres_port};Database=vessel;"
                       f"Username=vessel;Password={postgres_password}")}
            subprocess.run([os.environ.get("DOTNET", "dotnet"), "test", str(ROOT / "backend/Vessel.slnx"),
                            "--filter", "FullyQualifiedName~S3", "--verbosity", "minimal"], env=env, check=True)
        finally:
            subprocess.run([*compose, "down", "-v", "--remove-orphans"], check=True, env=compose_env)


if __name__ == "__main__":
    main()
