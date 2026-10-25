#!/usr/bin/env python3
"""Local-only development runner. Secrets stay in the API process environment."""
from pathlib import Path
import os
import signal
import subprocess
import sys
import time
from urllib.parse import quote
import ipaddress

ROOT = Path(__file__).resolve().parent.parent


def configuration():
    values = {}
    path = ROOT / ".env"
    if path.exists():
        for line in path.read_text().splitlines():
            stripped = line.strip()
            if not stripped or stripped.startswith("#"):
                continue
            if "=" not in line:
                raise SystemExit("Invalid .env line. Use KEY=value, without shell commands.")
            key, value = line.split("=", 1)
            literal = value.strip()
            if len(literal) >= 2 and literal[0] in "'\"" and literal[-1] == literal[0]:
                literal = literal[1:-1]
            values[key.strip()] = literal
    # Explicit process environment takes precedence over the local development file.
    values.update(os.environ)
    env = dict(os.environ)
    # The retired Token settings are passed on too, so a stale .env makes the API refuse to start instead of ignoring them.
    for key in ["Vessel__Auth__OwnerId", "Vessel__Auth__OwnerName", "Vessel__Auth__Token", "Vessel__Auth__Mode"]:
        if key in values:
            env[key] = values[key]
    for key, value in values.items():
        if configuration_key(key).startswith(("vessel:identity:", "vessel:credentials:", "vessel:evidence:", "vessel:events:",
                                              "vessel:messaging:", "vessel:monitor:", "vessel:notifications:",
                                              "vessel:worker:", "vessel:paper:")):
            env[key] = value
    if not any(configuration_key(key) == "vessel:messaging:rabbitmquri" for key in env):
        user, password = values.get("RABBITMQ_USER"), values.get("RABBITMQ_PASSWORD")
        if user and password:
            env["Vessel__Messaging__RabbitMqUri"] = (
                f"amqp://{quote(user, safe='')}:{quote(password, safe='')}@127.0.0.1:55672/"
            )
    if values.get("ConnectionStrings__Vessel"):
        env["ConnectionStrings__Vessel"] = values["ConnectionStrings__Vessel"]
    else:
        password = values.get("POSTGRES_PASSWORD", "")
        if not password or password.startswith("replace-"):
            raise SystemExit("Set a private POSTGRES_PASSWORD in the ignored root .env first.")
        quoted = password.replace('"', '""')
        env["ConnectionStrings__Vessel"] = (
            'Host=127.0.0.1;Port=55432;Database=vessel;Username=vessel;Password="' + quoted + '"'
        )
    return env


# The API and worker cannot start without PostgreSQL and RabbitMQ; both bind loopback only.
SERVICES = ["docker", "compose", "up", "--detach", "--wait", "postgres", "rabbitmq"]


def require_broker(env):
    if not any(configuration_key(key) == "vessel:messaging:rabbitmquri" and value for key, value in env.items()):
        raise SystemExit("Set private RABBITMQ_USER and RABBITMQ_PASSWORD in the ignored root .env first.")


def start_services():
    # Compose reads the ignored root .env itself; nothing secret is passed on the command line.
    if os.environ.get("Vessel_DEV_SKIP_SERVICES") == "1":
        return
    try:
        subprocess.run(SERVICES, cwd=ROOT, check=True)
    except (OSError, subprocess.CalledProcessError):
        raise SystemExit("Could not start the local PostgreSQL and RabbitMQ services with Docker Compose.") from None


def configuration_key(key):
    # .NET accepts both separators, ignores case and strips these host prefixes.
    key = key.casefold()
    for prefix in ("dotnet_", "aspnetcore_"):
        if key.startswith(prefix):
            key = key[len(prefix):]
            break
    # .NET maps CUSTOMCONNSTR_*, POSTGRESQLCONNSTR_* and other *CONNSTR_*
    # aliases into ConnectionStrings. Conservatively treat every such alias as secret.
    prefix, separator, name = key.partition("_")
    if separator and prefix.endswith("connstr"):
        return "connectionstrings:" + name.replace("__", ":")
    return key.replace("__", ":")


def with_identity_origins(env, host):
    """Wallet sign-in accepts writes only from listed origins: default to the Vite page unless .env lists its own."""
    if any(configuration_key(key).startswith("vessel:identity:origins:") for key in env):
        return env
    origins = [f"http://{host}:5180"] + (["http://localhost:5180"] if host == "127.0.0.1" else [])
    return {**env, **{f"Vessel__Identity__Origins__{index}": origin for index, origin in enumerate(origins)}}


def identity_command(arguments):
    # Offline identity commands read the same settings; Development allows the plain-HTTP dev origin.
    return ["dotnet", "run", "--project", "backend/src/Vessel.Api", "--no-launch-profile", "--", "identity", *arguments]


def frontend_environment():
    # This process loads Vite plugins as well as the client build. Keep server
    # credentials out of it, even when they came from the caller's environment.
    return {key: value for key, value in os.environ.items()
            if not configuration_key(key).startswith(("vessel:", "connectionstrings:"))
            and not key.casefold().startswith("infisical_")
            and not key.casefold().startswith(("vessel_s3_", "rustfs_", "aws_", "rabbitmq_", "vessel_test_"))
            and key.casefold() != "postgres_password"}


