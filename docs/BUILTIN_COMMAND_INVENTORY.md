# Tosh — Complete Builtin Command Inventory

> Auto-generated reference. Source of truth: `BuiltInCommands.RegisterDefaults()` in
> `src/Tosh.Core/Commands/BuiltInCommands.cs` plus the `source` command registered by `ToshEngine`.

Total registered commands: **~210 command names** (some classes are registered under multiple aliases).

---

## 1. Discovery, Help & Session

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `help` | Shows searchable Tosh help for commands, language topics, CLR types, and externals. | `help [topic … \| browse [query] \| search <query> \| related <topic> \| categories]` | `help ls` |
| `apropos` | Searches Tosh help topics with fuzzy matching. | `apropos <query>` | `apropos file` |
| `exit` | Requests the current Tosh session to exit. | `exit` | `exit` |
| `clear` | Clears the terminal display. | `clear` | `clear` |
| `history` | Shows, searches, expands, runs, deletes, saves, reloads, or clears shell history. | `history [status\|path\|search <text>\|expand <spec>\|run <spec>\|delete <spec>\|save\|reload\|clear]` | `history search git` |
| `history-search` | Searches shell history entries by text. | `history-search <text>` | `history-search "ls"` |
| `config` | Gets or changes shell configuration. | `config [browse [query]\|get <path>\|set <path> [value]\|reset [path]\|init [path]\|reload]` | `config get display.style` |
| `view` | Gets or sets shell display preferences. | `view [compact\|detail\|datetime\|datetimeoffset\|dateonly\|timeonly\|timespan\|size\|permissions\|attributes\|columns]` | `view detail` |
| `source` | Executes a Tosh script file in the current session and lets it affect the caller scope. | `source <path> [arg…]` | `source ~/.config/tosh/profile.tosh` |
| `exec` | Replaces the current Tosh process with an external command. | `exec [--] <command> [arg …]` | `exec /bin/bash` |

---

## 2. Output & Text Rendering

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `echo` | Emits its arguments as pipeline objects. | `echo <value> [value…]` | `echo hello world` |
| `raw` | Emits plain text without rich table or record rendering. | `raw [value…]` | `echo 42 \| raw` |
| `write` | Writes rendered values without a trailing newline. | `write [value…]` | `write "hello "` |
| `writeline` | Writes rendered values with a trailing newline. | `writeline [value…]` | `writeline "done"` |
| `styled` | Creates a styled text segment with color and formatting. | `styled <text> [--fg color] [--bg color] [--bold] [--italic] [--underline] [--dim]` | `styled "WARNING" --fg red --bold` |
| `read-line` | Reads a line of text from standard input. | `read-line [prompt]` | `var name = read-line "Enter name: "` |
| `template` | Renders pipeline objects into text using `{{ member.path }}` placeholders. | `template <text>` | `stat file.txt \| template "Size: {{ Length }}"` |

---

## 3. Filesystem — Navigation & Inspection

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `pwd` | Returns the current directory as a DirectoryInfo object. | `pwd` | `pwd` |
| `cd` | Changes the current directory for the Tosh session. | `cd [path \| - \| +]` | `cd ~/projects` |
| `back` | Goes back to the previous directory in the stack. | `back` | `back` |
| `forward` | Goes forward to the next directory in the stack. | `forward` | `forward` |
| `dirs` | Shows or manages the directory stack. | `dirs [goto <index> \| remove <index> \| clear]` | `dirs` |
| `ls` | Lists file system entries. | `ls [-aAldRFihrSt] [-T [depth]] [--sort name\|size\|time] [--time modified\|access\|created] [--group-directories-first] [--tree [depth]] [--show columns] [--hide columns] [--show-all] [path …]` | `ls -la` |
| `stat` | Returns detailed metadata for one or more paths. | `stat [-L] [-f\|--filesystem] [--show columns] [--hide columns] [--show-all] <path> [path…]` | `stat /etc/hosts` |
| `tree` | Wraps the system tree utility, returning typed tree-entry objects. | `tree [-adfL level] [--show columns] [--hide columns] [--show-all] [path …]` | `tree -L 2` |
| `find` | Recursively finds file system entries. | `find [path …] [-name pattern] [-iname pattern] [-regex pattern] [-iregex pattern] [-type f\|d\|l] [-maxdepth n] [-mindepth n] [-size +/-size] [-mtime +/-days] [-newer-than duration] [-older-than duration] [-empty]` | `find . -name "*.cs" -type f` |
| `glob` | Expands filesystem glob patterns. | `glob [-a] <pattern> [pattern …]` | `glob "**/*.txt"` |
| `readlink` | Returns symbolic link targets. | `readlink [-f] <path> [path…]` | `readlink /usr/bin/python` |
| `realpath` | Returns fully resolved absolute paths. | `realpath <path> [path…]` | `realpath ../src` |
| `dirname` | Returns the directory portion of each path. | `dirname <path> [path…]` | `dirname /etc/hosts` |
| `basename` | Returns the file name portion of each path. | `basename <path> [suffix]` | `basename /etc/hosts` |
| `exists` | Checks whether each path exists. | `exists <path> [path…]` | `exists ~/.bashrc` |
| `is-file` | Checks whether each path is a file. | `is-file <path> [path…]` | `is-file README.md` |
| `is-dir` | Checks whether each path is a directory. | `is-dir <path> [path…]` | `is-dir src` |
| `is-link` | Checks whether each path is a symbolic link. | `is-link <path> [path…]` | `is-link /usr/bin/python` |

