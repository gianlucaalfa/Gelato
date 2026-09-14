"""Add the verified stable release while preserving history and ignoring preview releases."""
import base64
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.request

repo = os.environ["GITHUB_REPOSITORY"]
version = re.search(r'^version: "([^"]+)"', Path("build.yaml").read_text(), re.M).group(1)

def get(path):
    return json.loads(subprocess.check_output(["gh", "api", f"repos/{repo}/{path}"], text=True))

release = get("releases/tags/v" + version)
if release["draft"] or release["prerelease"]:
    raise ValueError("Preview releases must never enter the stable feed")
archives = [asset for asset in release["assets"] if asset["name"].endswith(".zip")]
if len(archives) != 1:
    raise ValueError("Expected exactly one plugin archive")
archive = archives[0]
checksum_name = archive["name"][:-4] + ".md5"
checksum_asset = next(asset for asset in release["assets"] if asset["name"] == checksum_name)
with urllib.request.urlopen(checksum_asset["browser_download_url"], timeout=30) as response:
    checksum = response.read(4096).decode().split()[0]
if not re.fullmatch(r"[a-fA-F0-9]{32}", checksum):
    raise ValueError("Invalid plugin checksum")
current = get("contents/repository.json?ref=gh-pages")
feed = json.loads(base64.b64decode(current["content"]))
plugin = next(item for item in feed if item["guid"].lower() == "94ea4e14-8163-4989-96fe-0a2094bc2d6a")
versions = [item for item in plugin["versions"] if item["version"] != version]
versions.append({"version": version, "targetAbi": "12.0.0.0", "checksum": checksum,
                 "sourceUrl": archive["browser_download_url"], "timestamp": release["published_at"],
                 "changelog": release["body"] or ""})
plugin["versions"] = sorted(versions, key=lambda item: tuple(map(int, item["version"].split('.'))), reverse=True)
payload = {"branch": "gh-pages", "sha": current["sha"], "message": f"chore(feed): publish stable {version}",
           "content": base64.b64encode((json.dumps(feed, indent=2) + "\n").encode()).decode()}
subprocess.run(["gh", "api", "--method", "PUT", f"repos/{repo}/contents/repository.json", "--input", "-"],
               input=json.dumps(payload), text=True, check=True)
