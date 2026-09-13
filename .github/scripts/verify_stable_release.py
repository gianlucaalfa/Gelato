"""Reject missing, preview or mismatched releases before uploading any stable package."""
import json
import os
from pathlib import Path
import re
import subprocess

version = re.search(r'^version: "([^"]+)"', Path("build.yaml").read_text(), re.M).group(1)
if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
    raise ValueError("Invalid release version")
tag = "v" + version
release = json.loads(subprocess.check_output(["gh", "api", f"repos/{os.environ['GITHUB_REPOSITORY']}/releases/tags/{tag}"], text=True))
if release["draft"] or release["prerelease"]:
    raise ValueError("Only published stable releases may update the stable feed")
head = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
tag_head = subprocess.check_output(["git", "rev-parse", tag + "^{commit}"], text=True).strip()
if head != tag_head:
    raise ValueError("Workflow source does not match the release tag; select the matching tag when retrying")
subprocess.run(["git", "merge-base", "--is-ancestor", head, "origin/main"], check=True)
with open(os.environ["GITHUB_OUTPUT"], "a") as output:
    output.write(f"upload_url={release['upload_url']}\n")
