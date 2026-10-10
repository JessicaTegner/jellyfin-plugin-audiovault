#!/usr/bin/env bash
# Builds the plugin zip with jprm and adds it to a plugin repository folder (manifest.json). Nothing is uploaded.
# The release workflow runs this same script; see docs/releasing.md.
#
# Usage: scripts/package.sh [--skip-tests] [--repo DIR] [--zip-url URL] <base-url>
#   <base-url>     where the repository folder will be served from, e.g. http://localhost:18600
#   --repo DIR     repository folder to add the zip to (default: artifacts/repo)
#   --zip-url URL  the zip's own download address when it isn't served from the repository folder (a GitHub
#                  release asset, say); the zip is then not copied into the folder
#   --skip-tests   for CI, which runs the tests in a step of its own
set -euo pipefail
cd "$(dirname "$0")/.."

repo="artifacts/repo"
zip_url=""
run_tests=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) repo="${2:?--repo needs a folder}"; shift 2 ;;
    --zip-url) zip_url="${2:?--zip-url needs an address}"; shift 2 ;;
    --skip-tests) run_tests=0; shift ;;
    -*) echo "Unknown option $1" >&2; exit 2 ;;
    *) break ;;
  esac
done
base_url="${1:?usage: scripts/package.sh [--skip-tests] [--repo DIR] [--zip-url URL] <base-url>}"

jprm_version="1.1.0"
venv="artifacts/.jprm-venv"
if [[ ! -x "$venv/bin/jprm" ]] || ! "$venv/bin/pip" show jprm 2>/dev/null | grep -qx "Version: $jprm_version"; then
  python3 -m venv "$venv"
  "$venv/bin/pip" install -q "jprm==$jprm_version"
fi

# Jellyfin offers and loads the plugin only on servers at least as new as targetAbi. It must equal the Jellyfin
# packages the plugin is compiled against: older lets servers install a plugin that can't load, newer hides it from
# servers it runs on. PackagingTests checks the same.
abi=$(sed -n 's/^targetAbi: *"\{0,1\}\([0-9.]*\)"\{0,1\} *$/\1/p' build.yaml)
jellyfin=$(sed -n 's/.*<JellyfinVersion[^>]*>\([0-9.]*\)<\/JellyfinVersion>.*/\1/p' Directory.Build.props)
version=$(sed -n 's/^version: *"\{0,1\}\([0-9.]*\)"\{0,1\} *$/\1/p' build.yaml)
if [[ -z "$abi" || -z "$jellyfin" || -z "$version" ]]; then
  echo "Could not read version and targetAbi from build.yaml or JellyfinVersion from Directory.Build.props" >&2
  exit 1
fi
four_parts() { awk -F. '{ printf "%d.%d.%d.%d", $1, $2, $3, $4 }' <<<"$1"; }
if [[ "$(four_parts "$abi")" != "$(four_parts "$jellyfin")" ]]; then
  echo "build.yaml targetAbi $abi doesn't match JellyfinVersion $jellyfin in Directory.Build.props; make them equal" >&2
  exit 1
fi

if (( run_tests )); then
  AUDIOVAULT_REQUIRE_FFMPEG=1 dotnet test
fi

mkdir -p artifacts/zips "$repo"
zip=$("$venv/bin/jprm" plugin build . --output artifacts/zips | tail -n1)
[[ -f "$repo/manifest.json" ]] || "$venv/bin/jprm" repo init "$repo"
if [[ -n "$zip_url" ]]; then
  "$venv/bin/jprm" repo add --url "$base_url" --plugin-url "$zip_url" "$repo" "$zip"
else
  "$venv/bin/jprm" repo add --url "$base_url" "$repo" "$zip"
fi

echo "Built $zip (version $version, targetAbi $abi)."
echo "Repository ready in $repo. Add $base_url/manifest.json under Dashboard > Plugins > Repositories."
