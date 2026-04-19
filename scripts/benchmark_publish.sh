#!/usr/bin/env bash
set -euo pipefail

# benchmark_publish.sh — Compare publish configurations for TōSh
#
# Tests key combinations of:
#   - SelfContained vs Framework-dependent
#   - ReadyToRun (R2R) precompilation
#   - Single-file compression
#   - Trimming
#
# Measures: publish time, binary size, cold startup, warm startup, command execution

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Tosh.Cli/Tosh.Cli.csproj"
BENCH_DIR="$REPO_ROOT/artifacts/benchmarks"
RID="linux-x64"
WARMUP=3
RUNS=10

rm -rf "$BENCH_DIR"
mkdir -p "$BENCH_DIR"

# ── Configurations to test ──
# Format: "label|extra dotnet publish args"
CONFIGS=(
  "fdd|--self-contained false"
  "fdd-r2r|--self-contained false -p:PublishReadyToRun=true"
  "sc|--self-contained true"
  "sc-compress|--self-contained true -p:EnableCompressionInSingleFile=true"
  "sc-r2r|--self-contained true -p:PublishReadyToRun=true"
  "sc-r2r-compress|--self-contained true -p:PublishReadyToRun=true -p:EnableCompressionInSingleFile=true"
  "sc-trimmed|--self-contained true -p:PublishTrimmed=true -p:TrimMode=partial"
  "sc-trimmed-compress|--self-contained true -p:PublishTrimmed=true -p:TrimMode=partial -p:EnableCompressionInSingleFile=true"
)

BINARIES=()
LABELS=()
SIZES=()
BUILD_TIMES=()

echo "╔══════════════════════════════════════════════════════════════╗"
echo "║           TōSh Publish Configuration Benchmark              ║"
echo "╠══════════════════════════════════════════════════════════════╣"
echo "║  RID: $RID"
echo "║  Hyperfine runs: $RUNS (warmup: $WARMUP)"
echo "║  Configs: ${#CONFIGS[@]}"
echo "╚══════════════════════════════════════════════════════════════╝"
echo ""

# ── Phase 1: Build each configuration ──
echo "━━━ Phase 1: Publishing all configurations ━━━"
echo ""

for entry in "${CONFIGS[@]}"; do
  IFS='|' read -r label extra <<< "$entry"
  out_dir="$BENCH_DIR/$label"
  mkdir -p "$out_dir"

  echo "▸ Building: $label"
  echo "  Args: $extra"

  start_time=$(date +%s%N)
  dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    -o "$out_dir" \
    -p:PublishSingleFile=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:DisableToshVsCodeExtensionSync=true \
    $extra \
    --nologo -v quiet 2>&1 || { echo "  ✘ FAILED"; continue; }
  end_time=$(date +%s%N)

  elapsed_ms=$(( (end_time - start_time) / 1000000 ))

  # Find the binary
  bin=""
  for candidate in "$out_dir/Tosh.Cli" "$out_dir/tosh"; do
    if [[ -f "$candidate" && -x "$candidate" ]]; then
      bin="$candidate"
      break
    fi
  done

  if [[ -z "$bin" ]]; then
    echo "  ✘ No executable found in $out_dir"
    continue
  fi

  size=$(stat -c %s "$bin")
  size_mb=$(awk "BEGIN { printf \"%.1f\", $size / 1048576 }")

  echo "  ✔ Size: ${size_mb} MB | Build time: ${elapsed_ms} ms"
  echo ""

  BINARIES+=("$bin")
  LABELS+=("$label")
  SIZES+=("$size")
  BUILD_TIMES+=("$elapsed_ms")
done

echo ""
echo "━━━ Phase 2: Startup benchmark (tosh -c 'exit') ━━━"
echo ""

# Build hyperfine command for startup comparison
HYPER_ARGS=("--warmup" "$WARMUP" "--runs" "$RUNS" "--export-markdown" "$BENCH_DIR/startup.md")
for i in "${!BINARIES[@]}"; do
  HYPER_ARGS+=("-n" "${LABELS[$i]}" "${BINARIES[$i]} -c 'exit'")
done

hyperfine "${HYPER_ARGS[@]}"

echo ""
echo "━━━ Phase 3: Command execution benchmark (echo hello) ━━━"
echo ""

HYPER_ARGS2=("--warmup" "$WARMUP" "--runs" "$RUNS" "--export-markdown" "$BENCH_DIR/echo.md")
for i in "${!BINARIES[@]}"; do
  HYPER_ARGS2+=("-n" "${LABELS[$i]}" "${BINARIES[$i]} -c 'echo hello'")
done

hyperfine "${HYPER_ARGS2[@]}"

echo ""
echo "━━━ Phase 4: Pipeline benchmark (seq 1 10000 | where _ > 5000 | count) ━━━"
echo ""

HYPER_ARGS3=("--warmup" "$WARMUP" "--runs" "$RUNS" "--export-markdown" "$BENCH_DIR/pipeline.md")
for i in "${!BINARIES[@]}"; do
  HYPER_ARGS3+=("-n" "${LABELS[$i]}" "${BINARIES[$i]} -c 'seq 1 10000 | where _ > 5000 | count'")
done

hyperfine "${HYPER_ARGS3[@]}"

# ── Summary ──
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "                        BUILD SUMMARY"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
printf "%-22s %10s %12s\n" "Configuration" "Size (MB)" "Build (ms)"
echo "──────────────────────────────────────────────────────────────"
for i in "${!LABELS[@]}"; do
  size_mb=$(awk "BEGIN { printf \"%.1f\", ${SIZES[$i]} / 1048576 }")
  printf "%-22s %10s %12s\n" "${LABELS[$i]}" "$size_mb" "${BUILD_TIMES[$i]}"
done
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "Detailed results saved to:"
echo "  $BENCH_DIR/startup.md"
echo "  $BENCH_DIR/echo.md"
echo "  $BENCH_DIR/pipeline.md"