---

## 4. Filesystem — Mutation

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `mkdir` | Creates one or more directories. | `mkdir [-p] <path> [path…]` | `mkdir -p src/lib` |
| `touch` | Creates files or updates access and modification timestamps. | `touch [-acm] [-d time\|-r file] [-c] <path> [path…]` | `touch newfile.txt` |
| `rm` | Removes files or directories. | `rm [-r] [-f] [-i] <path> [path…]` | `rm -rf build/` |
| `cp` | Copies a file or directory. | `cp [-r] [-f] [-n] [-p] [-u] <source> [source …] <destination>` | `cp -r src/ backup/` |
| `mv` | Moves or renames files and directories. | `mv [-nufTi] [-t directory] <source> [source …] <destination>` | `mv old.txt new.txt` |
| `chmod` | Changes file permission bits. | `chmod [-R] <mode> <path> [path…]` | `chmod 755 script.sh` |
| `chown` | Changes file owner and group. | `chown [-R] <owner>[:group] <path> [path…]` | `chown user:staff file.txt` |
| `ln` | Creates hard links or symbolic links. | `ln [-s] [-f] <target> <link-path>` | `ln -s /opt/app latest` |
| `mkdir-temp` | Creates a temporary directory and returns it as a file system entry. | `mkdir-temp [prefix]` | `var tmp = mkdir-temp` |
| `tempfile` | Creates a temporary file and returns it as a file system entry. | `tempfile [prefix] [extension]` | `var f = tempfile "log" ".txt"` |

---

## 5. File I/O — Reading & Writing

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `cat` | Reads one or more files or piped text sources and emits their contents. | `cat [-n\|-b] [-s] [-E] [-T] [-A] [path …\|-]` | `cat README.md` |
| `read-file` | Reads one or more files and returns each file as a single string value. | `read-file <path> [path…]` | `var txt = read-file config.json` |
| `read-lines` | Reads one or more files and emits their contents line-by-line. | `read-lines <path> [path…]` | `read-lines log.txt \| grep ERROR` |
| `read-bytes` | Reads one or more files and returns each file as a byte array. | `read-bytes <path> [path…]` | `read-bytes image.png` |
| `write-file` | Writes plain text to a file, replacing any previous contents. | `write-file <path> [value…]` | `echo "hello" \| write-file out.txt` |
| `append-file` | Appends plain text to a file, creating it when needed. | `append-file <path> [value…]` | `echo "log entry" \| append-file app.log` |
| `write-bytes` | Writes byte-oriented content to a file, replacing any previous contents. | `write-bytes <path> [bytes…]` | `write-bytes out.bin [0x48, 0x65]` |
| `lines` | Splits text input into individual lines. | `lines [text …]` | `read-file log.txt \| lines` |
| `as-file` | Materializes values into a temporary file and returns it as a file system entry. | `as-file [text\|json\|csv] [value …]` | `echo "data" \| as-file text` |

---

## 6. Managed File Handles (Stream I/O)

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `open-file` | Opens one or more files as managed text or binary handles. | `open-file [--read\|--write\|--append] [--binary] [--encoding <name>] <path> [path…]` | `var h = open-file --write out.txt` |
| `close` | Closes one or more managed file handles. | `close <handle> [handle…]` | `close $h` |
| `read-from` | Reads a text or binary chunk from an open managed file handle. | `read-from [handle] [count]` | `read-from $h 100` |
| `read-line-from` | Reads the next text line from an open managed file handle. | `read-line-from [handle]` | `read-line-from $reader` |
| `read-to-end` | Reads the remainder of an open managed file handle. | `read-to-end [handle]` | `read-to-end $reader` |
| `write-to` | Writes plain text or bytes to an open managed file handle. | `write-to <handle> [value…]` | `write-to $h "hello"` |
| `write-line-to` | Writes one or more text lines to an open managed text file handle. | `write-line-to <handle> [value…]` | `write-line-to $writer "line 1"` |
| `flush` | Flushes one or more managed file handles. | `flush <handle> [handle…]` | `flush $writer` |
| `seek` | Moves an open managed file handle to a new position and returns the handle for continued piping. | `seek [handle] <offset> [begin\|current\|end]` | `echo $h \| seek 0 begin` |
| `position` | Returns the current stream position for one or more managed file handles. | `position [handle …]` | `position $writer` |
| `length` | Returns the current stream length for one or more managed file handles. | `length [handle …]` | `length $writer` |
| `copy-to` | Copies the remaining contents of one managed file handle into another compatible handle. | `copy-to [source] <target>` | `copy-to $src $dst` |

