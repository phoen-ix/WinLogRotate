#!/usr/bin/env bash
# Regenerate the docs screenshots: open the real console on a seeded demo machine and save every
# page and dialog the docs show (tests/WinLogRotate.Gui.Tests/DocsTour.cs).
#
#   scripts/docs-screenshots.sh [outdir]      # default: docs/screenshots
#
# Windows only (Git Bash), with the .NET 10 SDK, from an elevated shell: the console is WinForms,
# and the tour builds its demo machine under C:\WinLogRotate-demo, removing it again once every shot
# has passed (a failed run leaves it for inspection; the next run wipes and rebuilds it).
# From anywhere else, run the docs-screenshots workflow and download its artifact:
#   gh workflow run docs-screenshots.yml
#
# ARTIFACTS: where the test results go (default: a temp dir). The window shows the latest v* tag
# as its version when the checkout has tags; without them nothing is rebuilt for a version.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) ;;
  *)
    echo "docs-screenshots: needs Windows; run 'gh workflow run docs-screenshots.yml' instead" >&2
    exit 2 ;;
esac

OUT="$(mkdir -p "${1:-docs/screenshots}" && cd "${1:-docs/screenshots}" && pwd)"
ARTIFACTS="${ARTIFACTS:-$(mktemp -d)}"
VERSION="$(git describe --tags --abbrev=0 --match 'v[0-9]*.[0-9]*.[0-9]*' 2>/dev/null || true)"

DOCS_SCREENSHOT_DIR="$(cygpath -w "$OUT")" dotnet test tests/WinLogRotate.Gui.Tests/WinLogRotate.Gui.Tests.csproj \
  -c Release ${VERSION:+-p:Version="${VERSION#v}"} --results-directory "$(cygpath -w "$ARTIFACTS")"

shopt -s nullglob
PNGS=("$OUT"/*.png)
echo "docs-screenshots: ${#PNGS[@]} PNG(s) in $OUT"
