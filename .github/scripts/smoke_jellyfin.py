"""Test the packaged plugin in an isolated Jellyfin 12 server, including real authorization."""
import json
import os
from pathlib import Path
import secrets
import subprocess
import time
import urllib.error
import urllib.request
import zipfile

flavor = os.environ.get("JELLYFIN_FLAVOR", "official")
if flavor not in ("official", "linuxserver"):
    raise ValueError("Unknown Jellyfin image flavor")
linuxserver = flavor == "linuxserver"
root = Path(os.environ["RUNNER_TEMP"]) / ("gelato-smoke-" + flavor)
config = root / "config"
cache = root / "cache"
data = config / "data" if linuxserver else config
plugin = data / "plugins" / ("Gelato_" + os.environ["PLUGIN_VERSION"])
plugin.mkdir(parents=True)
cache.mkdir(parents=True)
with zipfile.ZipFile(next(Path("hardening-package").glob("*.zip"))) as archive:
    archive.extractall(plugin)
container = "gelato-hardening-smoke-" + flavor
base = "http://127.0.0.1:18096"
auth_header = 'MediaBrowser Client="GelatoRegression", Device="CI", DeviceId="gelato-smoke", Version="1.0"'

def request(path, method="GET", data=None, token=None, expected=200):
    headers = {"Authorization": auth_header + (f', Token="{token}"' if token else "")}
    if data is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(base + path, method=method, headers=headers,
                                 data=json.dumps(data).encode() if data is not None else None)
    try:
        with urllib.request.urlopen(req, timeout=10) as response:
            status, body = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, body = error.code, error.read()
    if status != expected:
        raise AssertionError(f"{method} {path}: expected {expected}, got {status}")
    return json.loads(body, object_hook=lambda value: {key.lower(): item for key, item in value.items()}) if body else None

def wait_ready(token=None):
    deadline = time.monotonic() + 180
    next_diagnostic = time.monotonic() + 30
    while time.monotonic() < deadline:
        try:
            info = request("/System/Info/Public")
            if not info.get("version", "").startswith("12."):
                raise AssertionError("Smoke test requires Jellyfin 12")
            # The startup server exposes public info before MVC and plugins are ready.
            # Probe an actual controller, both on first boot and after restart.
            request("/Plugins" if token else "/Startup/User", token=token)
            return
        except (OSError, AssertionError) as error:
            if time.monotonic() >= next_diagnostic:
                print(f"Waiting for {flavor} Jellyfin readiness ({type(error).__name__})", flush=True)
                logs = subprocess.run(["docker", "logs", "--tail", "120", container],
                                      capture_output=True, text=True, timeout=10)
                Path(f"jellyfin-smoke-{flavor}.log").write_text(logs.stdout + logs.stderr)
                print(logs.stdout + logs.stderr, flush=True)
                next_diagnostic = time.monotonic() + 30
            time.sleep(2)
    raise RuntimeError("Jellyfin did not become ready")

try:
    image = (
        "lscr.io/linuxserver/jellyfin:12.0ubu2604-ls48@sha256:0f42497a69fa0441bfd5f9d6bba8694f2a656ec984e571d0ac04c6dd91250039"
        if linuxserver else
        "jellyfin/jellyfin:12.0@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5"
    )
    arguments = ["docker", "run", "--detach", "--name", container,
                 "--publish", "127.0.0.1:18096:8096", "--volume", f"{config}:/config"]
    if linuxserver:
        # LinuxServer keeps DataPath below /config/data and runs Jellyfin as abc.
        # Use the production UID/GID convention with an isolated, empty data directory.
        arguments += ["--env", "PUID=1000", "--env", "PGID=1000", "--env", "TZ=Europe/Rome"]
    else:
        arguments += ["--volume", f"{cache}:/cache"]
    subprocess.run(arguments + [image], check=True)
    wait_ready()
    request("/Startup/User")
    password = secrets.token_urlsafe(24)
    request("/Startup/User", "POST", {"Name": "smoke-admin", "Password": password}, expected=204)
    request("/Startup/Complete", "POST", expected=204)
    admin = request("/Users/AuthenticateByName", "POST", {"Username": "smoke-admin", "Pw": password})["accesstoken"]
    plugins = request("/Plugins", token=admin)
    assert any(p["name"] == "Gelato" and p["version"] == os.environ["PLUGIN_VERSION"] for p in plugins)
    request("/Users/New", "POST", {"Name": "smoke-user", "Password": password}, admin)
    user = request("/Users/AuthenticateByName", "POST", {"Username": "smoke-user", "Pw": password})["accesstoken"]
    request("/Palco/Cache/smtp-config?ns=anfiteatro-registration", expected=401)
    request("/Palco/Cache/smtp-config?ns=anfiteatro-registration", token=user, expected=403)
    request("/gelato/catalogs/import-all", "POST", token=user, expected=403)
    assert request("/Palco/Registration/Enabled")["enabled"] is False
    request("/Palco/Registration/Request", "POST", {"Id": "request-smoke", "Data": "{}"}, expected=403)
    value = json.dumps({"Password": "ephemeral-smoke-secret"})
    request("/Palco/Cache/smtp-config?ns=anfiteatro-registration", "POST", {"Value": value}, admin)
    assert request("/Palco/Cache/smtp-config?ns=anfiteatro-registration", token=admin)["value"] == value
    subprocess.run(["docker", "restart", container], check=True)
    wait_ready(admin)
    assert request("/Palco/Cache/smtp-config?ns=anfiteatro-registration", token=admin)["value"] == value
    print(f"Jellyfin 12 ({flavor}) smoke passed: package loading, anonymous/user/admin policies, disabled registration, SMTP restart persistence")
finally:
    logs = subprocess.run(["docker", "logs", container], capture_output=True, text=True)
    Path(f"jellyfin-smoke-{flavor}.log").write_text(logs.stdout + logs.stderr)
    print("\n".join((logs.stdout + logs.stderr).splitlines()[-120:]))
    subprocess.run(["docker", "rm", "--force", container], check=False)