---

## 7. System & Process

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `uname` | Returns kernel and operating system information as a Tosh object. | `uname [-a\|-s\|-n\|-r\|-v\|-m\|-o]` | `uname -a` |
| `hostname` | Returns the current host name. | `hostname` | `hostname` |
| `whoami` | Returns the current user principal. | `whoami` | `whoami` |
| `id` | Returns current user and group identity information. | `id [-u\|-g\|-G] [-n]` | `id -u` |
| `env` | Lists, queries, sets, or unsets environment variables. Runs nested commands with temporary environment changes. | `env [name …] \| env [-u name] [name=value …] \| env [-u name] [name=value …] -- <command …>` | `env HOME` |
| `uptime` | Returns system uptime and load averages. | `uptime` | `uptime` |
| `free` | Returns system memory and swap usage. | `free` | `free` |
| `ps` | Lists running processes as Tosh process objects. | `ps [-eAf] [-p pid[,pid…]] [--ppid pid[,pid…]] [-u user[,user…]] [-t tty[,tty…]] [-o columns] [--sort field[,field…]] [--show columns] [--hide columns] [--show-all] [name-or-id …]` | `ps -ef` |
| `jobs` | Lists Tosh background jobs. | `jobs` | `jobs` |
| `wait-for` | Waits for one or more Tosh background jobs to finish. | `wait-for [job-id …]` | `wait-for 1` |
| `kill` | Stops a Tosh background job or operating-system process. | `kill <job-id\|pid> [job-id\|pid …]` | `kill 1234` |
| `signal` | Sends a signal to a Tosh background job or operating-system process. | `signal <signal> <job-id\|pid> [job-id\|pid …]` | `signal SIGTERM 1234` |
| `sleep` | Pauses execution for a duration. | `sleep <duration> [duration…]` | `sleep 2s` |
| `ping` | Pings a host and returns typed reply objects. | `ping [-c count] [-W timeout-ms] <host>` | `ping -c 4 google.com` |

---

## 8. Disk & Filesystem Usage

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `df` | Returns mounted file system usage information. | `df [-hTl] [-t type[,type…]] [-x type[,type…]] [--total] [--output columns] [--show columns] [--hide columns] [--show-all] [path …]` | `df -h` |
| `mounts` | Alias for `df`. | *(same as `df`)* | `mounts` |
| `du` | Returns disk usage information for files and directories. | `du [-a] [-s] [-d depth] [-h] [-c] [-x] [--time] [--show columns] [--hide columns] [--show-all] [path …]` | `du -sh *` |
| `usage` | Alias for `du`. | *(same as `du`)* | `usage -s .` |
| `disk-usage` | Alias for `du`. | *(same as `du`)* | `disk-usage -d 1` |
| `findmnt` | Wraps the system findmnt utility, returning typed mounted-filesystem objects. | `findmnt [-AflbR] [-S source] [-T target] [-M mountpoint] [-t types] [-O options] [-o columns] [path-or-device …]` | `findmnt -l` |

---

## 9. Systemd & Linux Utilities

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `systemctl` | Wraps systemctl, returning typed unit rows and structured unit property sets. | `systemctl [list-units [pattern …] \| list-unit-files [pattern …] \| show <unit …> \| status <unit …> \| <other …>]` | `systemctl list-units --type service` |
| `journalctl` | Wraps journalctl, returning typed journal-entry objects. | `journalctl [-n count] [-u unit] [--since when] [--until when] [query …]` | `journalctl -n 20 -u sshd` |
| `loginctl` | Wraps loginctl, returning typed session/user/seat rows. | `loginctl [list-sessions \| list-users \| list-seats \| show-session <id …> \| show-user <user …> \| show-seat <seat …> \| <other …>]` | `loginctl list-sessions` |
| `hostnamectl` | Wraps hostnamectl, returning a structured host-status object. | `hostnamectl [status \| <other-command …>]` | `hostnamectl status` |
| `networkctl` | Wraps networkctl, returning typed network-link rows. | `networkctl [list [pattern …] \| <other-command …>]` | `networkctl list` |
| `lsblk` | Wraps the system lsblk utility, returning typed block-device objects. | `lsblk [-aAdbflmpStzDNv] [-I list] [-e list] [-x column] [-o columns] [device …]` | `lsblk` |
| `lscpu` | Wraps lscpu, returning typed CPU summary, topology, and cache objects. | `lscpu [-B] [--hierarchic when] \| lscpu -e [options] [-o columns] \| lscpu -C [-B] [-o columns]` | `lscpu` |
| `lsfd` | Wraps lsfd, returning typed open-file-descriptor objects. | `lsfd [-l] [-p pid[,pid…]] [-i[4\|6]] [-o columns] [--summary[=only\|append\|never]]` | `lsfd -p $$` |
| `lsipc` | Wraps lsipc, returning structured IPC resource and limit records. | `lsipc [-m\|-M\|-q\|-Q\|-s\|-S] [-g] [-i id\|-N name] [-c] [-t] [-b] [-P] [-o columns]` | `lsipc -m` |
| `ip` | Wraps the system ip utility, returning typed objects for JSON-backed subcommands. | `ip [addr\|link\|route [filter …]] \| ip <other-subcommand …>` | `ip addr` |

