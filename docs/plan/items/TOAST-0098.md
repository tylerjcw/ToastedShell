---
id: TOAST-0098
title: "`http serve` binds to loopback and leads with a URL that cannot work, so a LAN transfer fails twice before it works"
status: proposed
area: tosh
priority: 3
opened: 2026-08-29
---

## Problem

Transferring a file to another machine on the LAN failed twice in a row, for two separate
reasons, neither of which the command says anything about.

**First: the default bind is loopback.** `--bind` defaults to `127.0.0.1`, so
`http serve ./dir` is unreachable from anywhere else. From the other machine this presents as
**connection timed out** — the least informative failure available, because a dropped SYN looks
identical to a firewall drop or a wrong address. Nothing in the output says the server is
loopback-only or that `--lan` exists.

**Second: the handle leads with the URL that cannot work.** Even with `--lan`, the rendered
handle puts `Url` above `ShareUrl`:

```
│ Url       │ http://localhost:18080/                         │
│ ShareUrl  │ http://192.168.254.2:18080/?token=cosmic-beacon │
│ Bind      │ 0.0.0.0                                         │
```

`Url` is the first field the eye lands on and the only one that is useless off-machine. It also
omits the token, so copying it gives an unauthorised request even locally.

The default itself is right: a file server that silently exposes a directory to the whole LAN on
first use would be a worse default than one that makes you ask. The problem is entirely that
neither failure explains itself.

## Suggested fix

- When the bind address is loopback, say so on start and name `--lan`. One line, at the point
  the server is created, is enough to collapse the first failure.
- When `--lan` is set, lead with `ShareUrl` and demote or drop `Url`. The display profile for
  `HttpFileServerHandle` already orders these fields; it is a reordering, not new data.
- Consider including the token in `Url` too, or omitting `Url` entirely when a token is set,
  since a URL that 401s is not a useful thing to offer.

## Not in scope

The third failure in the same session was `ERR_SSL_PROTOCOL_ERROR`: the browser upgraded
`http://` to `https://` under HTTPS-First. That is browser behaviour and not something the
command can prevent — though a start-up line reading `http://…  (plain HTTP — not HTTPS)` would
have shortened the diagnosis.

## Verification

- `http serve ./dir` mentions `--lan`; `http serve ./dir --lan` does not.
- With `--lan`, the first URL in the rendered handle is the one another machine can reach.
- The existing `HttpFileServerHandle` tests still pass, and the display-profile change is
  covered by whichever test asserts that handle's columns.
