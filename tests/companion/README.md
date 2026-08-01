# Companion page tests

Headless tests for `NGUAdvisorCompanion/wwwroot/index.html`. The page is the entire companion UI —
one file, five `<script>` blocks — and none of it was covered by the xunit suite, which links only
Unity-free C#. These run the **real page** under jsdom.

```
cd tests/companion
npm install
npm test                       # dev tree
node test-loadouts.js "../../../NGUAdvisor-public/NGUAdvisorCompanion/wwwroot/index.html"
```

Exit code is non-zero if anything fails, so it drops straight into CI or a pre-port check.

## What it covers

The Gear › Loadouts surface: the arming-gate switches and their status lines, the objective
dropdowns, the Main/idle block, `Fill from objective`, and the auto-fill on objective change.

## Two things it does that are worth keeping

**It boots the page the way WebView2 does.** The live layer registers its snapshot listener at
script-execution time via `window.chrome.webview.addEventListener("message", …)`, and an earlier
IIFE early-returns unless `window.chrome.webview` exists. So the stub is installed in jsdom's
`beforeParse` hook — installing it after `new JSDOM(...)` is too late and the page silently renders
its design-time content forever, which looks exactly like a hang.

**It cross-checks the page against the injector.** Every `[data-setting]` key the page emits is
verified to exist in `UiBridge.BindingList`, parsed out of `UiBridge.cs`. That boundary has no
compiler behind it: a key the page writes but the injector doesn't bind falls through to a
`LogDebug("unknown key")` and the control silently does nothing, forever. This is the only test in
the repo that can catch it.

## Notes

- jsdom needs a `matchMedia` stub; `index.html` calls it unguarded (fine in WebView2).
- The page installs 1 Hz intervals, so the harness exits explicitly rather than waiting for the event
  loop to drain, and wraps its assertions so a throw reports instead of looking like a hang.
- Assertions run against a synthetic snapshot. For visual work, judge at **1346 × 1184** with a real
  snapshot pulled off the pipe instead — see `docs/COMPANION_UI.md`.