---

## 10. Text Manipulation

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `head` | Returns the first objects or first text lines. | `head [-n count] [-c bytes] [path …]` | `head -n 5 file.txt` |
| `tail` | Returns the last objects or last text lines. | `tail [-n count] [-c bytes] [-f] [path …]` | `tail -n 10 log.txt` |
| `wc` | Counts lines, words, bytes, characters, and longest-line length. | `wc [-lwmcL] [path …]` | `wc -l *.cs` |
| `uniq` | Collapses adjacent duplicate input values. | `uniq [-c] [-i] [path …]` | `sort names.txt \| uniq -c` |
| `cut` | Extracts character or delimited fields from text. | `cut (-f fields [-d delimiter] \| -c chars) [path …]` | `cut -f 1 -d ":" /etc/passwd` |
| `tr` | Translates or deletes characters in text. | `tr [-d] <set1> [set2] [path …]` | `echo "HELLO" \| tr "A-Z" "a-z"` |
| `grep` | Searches text input with a regular expression or literal pattern. | `grep [-i] [-v] [-F] [-n] [-r] [-w] [-o] [-c] [-l] [-L] [-A num] [-B num] [-C num] <pattern\|regex> [path …]` | `grep -rn "TODO" src/` |
| `split` | Splits text values into smaller text values. | `split [-r] [-i] [-m] [-s] [-x] [--explicit-capture] [delimiter\|regex] [text …]` | `echo "a,b,c" \| split ","` |
| `join-lines` | Joins input values into a single text value. | `join-lines [separator]` | `echo a b c \| join-lines ", "` |
| `replace` | Replaces text in each input value. | `replace [-i] [-m] [-s] [-x] [--explicit-capture] [-r] <pattern\|regex> <replacement> [text …]` | `echo "hello" \| replace "l" "r"` |
| `match` | Matches text with a regular expression and returns shell record objects. | `match [-i] [-m] [-s] [-x] [--explicit-capture] <pattern\|regex> [text …]` | `echo "2024-01-15" \| match "(?<y>\d{4})-(?<m>\d{2})"` |
| `parse` | Parses text input with a regular expression into shell record objects. | `parse [-a] [-i] [-m] [-s] [-x] [--explicit-capture] <pattern\|regex> [text …]` | `ps -ef \| parse "(\S+)\s+(\S+)"` |
| `xargs` | Builds command invocations from text input. | `xargs [-n count] <command> [fixed-arg …]` | `find . -name "*.log" \| xargs rm` |

---

## 11. Structured Data — Parsing & Serialization

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `from-json` | Parses JSON text into CLR values and projected objects. | `from-json [json-text]` | `cat data.json \| from-json` |
| `from-csv` | Parses CSV text into shell row objects. | `from-csv [csv-text]` | `cat data.csv \| from-csv` |
| `from-tsv` | Parses TSV text into shell row objects. | `from-tsv [tsv-text]` | `cat data.tsv \| from-tsv` |
| `from-xml` | Parses XML text into CLR XDocument values. | `from-xml [xml-text]` | `cat config.xml \| from-xml` |
| `to-json` | Serializes pipeline values into JSON text. | `to-json [-c\|--compact] [value …]` | `ls \| to-json` |
| `to-csv` | Serializes pipeline values into CSV text. | `to-csv [value …]` | `ps \| to-csv` |
| `hash` | Computes hashes for text input or files. | `hash [algorithm] [path …]` | `hash sha256 file.bin` |

---

