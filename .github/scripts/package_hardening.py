"""Package only declared plugin dependencies and write a Jellyfin 12 feed entry."""
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import zipfile

root = Path(__file__).resolve().parents[2]
config = (root / "build.yaml").read_text()
field = lambda name: re.search(r'^' + name + r': "([^"]+)"', config, re.M).group(1)
version = os.environ["PLUGIN_VERSION"]
if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
    raise ValueError("Jellyfin requires a numeric four-part version")
artifacts = re.findall(r'^  - "([^"]+\.dll)"', config, re.M)
if "Gelato.dll" not in artifacts:
    raise ValueError("Missing main plugin artifact")
output = root / "hardening-package"
output.mkdir(exist_ok=True)
archive = output / f"gelato_{version}.zip"
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as package:
    for name in artifacts:
        package.write(root / "bin/Release/net10.0" / name, name)
content = archive.read_bytes()
md5 = hashlib.md5(content).hexdigest()
for suffix, digest in [("md5", md5), ("sha256", hashlib.sha256(content).hexdigest())]:
    archive.with_suffix("." + suffix).write_text(f"{digest}  {archive.name}\n")
entry = {
    "name": field("name"), "guid": field("guid"), "owner": "gianlucaalfa",
    "category": "General", "overview": "Gelato hardening preview channel",
    "description": "Security and HTTP streaming improvements. Independent preview update channel.",
    "versions": [{
        "version": version, "targetAbi": field("targetAbi"), "checksum": md5,
        "sourceUrl": f"https://github.com/{os.environ['GITHUB_REPOSITORY']}/releases/download/hardening-v{version}/{archive.name}",
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "changelog": f"Hardening preview from commit {os.environ['GITHUB_SHA']}. Regression tests passed before publication."
    }]
}
(output / "repository.json").write_text(json.dumps([entry], indent=2) + "\n")
