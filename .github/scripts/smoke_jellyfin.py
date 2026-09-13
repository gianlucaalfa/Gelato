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

root = Path(os.environ["RUNNER_TEMP"]) / "gelato-smoke"
config = root / "config"
cache = root / "cache"
plugin = config / "plugins" / ("Gelato_" + os.environ["PLUGIN_VERSION"])
plugin.mkdir(parents=True)
cache.mkdir(parents=True)
with zipfile.ZipFile(next(Path("hardening-package").glob("*.zip"))) as archive:
    archive.extractall(plugin)
container = "gelato-hardening-smoke"
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
    return json.loads(body) if body else None

def wait_ready():
    for _ in range(90):
        try:
            info = request("/System/Info/Public")
            if not info["Version"].startswith("12."):
                raise AssertionError("Smoke test requires Jellyfin 12")
            return
        except (OSError, AssertionError):
            time.sleep(2)
    raise RuntimeError("Jellyfin did not become ready")

try:
    subprocess.run(["docker", "run", "--detach", "--name", container,
                    "--publish", "127.0.0.1:18096:8096",
                    "--volume", f"{config}:/config", "--volume", f"{cache}:/cache",
                    "jellyfin/jellyfin:12.0"], check=True)
    wait_ready()
    request("/Startup/User")
    password = secrets.token_urlsafe(24)
    request("/Startup/User", "POST", {"Name": "smoke-admin", "Password": password}, expected=204)
    request("/Startup/Complete", "POST", expected=204)
    admin = request("/Users/AuthenticateByName", "POST", {"Username": "smoke-admin", "Pw": password})["AccessToken"]
    plugins = request("/Plugins", token=admin)
    assert any(p["Name"] == "Gelato" and p["Version"] == os.environ["PLUGIN_VERSION"] for p in plugins)
    request("/Users/New", "POST", {"Name": "smoke-user", "Password": password}, admin)
    user = request("/Users/AuthenticateByName", "POST", {"Username": "smoke-user", "Pw": password})["AccessToken"]
    request("/Palco/Cache/smtp-config?ns=anfiteatro-registration", expected=401)
    request("/Palco/Cache/smtp-config?ns=anfiteatro-registration", token=user, expected=403)
    request("/gelato/catalogs/import-all", "POST", token=user, expected=403)
    assert request("/Palco/Registration/Enabled")["enabled"] is False
    request("/Palco/Registration/Request", "POST", {"Id": "request-smoke", "Data": "{}"}, expected=403)
    value = json.dumps({"Password": "ephemeral-smoke-secret"})
    request("/Palco/Cache/smtp-config?ns=anfiteatro-registration", "POST", {"Value": value}, admin)
    assert request("/Palco/Cache/smtp-config?ns=anfiteatro-registration", token=admin)["Value"] == value
    subprocess.run(["docker", "restart", container], check=True)
    wait_ready()
    assert request("/Palco/Cache/smtp-config?ns=anfiteatro-registration", token=admin)["Value"] == value
    print("Jellyfin 12 smoke passed: package loading, anonymous/user/admin policies, disabled registration, SMTP restart persistence")
finally:
    logs = subprocess.run(["docker", "logs", container], capture_output=True, text=True)
    Path("jellyfin-smoke.log").write_text(logs.stdout + logs.stderr)
    subprocess.run(["docker", "rm", "--force", container], check=False)