## 12. Pipeline Shaping & Selection

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `get` | Gets a member, projects fields, or retrieves an item by index. | `get <member-path> or get { <member-path>, … } or get <index>` | `ls \| get Name` |
| `select` | Alias for `get`. | *(same as `get`)* | `ps \| select Name,Memory` |
| `pick` | Alias for `get`. | *(same as `get`)* | `ls \| pick { Name, Length }` |
| `rename` | Renames fields on record-like objects. | `rename <old> <new> [old2 new2 …]` | `ls \| rename Name FileName` |
| `inspect` | Inspects piped CLR objects and returns their shape and preview data. | `inspect [-a]` | `date now \| inspect` |
| `where` | Filters pipeline objects with a predicate block or callable. | `where <predicate-expression\|callable>` | `ls \| where Length > 1000` |
| `first` | Returns the first object or first N objects from the pipeline. | `first [count]` | `ls \| first 5` |
| `last` | Returns the last object or last N objects from the pipeline. | `last [count]` | `history \| last 10` |
| `skip` | Skips the first object or first N objects from the pipeline. | `skip [count]` | `seq 10 \| skip 3` |
| `sort` | Sorts the current pipeline objects. | `sort [-r\|--reverse] [-n\|--numeric] [-u\|--unique] [-h\|--human-numeric] [member-path\|callable\|block]` | `ls \| sort Length` |
| `sort-by` | Alias for `sort`. | *(same as `sort`)* | `ps \| sort-by Memory` |
| `reverse` | Reverses the order of the current pipeline objects. | `reverse` | `seq 5 \| reverse` |
| `count` | Counts the number of objects in the current pipeline. | `count` | `ls \| count` |
| `collect` | Collects all pipeline items into a single list. | `collect` | `seq 5 \| collect` |
| `flatten` | Explicitly expands enumerable pipeline values by one level. | `flatten` | `echo [1, 2] [3, 4] \| flatten` |
| `distinct` | Removes duplicate pipeline values. | `distinct [member-path]` | `echo a b a c \| distinct` |
| `group-by` | Groups pipeline values by a member path, block, or callable. | `group-by <member-path\|callable\|block>` | `ls \| group-by Extension` |
| `transpose` | Pivots rows into columns. Each input record's keys become column headers and values become rows. | `transpose` | `df \| first \| transpose` |
| `tee` | Passes values through while also writing them out or capturing them. | `tee [-a] [path] or tee -v <name>` | `ls \| tee listing.txt \| count` |
| `ignore` | Consumes and discards all pipeline input. | `… \| ignore` | `rm -r tmp/ \| ignore` |

---

## 13. Pipeline Iteration & Transformation

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `each` | Executes a block or callable once for each input object. | `each <callable\|block>` | `seq 3 \| each { _ * 2 }` |
| `foreach` | Alias for `each`. | *(same as `each`)* | `ls \| foreach { echo _.Name }` |
| `map` | Transforms each pipeline value with a callable value or block. | `map <callable\|block>` | `seq 5 \| map { _ * _ }` |
| `filter` | Filters pipeline values with a callable value or block predicate. | `filter <callable\|block>` | `seq 10 \| filter { _ % 2 == 0 }` |
| `reduce` | Folds the current pipeline into one value using a seed and callable value or block. | `reduce <seed> <callable\|block>` | `seq 5 \| reduce 0 { $acc + _ }` |
| `scan` | Like reduce but yields every intermediate accumulator value. | `scan <seed> <callable\|block>` | `[1, 2, 3, 4] \| scan 0 { $acc + _ }` |
| `flat-map` | Transforms each pipeline value with a callable or block then flattens the results. | `flat-map <callable\|block>` | `[1, 2, 3] \| flat-map { [_, (_ * 10)] }` |
| `zip` | Merges two sequences pairwise. | `zip <other-sequence> [callable\|block]` | `[1, 2, 3] \| zip $b` |
| `take-while` | Yields input values while the predicate remains true. | `take-while <predicate-expression\|callable>` | `seq 10 \| take-while { _ < 5 }` |
| `skip-while` | Skips input values while the predicate remains true. | `skip-while <predicate-expression\|callable>` | `seq 10 \| skip-while { _ < 5 }` |
| `take-until` | Yields input values until the predicate becomes true. | `take-until <predicate-expression\|callable>` | `seq 10 \| take-until { _ > 7 }` |
| `skip-until` | Skips input values until the predicate becomes true. | `skip-until <predicate-expression\|callable>` | `seq 10 \| skip-until { _ > 3 }` |
| `partition` | Splits pipeline values into two lists based on a predicate: [matches, non-matches]. | `partition <callable\|block>` | `seq 10 \| partition { _ > 5 }` |
| `find-index` | Returns the 0-based index of the first pipeline value matching the predicate, or -1 if none match. | `find-index <callable\|block>` | `echo a b c \| find-index { _ == "b" }` |
| `chunk` | Groups pipeline items into fixed-size batches. | `chunk <size>` | `seq 10 \| chunk 3` |
| `window` | Yields sliding windows of a given size over the pipeline. | `window <size> [callable\|block]` | `seq 5 \| window 3` |
| `group-while` | Groups consecutive items while the predicate holds. | `group-while <callable\|block>` | `[1, 2, 5, 6, 10] \| group-while { _ - $prev < 3 }` |
| `interleave` | Alternates items from the pipeline with items from another sequence. | `interleave <other-sequence>` | `[1, 2, 3] \| interleave [10, 20, 30]` |

