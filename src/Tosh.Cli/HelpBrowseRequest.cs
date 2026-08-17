// The TUI request type, aliased so call sites read as a shell concept rather than a
// namespace path. This alias lived in Tosh.Runtime until `TOAST-0006`, where it was one
// of only two things giving the runtime — and so, transitively, the language — a
// dependency on Tosh.Tui.
global using HelpBrowseRequest = Tosh.Tui.Requests.HelpBrowseRequest;
