#!/usr/bin/env bash
# TOAST-0087 step 1: audit declared command side-effect metadata against the
# APIs each command source actually calls.
cd /home/komrad/projects/tosh || exit 1

files=$(grep -rl --include='*.cs' ': ShellCommand' src/Toast.Stdlib src/Tosh.Stdlib | grep -v '/obj/\|/bin/' | sort)

# effect -> regex of CLR APIs that imply it
declare -A PAT
PAT[fs.read]='File\.(ReadAll|Open|Exists|ReadLines)|new StreamReader|Directory\.(Enumerate|GetFiles|GetDirectories|Exists)|FileInfo|DirectoryInfo'
PAT[fs.write]='File\.(WriteAll|AppendAll|Delete|Move|Copy|Create)|Directory\.(Create|Delete|Move)|new StreamWriter'
PAT[network]='HttpClient|TcpClient|UdpClient|Socket|Dns\.|new Ping|WebSocket|SmtpClient'
PAT[process]='Process\.Start|new Process|ProcessStartInfo'
PAT[env.read]='Environment\.GetEnvironmentVariable|Environment\.GetFolderPath|Environment\.CurrentDirectory|Environment\.ExpandEnvironment'
PAT[env.write]='Environment\.SetEnvironmentVariable|Environment\.CurrentDirectory *='
PAT[terminal]='Console\.(Write|Read|Clear|SetCursor|Beep)|AnsiConsole|Terminal\.'
PAT[native]='DllImport|NativeLibrary|Marshal\.|stackalloc|LibraryImport'
PAT[clr]='Assembly\.Load|Activator\.CreateInstance|GetMethod\(|Invoke\(.*BindingFlags|typeof\(.*\)\.GetType'

printf '%-46s | %-22s | %s\n' "COMMAND" "DECLARED" "OBSERVED"
printf '%s\n' "--------------------------------------------------------------------------------------------------------"

tot=0; declared=0; undeclared_effect=0; over=0
for f in $files; do
    tot=$((tot+1))
    name=$(basename "$f" .cs)

    # declared flags
    d=""
    if grep -q 'CommandSideEffects' "$f"; then
        declared=$((declared+1))
        line=$(grep 'CommandSideEffects(' "$f")
        grep -q 'ReadsFiles' <<<"$line"    && d="$d fs.read"
        grep -q 'WritesFiles' <<<"$line"   && d="$d fs.write"
        grep -q 'Network' <<<"$line"       && d="$d network"
        grep -q 'SpawnsProcess' <<<"$line" && d="$d process"
    fi

    # observed effects
    o=""
    for e in fs.read fs.write network process env.read env.write terminal native clr; do
        if grep -qE "${PAT[$e]}" "$f"; then o="$o $e"; fi
    done

    [ -z "$d" ] && [ -z "$o" ] && continue

    # is there an observed effect with no declaration at all?
    if [ -z "$d" ] && [ -n "$o" ]; then
        undeclared_effect=$((undeclared_effect+1))
        mark="UNDECLARED"
    else
        mark=""
    fi
    printf '%-46s | %-22s |%s %s\n' "$name" "${d:- (none)}" "$o" "$mark"
done

echo
echo "commands scanned:            $tot"
echo "declare [CommandSideEffects]: $declared"
echo "observed effects, undeclared: $undeclared_effect"
