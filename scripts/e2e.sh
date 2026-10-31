#!/usr/bin/env bash
# The end-to-end journeys: the real console and the real winlogrotate.exe, driven through every
# page, dialog and verb down to the files they write (tests/WinLogRotate.Gui.Tests/E2E; the map of
# what is covered is tests/WinLogRotate.Gui.Tests/COVERAGE.md).
#
#   scripts/e2e.sh [test-runner options]      # e.g. --filter-class '*JobJourneys'
#
# Windows only (Git Bash), with the .NET 10 SDK, from an elevated shell: every journey works in a
# sandbox of its own under C:\WinLogRotate-e2e, deleted when it ends. WINLOGROTATE_E2E_MACHINE=1
# also runs the journeys that change the machine itself (a scheduled task, the app theme) - CI
# sets it; leave it unset on a machine you care about. From anywhere else this refuses with exit 2:
# ci.yml's e2e job runs it.
#
# ARTIFACTS: where the test results go (default: a temp dir). No services, no ports.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) ;;
  *)
    echo "e2e: needs Windows; the e2e job in ci.yml runs it" >&2
    exit 2 ;;
esac

ARTIFACTS="${ARTIFACTS:-$(mktemp -d)}"
mkdir -p "$ARTIFACTS"

WINLOGROTATE_E2E=1 dotnet test tests/WinLogRotate.Gui.Tests/WinLogRotate.Gui.Tests.csproj -c Release \
  --results-directory "$(cygpath -w "$ARTIFACTS")" --filter-trait "Category=E2E" "$@"
