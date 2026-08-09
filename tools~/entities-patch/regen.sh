#!/usr/bin/env bash
# Rebuilds the .patch from the current state of the vendored package.
#
# Called after every edit of the fork: the file in this folder is the only thing that outlives a bump
# of the package, and it must not drift apart from the working copy.
#
# Bash and not PowerShell on purpose: the diff has to be written byte for byte, and PowerShell
# re-encodes the stream.
#
# The baseline (the commit holding the CLEAN package) is taken from the header of the existing patch;
# the first time round, or when the version changes, it is passed as the second argument.
#
#   ./regen.sh <path to the Unity project> [baseline-commit]

set -euo pipefail

PROJECT="${1:?the first argument is the path to the Unity project}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

cd "$PROJECT"

PKG_DIR="Packages/com.unity.entities"
[ -d "$PKG_DIR" ] || { echo "no '$PKG_DIR' — the package is not vendored" >&2; exit 1; }

VERSION=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PKG_DIR/package.json" | head -1)
OUT="$HERE/com.unity.entities@$VERSION.patch"

BASE="${2:-}"
if [ -z "$BASE" ] && [ -f "$OUT" ]; then
    BASE=$(sed -n 's/^# Baseline: .* in commit \([0-9a-f]\{7,\}\)\.$/\1/p' "$OUT" | head -1)
fi
if [ -z "$BASE" ]; then
    echo "nothing to derive the baseline from: pass the commit with the clean package as the second argument" >&2
    exit 1
fi

git cat-file -e "$BASE^{commit}" 2>/dev/null || { echo "commit '$BASE' does not exist in this repository" >&2; exit 1; }

# New files of the fork only enter the diff once marked for addition; their content does not go into the index.
UNTRACKED=$(git ls-files --others --exclude-standard -- "$PKG_DIR")
if [ -n "$UNTRACKED" ]; then
    echo "$UNTRACKED" | xargs -d '\n' git add -N --
fi

{
    echo "# A fork of com.unity.entities $VERSION for the Blobcheg reference patch."
    echo "#"
    echo "# Baseline: a clean $VERSION, as it lies in commit $BASE."
    echo "# Apply from the root of the Unity project, the package must already be vendored into $PKG_DIR:"
    echo "#     git apply --3way Packages/Blobcheg/tools~/entities-patch/com.unity.entities@$VERSION.patch"
    echo "# Check without applying:  git apply --check <the same path>"
    echo "#"
    echo "# Rebuild after editing the fork: tools~/entities-patch/regen.sh <project>"
    echo ""
    git diff "$BASE" -- "$PKG_DIR"
} > "$OUT"

if [ -n "$UNTRACKED" ]; then
    echo "$UNTRACKED" | xargs -d '\n' git reset -q --
fi

echo "assembled $OUT"
grep '^diff --git' "$OUT" | sed 's/^diff --git a\///; s/ b\/.*//' | sed 's/^/  /'
