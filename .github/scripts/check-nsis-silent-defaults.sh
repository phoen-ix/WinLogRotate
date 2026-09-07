#!/usr/bin/env bash
# Every MessageBox in an NSIS script must carry an /SD silent default.
#
# NSIS does NOT suppress message boxes under /S. A box without a default therefore hangs an
# unattended install forever, on a dialog nobody can see, and the deployment that invoked it
# simply never returns. The reference project calls this its single most important gotcha and
# enforces it by review; four lines of grep enforce it properly.
set -euo pipefail

script="${1:-packaging/winlogrotate.nsi}"
failed=0

# Strip CR first. .gitattributes checks .nsi out as CRLF - which is right for an NSIS script -
# so a continued line ends "\<CR>" rather than "\", the join below never matches, and every
# multi-line MessageBox looks like it lacks a default. A checker that only works on one line
# ending is a broken checker; being robust to whatever it is handed is its job.
#
# Then join continuations: a MessageBox is routinely split across several lines with a trailing
# backslash, and the /SD usually lives on the last of them.
joined="$(tr -d '\r' < "$script" | sed -e :a -e '/\\$/N; s/\\\n//; ta')"

while IFS= read -r line; do
    case "$line" in
        *"/SD "*) ;;
        *) echo "MessageBox without an /SD silent default:"; echo "    ${line#"${line%%[![:space:]]*}"}"; failed=1 ;;
    esac
done < <(printf '%s\n' "$joined" | grep -E '^\s*MessageBox\s' || true)

if [ "$failed" -ne 0 ]; then
    echo
    echo "Add an /SD default (e.g. /SD IDNO) so a silent install cannot hang on an invisible dialog."
    exit 1
fi

echo "All MessageBox calls in $script carry an /SD default."