def verification_environment():
    # Synthetic checks must not inherit a caller's owner, DB, key ring or storage.
    # Reuse the .NET separator/case-aware filter, then remove Compose overrides.
    return {key: value for key, value in frontend_environment().items()
            if not key.upper().startswith(("VESSEL_", "COMPOSE_"))
            # Do not let host settings select a caller's appsettings/secrets directory.
            and configuration_key(key) not in ("contentroot", "environment", "applicationname",
                                                "hostingstartupassemblies", "hostingstartupexcludeassemblies")}


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "run"
    env = configuration()
    if mode in ["migrate", "run", "api", "worker", "lan"]:
        require_broker(env)
    if mode == "identity":
        # For example: python3 scripts/dev.py identity bind-wallet --user <owner GUID> --address 0x…
        start_services()
        command = {**with_identity_origins(env, "127.0.0.1"), "ASPNETCORE_ENVIRONMENT": "Development"}
        raise SystemExit(subprocess.run(identity_command(sys.argv[2:]), cwd=ROOT, env=command).returncode)
    if mode == "migrate":
        start_services()
        subprocess.run(["dotnet", "tool", "restore"], cwd=ROOT / "backend", env=env, check=True)
        for project in ["src/Vessel.Identity", "src/Vessel.Persistence"]:
            subprocess.run(
                ["dotnet", "ef", "database", "update", "--project", project],
                cwd=ROOT / "backend", env=env, check=True,
            )
        # The configured owner becomes the operator user; binding its wallet stays an explicit step.
        subprocess.run(identity_command(["ensure-owner"]), cwd=ROOT,
                       env={**with_identity_origins(env, "127.0.0.1"), "ASPNETCORE_ENVIRONMENT": "Development"}, check=True)
        subprocess.run(
            ["dotnet", "run", "--project", "backend/src/Vessel.Api", "--no-launch-profile", "--", "provision-messaging"],
            cwd=ROOT, env=env, check=True,
        )
        return
    if mode not in ["run", "api", "worker", "lan"]:
        raise SystemExit("Use run, api, worker, lan, migrate, or identity.")
    host = "127.0.0.1"
    if mode == "lan":
        host = os.environ.get("Vessel_DEV_HOST", "")
        try:
            address = ipaddress.ip_address(host)
        except ValueError:
            raise SystemExit("Set Vessel_DEV_HOST to the server's private LAN IPv4 address.") from None
        if address.version != 4 or not address.is_private or address.is_loopback or address.is_unspecified or address.is_multicast:
            raise SystemExit("LAN preview must bind an explicit private, non-loopback IPv4 address.")
    env = with_identity_origins(env, host)
    start_services()
    processes = []
    def stop(*_):
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM, stop)
    try:
        if mode in ["run", "worker", "lan"]:
            # Build once, then run the monitor and notifications workers from that build so they never race.
            subprocess.run(["dotnet", "build", "backend/src/Vessel.Worker"], cwd=ROOT, env=env, check=True)
        api_command = ["dotnet", "run", "--project", "backend/src/Vessel.Api"]
        if mode == "lan":
            api_command += ["--configuration", "Release", "--no-build", "--no-launch-profile",
                            "--urls", "http://127.0.0.1:5080"]
            env["ASPNETCORE_ENVIRONMENT"] = "Development"
        if mode != "worker":
            processes.append(subprocess.Popen(
                api_command,
                cwd=ROOT, env=env, start_new_session=True,
            ))
        if mode in ["run", "worker", "lan"]:
            worker = ["dotnet", "run", "--project", "backend/src/Vessel.Worker", "--no-build", "--no-launch-profile"]
            # Workers validate the same sign-in settings as the API, whose dev origin is plain HTTP.
            env = {**env, "DOTNET_ENVIRONMENT": "Development"}
            processes.append(subprocess.Popen(worker, cwd=ROOT, env=env, start_new_session=True))
            # The Notification Hub is its own deployment with its own heartbeat file.
            processes.append(subprocess.Popen(worker, cwd=ROOT, env={
                **env,
                "Vessel__Worker__Modules__0": "notifications",
                "Vessel__Worker__Deployment": "notifications",
                "Vessel__Notifications__HeartbeatFile": env.get("Vessel__Notifications__HeartbeatFile",
                                                                "/tmp/vessel-dev-notifications-heartbeat"),
            }, start_new_session=True))
            if os.environ.get("Vessel_DEV_PAPER") == "1":
                # A new deployment, never a changed module set for the existing monitor.
                paper_env = {key: value for key, value in env.items()
                             if not configuration_key(key).startswith((
                                 "vessel:worker:modules:", "vessel:credentials:",
                                 "vessel:evidence:", "vessel:messaging:"))}
                processes.append(subprocess.Popen(worker, cwd=ROOT, env={
                    **paper_env,
                    "Vessel__Worker__Modules__0": "paper",
                    "Vessel__Worker__Deployment": "paper",
                    "Vessel__Paper__HeartbeatFile": env.get("Vessel__Paper__HeartbeatFile",
                                                          "/tmp/vessel-dev-paper-heartbeat"),
                }, start_new_session=True))
        if mode in ["run", "lan"]:
            # Do not give the frontend process database or broker credentials.
            processes.append(subprocess.Popen(
                ["pnpm", "--filter", "vessel-frontend", "dev", "--host", host],
                cwd=ROOT, env=frontend_environment(), start_new_session=True,
            ))
            print(f"Frontend: http://{host}:5180 · API: http://127.0.0.1:5080", flush=True)
            print("Workers run the monitor and notifications modules. Stop with Ctrl+C; PostgreSQL and RabbitMQ keep running.", flush=True)
            print("Sign in with a browser wallet bound once by: python3 scripts/dev.py identity bind-wallet "
                  "--user <owner GUID> --address <0x public address>", flush=True)
            if os.environ.get("Vessel_DEV_PAPER") == "1":
                print("Optional paper worker runs separately; paper execution remains off until enabled in Settings.", flush=True)
        while True:
            for process in processes:
                if process.poll() is not None:
                    raise SystemExit(process.returncode)
            time.sleep(0.25)
    except KeyboardInterrupt:
        pass
    finally:
        for process in processes:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
        for process in processes:
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()


if __name__ == "__main__":
    main()
