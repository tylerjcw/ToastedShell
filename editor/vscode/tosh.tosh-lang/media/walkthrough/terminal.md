# TōSh Terminal

Open a TōSh terminal from the **Terminal** menu → **New Terminal** → select **TōSh** from the profile dropdown (the shell selector icon ∨ next to the `+` button).

The terminal runs a full interactive TōSh session — pipelines, variables, functions, and your `profile.tosh` all work as normal.

```tosh
ls | where size > 1mb | sort-by size -d | first 10
```
