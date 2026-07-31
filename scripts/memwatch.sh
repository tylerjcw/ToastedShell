#!/usr/bin/env bash
# memwatch.sh — sample TOTAL SYSTEM memory and name the top consumers.
#
# TS-P2-38: a 128 GB machine was exhausted three times in one session while the
# test suite ran. The suite was blamed and then exonerated by measurement — RSS
# across a full run is dead flat at ~2.8 GB, and 32 threads look identical to 8.
# What actually consumed the memory is still unknown, and the reason it is still
# unknown is that the first sampler matched only `dotnet|testhost`. It could not
# have seen a non-.NET consumer, and VS Code's Roslyn host alone measured 1.27 GB.
#
# So this samples the *machine*, not a process family, and records enough that the
# next exhaustion is diagnosed rather than guessed at.
#
# Deliberately bash rather than TōSh: this has to keep working while TōSh itself is
# mid-rebuild or broken, which is exactly when a suite run is likely to go wrong.
# A diagnostic that shares a failure mode with its subject is not a diagnostic.
#
# Usage:
#   scripts/memwatch.sh -- dotnet test Tosh.slnx --no-build     # watch a command
#   scripts/memwatch.sh                                          # watch until Ctrl-C
#
# Options:
#   -i SECONDS   sample interval (default 2)
#   -t PERCENT   alert when available memory falls below this (default 20)
#   -o FILE      log file (default /tmp/tosh-memwatch-<timestamp>.log)

set -uo pipefail

interval=2
threshold=20
log=""

while getopts "i:t:o:h" opt; do
    case "$opt" in
        i) interval="$OPTARG" ;;
        t) threshold="$OPTARG" ;;
        o) log="$OPTARG" ;;
        h) sed -n '2,26p' "$0"; exit 0 ;;
        *) exit 2 ;;
    esac
done
shift $((OPTIND - 1))
[ "${1:-}" = "--" ] && shift

log="${log:-/tmp/tosh-memwatch-$(date +%Y%m%d-%H%M%S).log}"

mem_total_kb=$(awk '/^MemTotal:/ {print $2}' /proc/meminfo)

# Peak state, updated as we sample.
worst_used_kb=0
worst_snapshot=""
alerted=0

# Every process over 100 MB RSS, largest first, with its full command line.
top_consumers() {
    ps -eo rss=,pid=,comm=,args= --sort=-rss 2>/dev/null \
        | awk -v min=102400 '$1 >= min {
            rss = $1; pid = $2; $1 = ""; $2 = "";
            cmd = substr($0, 3);
            if (length(cmd) > 120) cmd = substr(cmd, 1, 120) "...";
            printf "      %8.1f MB  pid %-8s %s\n", rss / 1024, pid, cmd;
        }' | head -15
}

sample() {
    local available_kb used_kb used_pct swap_used_kb
    available_kb=$(awk '/^MemAvailable:/ {print $2}' /proc/meminfo)
    swap_used_kb=$(awk '/^SwapTotal:/ {t=$2} /^SwapFree:/ {f=$2} END {print t - f}' /proc/meminfo)
    used_kb=$((mem_total_kb - available_kb))
    used_pct=$((used_kb * 100 / mem_total_kb))

    printf '%s  used %6.1f GB / %.1f GB (%d%%)  swap %5.1f GB\n' \
        "$(date +%H:%M:%S)" \
        "$(awk -v k="$used_kb" 'BEGIN{print k/1048576}')" \
        "$(awk -v k="$mem_total_kb" 'BEGIN{print k/1048576}')" \
        "$used_pct" \
        "$(awk -v k="$swap_used_kb" 'BEGIN{print k/1048576}')" >> "$log"

    if [ "$used_kb" -gt "$worst_used_kb" ]; then
        worst_used_kb=$used_kb
        worst_snapshot=$(top_consumers)
    fi

    # Below the threshold of available memory: dump the full picture *now*, while
    # the consumer still exists. After the OOM killer runs it is too late to ask.
    local available_pct=$((available_kb * 100 / mem_total_kb))
    if [ "$available_pct" -lt "$threshold" ]; then
        {
            echo "  !! ALERT: only ${available_pct}% memory available — consumers over 100 MB:"
            top_consumers
        } >> "$log"
        if [ "$alerted" -eq 0 ]; then
            echo "memwatch: ALERT — available memory below ${threshold}%; see $log" >&2
            alerted=1
        fi
    fi
}

summarize() {
    {
        echo
        echo "── summary ──────────────────────────────────────────────"
        printf 'peak system usage: %.1f GB of %.1f GB (%d%%)\n' \
            "$(awk -v k="$worst_used_kb" 'BEGIN{print k/1048576}')" \
            "$(awk -v k="$mem_total_kb" 'BEGIN{print k/1048576}')" \
            "$((worst_used_kb * 100 / mem_total_kb))"
        echo "top consumers at peak:"
        echo "$worst_snapshot"
    } >> "$log"
    echo "memwatch: log written to $log" >&2
}

{
    echo "memwatch started $(date --iso-8601=seconds)"
    printf 'machine total: %.1f GB   interval: %ss   alert below: %s%% available\n\n' \
        "$(awk -v k="$mem_total_kb" 'BEGIN{print k/1048576}')" "$interval" "$threshold"
} > "$log"

if [ "$#" -gt 0 ]; then
    "$@" &
    child=$!
    trap 'kill "$child" 2>/dev/null' INT TERM
    while kill -0 "$child" 2>/dev/null; do
        sample
        sleep "$interval"
    done
    wait "$child"
    status=$?
    sample
    summarize
    exit "$status"
else
    trap 'summarize; exit 0' INT TERM
    while true; do
        sample
        sleep "$interval"
    done
fi