---

## 14. Pipeline Quantifiers

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `any` | Returns true if any pipeline value matches the predicate. | `any <callable\|block>` | `seq 5 \| any { _ > 3 }` |
| `all` | Returns true if every pipeline value matches the predicate. | `all <callable\|block>` | `seq 5 \| all { _ > 0 }` |
| `none` | Returns true if no pipeline values match the predicate. | `none <callable\|block>` | `seq 5 \| none { _ > 10 }` |

---

## 15. Aggregation & Statistics

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `sum` | Sums numeric, storage size, or timespan values. | `sum [member-path]` | `seq 5 \| sum` |
| `average` | Averages numeric, storage size, or timespan values. | `average [member-path]` | `seq 10 \| average` |
| `avg` | Alias for `average`. | *(same as `average`)* | `seq 10 \| avg` |
| `min` | Returns the minimum pipeline value. | `min [member-path]` | `seq 10 \| min` |
| `max` | Returns the maximum pipeline value. | `max [member-path]` | `ls \| max Length` |
| `frequencies` | Counts occurrences of each distinct value in the pipeline. | `frequencies` | `echo a b a c b a \| frequencies` |
| `summarize` | Computes structured summary objects for requested aggregates over the pipeline. | `summarize [--sum [columns]] [--avg [columns]] [--min [columns]] [--max [columns]] [--count [columns]]` | `df \| summarize --sum Size,Used` |
| `summary` | Alias for `summarize`. | *(same as `summarize`)* | `ps \| summary --avg Memory` |

---

## 16. Date, Time & Identifiers

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `date` | Creates, parses, and adjusts date/time values. | `date [-d\|--date-only] [-t\|--time-only] <now\|utc-now\|today\|tomorrow\|yesterday\|parse\|from-unix\|from-unix-ms\|<iso-date>> … or <date> \| date <add\|sub> …` | `date now` |
| `timespan` | Parses a duration into a CLR duration value. | `timespan <duration>` | `timespan "2h30m"` |
| `guid` | Creates, parses, formats, and inspects GUID values. | `guid [new [v4\|v7]\|empty\|parse\|format <d\|n\|b\|p\|x>\|info] [value …]` | `guid new v7` |
| `seq` | Generates a numeric sequence. | `seq <stop> \| seq <start> <stop> \| seq <start> <step> <stop>` | `seq 1 2 10` |

---

## 17. Variables, Scope & Module Management

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `vars` | Lists all visible variables in the current scope. | `vars [filter]` | `vars` |
| `which` | Resolves built-in commands and external executables. | `which <name …>` | `which ls` |
| `whence` | Alias for `which`. | *(same as `which`)* | `whence grep` |
| `export` | Exports a Tosh value to the process environment. | `export <name> [value]` | `export PATH "/usr/local/bin:$PATH"` |
| `forget` | Removes Tosh variables, functions, and exported environment names. | `forget <name> [name…]` | `forget myvar` |
| `unset` | Alias for `forget`. | *(same as `forget`)* | `unset TEMP_VAR` |
| `raise` | Raises an event, invoking all registered handlers. | `raise <event>` | `raise on-prompt` |
| `events` | Lists, inspects, and manages event handlers. | `events [list\|names\|handlers <event>\|remove <event> <handler>\|clear <event>]` | `events list` |
| `assert` | Asserts that a condition is true; throws a diagnostic error if it is false. | `assert <predicate> [message]` | `assert (1 + 1 == 2)` |

---

## 18. Functional Programming

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `invoke` | Invokes a callable value such as a lambda or function object. | `invoke <callable> [arg …]` | `invoke $fn 42` |
| `partial` | Binds leading arguments to a callable and returns a new callable value. | `partial <callable> [arg …]` | `var add5 = partial $add 5` |
| `curry` | Converts a fixed-arity callable into a curried callable value. | `curry <callable>` | `var c = curry $add` |
| `compose` | Composes two or more callables into a single callable that chains them left-to-right. | `compose <callable1> <callable2> [callable …]` | `var inc_double = compose $inc $double` |
| `unfold` | Generates values from a seed by repeatedly applying a callable. Returns `[value, next-state]` pairs or null to stop. | `unfold <seed> <callable\|block>` | `unfold 1 { [_, _ * 2] } \| first 5` |
| `iterate` | Generates an infinite sequence by repeatedly applying a callable to the previous result, starting from a seed. | `iterate <seed> <callable\|block>` | `iterate 1 { _ * 2 } \| first 10` |
| `converge` | Applies a callable to the seed repeatedly until two consecutive results are equal, then yields that stable value. | `converge <seed> <callable\|block>` | `converge 100.0 { _ / 2 }` |

