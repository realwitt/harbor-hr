#!/bin/bash
# Drop old Harbor images on the deploy host.
# Keep :latest and the two newest 7-hex tags, locally and in the Forgejo registry.
# Never run docker system prune -a.
set -euo pipefail

REGISTRY="${REGISTRY:-127.0.0.1:3000}"
OWNER="${OWNER:-realwitt}"
KEEP_SHA="${KEEP_SHA:-2}"

prune_local() {
  local repo="$1"
  docker images --format '{{.Tag}}\t{{.CreatedAt}}' "$repo" \
    | awk -F'\t' '$1 ~ /^[0-9a-f]{7}$/' \
    | sort -t$'\t' -k2,2r \
    | awk -F'\t' -v keep="$KEEP_SHA" 'NR > keep { print $1 }' \
    | while read -r tag; do
        echo "rmi ${repo}:${tag}"
        docker rmi "${repo}:${tag}" || true
      done
}

prune_local "$REGISTRY/$OWNER/harbor-api"
prune_local "$REGISTRY/$OWNER/harbor-web"

if [ ! -f /opt/forge/.admin-credentials ]; then
  echo "registry prune skipped: no admin credentials"
  exit 0
fi

TOKEN=$(awk '/claude-ops/{print $NF}' /opt/forge/.admin-credentials)
if [ -z "${TOKEN}" ]; then
  echo "registry prune skipped: no token"
  exit 0
fi

KEEP_SHA="$KEEP_SHA" TOKEN="$TOKEN" OWNER="$OWNER" python3 - <<'PY'
import json, os, re, urllib.error, urllib.parse, urllib.request

token = os.environ["TOKEN"]
owner = os.environ["OWNER"]
keep = int(os.environ.get("KEEP_SHA", "2"))
api = "http://127.0.0.1:3000/api/v1"
names = {"harbor-api", "harbor-web"}
sha = re.compile(r"^[0-9a-f]{7}$")

def req(url, method="GET"):
    request = urllib.request.Request(url, method=method, headers={"Authorization": "token " + token})
    with urllib.request.urlopen(request, timeout=60) as response:
        raw = response.read()
        if method != "GET":
            return None
        return json.loads(raw or b"null")

pkgs = []
page = 1
while page <= 20:
    batch = req(f"{api}/packages/{owner}?type=container&limit=50&page={page}") or []
    pkgs.extend(batch)
    if len(batch) < 50:
        break
    page += 1

by_name = {name: [] for name in names}
for pkg in pkgs:
    name = pkg.get("name")
    if name in names:
        by_name[name].append(pkg)

deleted = 0
for name, versions in by_name.items():
    tagged = [item for item in versions if sha.fullmatch(item.get("version") or "")]
    tagged.sort(key=lambda item: item.get("created_at") or "", reverse=True)
    kept = tagged[:keep]
    cutoff = kept[-1]["created_at"] if kept else "9999"
    drop = []
    for item in versions:
        version = item.get("version") or ""
        if version == "latest":
            continue
        if sha.fullmatch(version) and item in kept:
            continue
        created = item.get("created_at") or ""
        if sha.fullmatch(version) or (version.startswith("sha256:") and created < cutoff):
            drop.append(item)
    for item in drop:
        version = urllib.parse.quote(item["version"], safe="")
        try:
            req(f"{api}/packages/{owner}/container/{name}/{version}", method="DELETE")
        except urllib.error.HTTPError as error:
            print("registry delete failed", name, item["version"], error.code)
            continue
        print("deleted registry", name, item["version"])
        deleted += 1

print(f"harbor registry prune done keep={keep} deleted={deleted}")
PY
