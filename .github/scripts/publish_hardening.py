"""Publish a separate prerelease and feed without changing the stable gh-pages branch."""
import json
import os
from pathlib import Path
import subprocess

repo = os.environ["GITHUB_REPOSITORY"]
sha = os.environ["GITHUB_SHA"]
version = os.environ["PLUGIN_VERSION"]
branch = "hardening-feed"
output = Path("hardening-package")

def api(endpoint, payload=None):
    args = ["gh", "api", f"repos/{repo}/{endpoint}"]
    if payload is not None:
        args += ["--method", "POST", "--input", "-"]
    result = subprocess.run(args, input=json.dumps(payload) if payload is not None else None,
                            text=True, capture_output=True, check=True)
    return json.loads(result.stdout)

# Skip superseded builds. A later successful push will publish its own immutable release.
if api("git/ref/heads/fix/hardening-native-versions")["object"]["sha"] != sha:
    print("Skipping superseded preview build")
    raise SystemExit(0)

tag = f"hardening-v{version}"
subprocess.run(["gh", "release", "create", tag, "--repo", repo, "--target", sha,
                "--title", f"Gelato hardening {version}", "--prerelease", "--latest=false",
                "--notes", f"Security and HTTP streaming preview. Source: {sha}. Regression tests passed. See docs/hardening.md for installation and rollback.",
                *map(str, sorted(output.glob("gelato_*")))], check=True)

# Only this workflow owns this branch. Preserve other files if they are ever added.
refs = api("git/matching-refs/heads/" + branch)
head = next((ref["object"]["sha"] for ref in refs if ref["ref"] == "refs/heads/" + branch), None)
payload = {"tree": [{"path": "repository.json", "mode": "100644", "type": "blob",
                       "content": (output / "repository.json").read_text()}]}
if head:
    payload["base_tree"] = api("git/commits/" + head)["tree"]["sha"]
tree = api("git/trees", payload)["sha"]
commit = api("git/commits", {"message": f"chore(feed): publish hardening {version}", "tree": tree,
                              "parents": [head] if head else []})["sha"]
if head:
    subprocess.run(["gh", "api", "--method", "PATCH", f"repos/{repo}/git/refs/heads/{branch}",
                    "--input", "-"], input=json.dumps({"sha": commit, "force": False}), text=True, check=True)
else:
    api("git/refs", {"ref": "refs/heads/" + branch, "sha": commit})
print(f"Published https://raw.githubusercontent.com/{repo}/{branch}/repository.json")
