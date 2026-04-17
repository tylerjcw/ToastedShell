# Common ToSh Tasks

Practical ToSh scripts that range from simple shell usage to more involved automation.

Run them with `tosh --no-startup` while you are iterating:

```bash
tosh --no-startup ./examples/common_tasks/01_hello_and_context.tosh
tosh --no-startup ./examples/common_tasks/02_recent_files_report.tosh . 15 3d
tosh --no-startup ./examples/common_tasks/03_todo_report.tosh .
tosh --no-startup ./examples/common_tasks/04_extension_inventory.tosh . 12
tosh --no-startup ./examples/common_tasks/05_backup_recent_files.tosh . ./tmp/backups 12h
```

Scripts:

- `01_hello_and_context.tosh`: greet the user, show the current directory, and summarize the top level.
- `02_recent_files_report.tosh`: list recently modified files and total size under a tree.
- `03_todo_report.tosh`: scan common project files for `TODO`-style markers.
- `04_extension_inventory.tosh`: count file extensions and show the largest files.
- `05_backup_recent_files.tosh`: copy recently changed files into a backup directory and write a JSON manifest.