---

## 19. Object Reflection & Manipulation

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `type-of` | Returns the CLR type or Tosh class type for each input object. | `type-of [value…]` | `echo 42 \| type-of` |
| `describe-type` | Describes CLR types, Tosh named types, or shell collection types. | `describe-type [type …]` | `describe-type Int32` |
| `members` | Lists public members for CLR types or pipeline objects. | `members [type …]` | `date now \| members` |
| `methods` | Lists public methods for CLR types or pipeline objects. | `methods [type …]` | `"hello" \| methods` |
| `constructors` | Lists constructors for CLR types, Tosh classes, and shell collection types. | `constructors [type …]` | `constructors DateTime` |
| `types` | Lists available CLR and Tosh shell types. | `types [-a] [filter]` | `types File` |
| `load-assembly` | Loads a .NET assembly from disk into the current process. | `load-assembly <path> [path…]` | `load-assembly MyLib.dll` |
| `has-prop` | Checks whether an object has the named property. | `has-prop [object] <name>` | `stat file.txt \| has-prop Length` |
| `has-method` | Checks whether an object or Tosh class has the named method. | `has-method [object] <name>` | `"hello" \| has-method ToUpper` |
| `get-props` | Lists property names for an object. | `get-props [object]` | `stat file.txt \| get-props` |
| `get-methods` | Lists method names for an object or Tosh class. | `get-methods [object]` | `date now \| get-methods` |
| `get-prop` | Gets a property value by dynamic name. | `get-prop [object] <name>` | `stat file.txt \| get-prop Length` |
| `set-prop` | Sets or adds a property on a dynamic record. | `set-prop [object] <name> <value>` | `set-prop $record Age 30` |
| `del-prop` | Removes a property from a dynamic record. | `del-prop [object] <name>` | `del-prop $record Temp` |
| `call-method` | Invokes a method by dynamic name. | `call-method [object] <method-name> [args…]` | `"hello" \| call-method ToUpper` |
| `clone` | Creates a shallow copy of an object. | `clone [object]` | `clone $record` |
| `cast` | Casts pipeline values to a CLR type, including constructed generic collection types. | `cast <type> [value …]` | `echo "42" \| cast Int32` |
| `new` | Constructs a new CLR object, Tosh named type, or shell collection. | `new <type-name> [ctor-args…]` | `new DateTime 2024 1 1` |
| `call` | Invokes an instance or static CLR method. | `call <method-name> [args…] or call <type-name> <method-name> [args…]` | `stat file.txt \| call OpenText` |

---

## 20. Native Interop (Unmanaged Memory)

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `native-alloc` | Allocates a native unmanaged buffer by byte size or interop type. | `native-alloc <bytes \| type-name>` | `var buf = native-alloc 256` |
| `alloc` | Alias for `native-alloc`. | *(same as `native-alloc`)* | `var buf = alloc 1024` |
| `native-free` | Frees one or more native buffers allocated by native-alloc. | `native-free [buffer …]` | `native-free $buf` |
| `native-read` | Reads a C string, byte range, or native scalar/struct-layout value from native memory. | `native-read <cstring\|bytes\|type-name> [buffer\|pointer] [length] [offset]` | `native-read bytes $buf 8` |
| `read-buffer` | Alias for `native-read`. | *(same as `native-read`)* | `read-buffer cstring $buf` |
| `native-write` | Writes a C string, byte sequence, or struct-layout value into native memory. | `native-write <buffer\|pointer> <value> [offset]` | `native-write $buf "hello"` |
| `write-buffer` | Alias for `native-write`. | *(same as `native-write`)* | `write-buffer $buf [0x41, 0x42]` |
| `native-size-of` | Returns the unmanaged size of a supported native interop type. | `native-size-of <type-name> [type-name …]` | `native-size-of Int32` |
| `size-of` | Alias for `native-size-of`. | *(same as `native-size-of`)* | `size-of IntPtr` |
| `native-offset-of` | Returns the unmanaged field offset for a sequential or explicit-layout struct. | `native-offset-of <type-name>[.<field-name>] [field-name]` | `native-offset-of MyStruct.Field1` |
| `offset-of` | Alias for `native-offset-of`. | *(same as `native-offset-of`)* | `offset-of MyStruct.X` |

---

## 21. Prompt Segments

These commands are used in prompt configuration to build styled prompt segments.

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `prompt-time` | Returns the current time as a styled prompt segment. | `prompt-time [--fg color] [--bg color] [--bold] [--dim] [--format pattern]` | `prompt-time --fg cyan` |
| `prompt-dir` | Returns the current directory as a styled prompt segment. | `prompt-dir [--fg color] [--bg color] [--bold] [--depth n]` | `prompt-dir --depth 2` |
| `prompt-git` | Returns git branch and status as styled prompt segments. | `prompt-git [--fg color] [--bg color] [--bold]` | `prompt-git` |
| `prompt-userhost` | Returns the current user and host as a styled prompt segment. | `prompt-userhost [--fg color] [--bg color] [--bold] [--dim]` | `prompt-userhost` |
| `prompt-history` | Returns the next history id as a styled prompt segment. | `prompt-history [id] [--fg color] [--bg color] [--bold] [--dim]` | `prompt-history` |
| `prompt-jobs` | Returns the current background job count as a styled prompt segment. | `prompt-jobs [count] [--fg color] [--bg color] [--bold] [--dim]` | `prompt-jobs` |
| `prompt-duration` | Returns the last command duration as a styled prompt segment. | `prompt-duration [duration] [--fg color] [--bg color] [--bold] [--dim] [--threshold-ms value]` | `prompt-duration --threshold-ms 500` |
| `prompt-exit` | Returns the last non-zero exit code as a styled prompt segment. | `prompt-exit [code] [--fg color] [--bg color] [--bold] [--dim]` | `prompt-exit` |
| `prompt-text` | Returns literal text as a styled prompt segment. | `prompt-text <text> [--fg color] [--bg color] [--bold] [--dim]` | `prompt-text "$ "` |
| `prompt-newline` | Inserts a line break in the prompt. | `prompt-newline` | `prompt-newline` |

---

## 22. TUI (Terminal User Interface)

| Command | Description | Signature | Example |
|---------|-------------|-----------|---------|
| `tui` | Interactive TUI components for scripts. Provides list pickers, confirmations, text input, file pickers, and custom screens. Use `--cli` for inline (non-fullscreen) prompts. | `tui pick\|confirm\|input\|file\|filter\|screen\|add-list\|add-text\|add-input\|add-picker\|layout\|run [options]` | `tui pick "Choose:" a b c` |

---

## Unregistered / Dormant Commands

These command classes exist in the source tree but are **not** registered in `BuiltInCommands.RegisterDefaults()`:

| Command Class | Shell Name | Description |
|---------------|-----------|-------------|
| `UndefCommand` | `undef` | Removes user-defined functions. |

---

## Command Registration Aliases Summary

Several command classes are registered under multiple shell names:

| Primary | Aliases |
|---------|---------|
| `df` | `mounts` |
| `du` | `usage`, `disk-usage` |
| `get` | `select`, `pick` |
| `each` | `foreach` |
| `sort` | `sort-by` |
| `average` | `avg` |
| `summarize` | `summary` |
| `which` | `whence` |
| `forget` | `unset` |
| `native-alloc` | `alloc` |
| `native-read` | `read-buffer` |
| `native-write` | `write-buffer` |
| `native-size-of` | `size-of` |
| `native-offset-of` | `offset-of` |

---

## Totals

| Category | Unique Command Classes | Registered Names (incl. aliases) |
|----------|----------------------|----------------------------------|
| Discovery, Help & Session | 10 | 10 |
| Output & Text Rendering | 7 | 7 |
| Filesystem — Navigation & Inspection | 16 | 16 |
| Filesystem — Mutation | 10 | 10 |
| File I/O — Reading & Writing | 9 | 9 |
| Managed File Handles (Stream I/O) | 12 | 12 |
| System & Process | 14 | 14 |
| Disk & Filesystem Usage | 4 | 6 |
| Systemd & Linux Utilities | 9 | 9 |
| Text Manipulation | 14 | 14 |
| Structured Data — Parsing & Serialization | 7 | 7 |
| Pipeline Shaping & Selection | 17 | 19 |
| Pipeline Iteration & Transformation | 16 | 17 |
| Pipeline Quantifiers | 3 | 3 |
| Aggregation & Statistics | 6 | 8 |
| Date, Time & Identifiers | 4 | 4 |
| Variables, Scope & Module Management | 8 | 10 |
| Functional Programming | 7 | 7 |
| Object Reflection & Manipulation | 18 | 18 |
| Native Interop | 6 | 12 |
| Prompt Segments | 10 | 10 |
| TUI | 1 | 1 |
| **TOTAL** | **~198 unique classes** | **~223 registered names** |

> Note: Exact counts include the `source` command registered by ToshEngine and 4 `PathPredicateCommand` instances (`exists`, `is-file`, `is-dir`, `is-link`).
