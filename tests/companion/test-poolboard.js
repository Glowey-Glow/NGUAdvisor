// jsdom harness for "Where the pool went" (renderPools) — now with the R3 chip beside Energy/Magic.
//
// The bug class this file exists for is already recorded in the page: the board CACHES the last real
// payload because the injector gates each pool on its OWN allocator signature, and an earlier version
// cached that payload WHOLESALE — so a tick carrying { seq, energy } deleted magic, which is most
// ticks, because the pools re-plan independently. R3 makes that sharper, not softer: its plan only
// changes when a hack's disposition does, so on a save whose hacks are all hard-capped the r3 key is
// absent from essentially every payload and a wholesale cache would delete it hardest.
//
// It also covers the two states the R3 board has to survive:
//   - an OLDER INJECTOR with a NEWER PAGE: no `r3` key at all, and nothing may throw;
//   - a pool whose every lane was refused: the lane list draws nothing (the renderer keeps seated
//     lanes and non-zero takes), so the pool-level message is the only thing that can say what
//     happened, and a blank panel there is no better than the silence it replaced.
//
// Usage: node test-poolboard.js [path-to-index.html]
const fs = require("fs");
const path = require("path");
const { JSDOM, VirtualConsole } = require("jsdom");

const FILE = process.argv[2] || path.resolve(__dirname, "../../NGUAdvisorCompanion/wwwroot/index.html");

let pass = 0, fail = 0;
const failures = [];
function ok(name, cond, detail) {
  if (cond) { pass++; }
  else { fail++; failures.push(name + (detail ? "  --> " + detail : "")); }
}

const html = fs.readFileSync(FILE, "utf8");
const vc = new VirtualConsole();
const jsdomErrors = [];
vc.on("jsdomError", e => { jsdomErrors.push(e.message); console.log("JSDOM ERROR: " + e.message); });

let hostListener = null;
const dom = new JSDOM(html, {
  runScripts: "dangerously",
  pretendToBeVisual: true,
  virtualConsole: vc,
  beforeParse(w) {
    // The live layer registers its snapshot listener at script-execution time and an earlier IIFE
    // early-returns unless window.chrome.webview exists, so the stub has to be installed here —
    // after `new JSDOM(...)` is too late and the page renders design-time content forever.
    w.chrome = {
      webview: {
        postMessage() {},
        addEventListener(type, fn) { if (type === "message") hostListener = fn; },
        removeEventListener() {}
      }
    };
    if (!w.matchMedia) w.matchMedia = () => ({ matches: false, addListener(){}, removeListener(){}, addEventListener(){}, removeEventListener(){} });
    w.scrollTo = w.scrollTo || function () {};
  }
});
const { window } = dom;
if (!window.Element.prototype.scrollIntoView) window.Element.prototype.scrollIntoView = function () {};

function done(code) {
  console.log("\n" + "=".repeat(60));
  console.log(FILE);
  console.log("PASS " + pass + "   FAIL " + fail);
  if (failures.length) { console.log("\nFAILURES:"); failures.forEach(f => console.log("  - " + f)); }
  console.log("=".repeat(60));
  try { dom.window.close(); } catch (e) {}
  process.exit(code != null ? code : (fail ? 1 : 0));
}
const HARD_STOP = setTimeout(() => { console.log("\n!! harness timed out before finishing"); done(1); }, 45000);
HARD_STOP.unref && HARD_STOP.unref();
function guard(fn) { return function () { try { return fn.apply(null, arguments); } catch (e) { console.log("HARNESS THREW: " + (e && e.stack || e)); done(1); } }; }

function send(snapshot) {
  if (!hostListener) throw new Error("the page never registered a webview message listener");
  hostListener({ data: snapshot });
}
function $(id) { return window.document.getElementById(id); }
function board() { return $("poolBoard"); }
function poolCards() {
  return Array.prototype.slice.call(board().querySelectorAll(".pb-pool"));
}
function poolNames() { return poolCards().map(c => c.querySelector("h3").textContent.trim()); }
function cardFor(name) { return poolCards().filter(c => c.querySelector("h3").textContent.trim() === name)[0]; }
function laneNames(name) {
  const c = cardFor(name);
  if (!c) return [];
  return Array.prototype.slice.call(c.querySelectorAll(".pb-lane .pb-nm")).map(e => e.textContent.trim());
}
function base(pools) { return { v: 1, settings: {}, systems: {}, pools: pools }; }

function pool(o) {
  return Object.assign({ pool: 1000, unallocated: 0, sinkSeated: true, lanes: [] }, o);
}
function lane(o) {
  return Object.assign({ name: "?", seated: true, sink: false, took: 0, offered: 0 }, o);
}

const ENERGY = pool({
  pool: 1000, unallocated: 100,
  lanes: [lane({ name: "CAPNGU-0", took: 600, offered: 900 }),
          lane({ name: "Wandoos", sink: true, took: 300, offered: 400 })]
});
const MAGIC = pool({
  pool: 500, unallocated: 50,
  lanes: [lane({ name: "CAPMNGU-1", took: 450, offered: 500 })]
});
const R3 = pool({
  pool: 2000, unallocated: 200,
  lanes: [lane({ name: "CAPHACK-0", took: 1000, offered: 2000 }),
          lane({ name: "Wishes", sink: true, took: 800, offered: 1000 })]
});

window.addEventListener("load", guard(() => {
  ok("the board host exists", !!board());

  // --- an older injector: no `pools` node at all -------------------------------------------------
  send(base(undefined));
  ok("no pools node -> the waiting line, no throw",
     /Waiting for the first allocation pass/.test(board().textContent), board().textContent.trim());

  // --- an older injector with the two-pool payload a newer page must still render ---------------
  send(base({ seq: 1, energy: ENERGY, magic: MAGIC }));
  ok("r3 absent from the payload does not throw", jsdomErrors.length === 0, jsdomErrors.join(" | "));
  ok("energy and magic render on their own", poolNames().join(",") === "Energy,Magic", poolNames().join(","));
  ok("no R3 card is invented", !cardFor("R3"));

  // --- r3 arrives ------------------------------------------------------------------------------
  send(base({ seq: 2, r3: R3 }));
  ok("R3 renders in the fixed pool order", poolNames().join(",") === "Energy,Magic,R3", poolNames().join(","));
  ok("R3 lanes render", laneNames("R3").join(",") === "CAPHACK-0,Wishes,idle remainder", laneNames("R3").join(","));
  ok("the wish lane takes the sink tag Wandoos takes on Energy",
     (cardFor("R3").querySelectorAll(".pb-tag.sink").length === 1), cardFor("R3").innerHTML);
  ok("R3 total is formatted by pbFmt", cardFor("R3").querySelector(".pb-tot").textContent.trim() === "2.0K",
     cardFor("R3").querySelector(".pb-tot").textContent);
  ok("R3 idle share is drawn hatched like the other pools",
     cardFor("R3").querySelectorAll(".pb-seg.idle").length === 1, cardFor("R3").innerHTML);

  // --- THE MERGE. This is the bug class the page's own comment records. -------------------------
  send(base({ seq: 3, energy: ENERGY }));
  ok("energy-only tick keeps magic AND r3", poolNames().join(",") === "Energy,Magic,R3", poolNames().join(","));
  send(base({ seq: 4, r3: R3 }));
  ok("r3-only tick keeps energy AND magic", poolNames().join(",") === "Energy,Magic,R3", poolNames().join(","));
  send(base({ seq: 5, magic: MAGIC }));
  ok("magic-only tick keeps energy AND r3", poolNames().join(",") === "Energy,Magic,R3", poolNames().join(","));
  send(base({ seq: 6 }));
  ok("a liveness-only tick keeps all three", poolNames().join(",") === "Energy,Magic,R3", poolNames().join(","));

  // --- an r3-only tick must not disturb what the other two are showing --------------------------
  const energyLanesBefore = laneNames("Energy").join(",");
  send(base({ seq: 7, r3: Object.assign({}, R3, { pool: 4000, unallocated: 400 }) }));
  ok("energy lanes survive an r3 update intact", laneNames("Energy").join(",") === energyLanesBefore,
     laneNames("Energy").join(","));
  ok("r3 total updates", cardFor("R3").querySelector(".pb-tot").textContent.trim() === "4.0K",
     cardFor("R3").querySelector(".pb-tot").textContent);

  // --- the idle alarm is the same threshold for R3 as for the other two -------------------------
  send(base({ seq: 8, r3: pool({ pool: 1000, unallocated: 400, sinkSeated: false,
    idleWhy: "STRANDED: no hack token in the timeline can take R3",
    lanes: [lane({ name: "Wishes", sink: true, took: 600, offered: 600 })] }) }));
  ok("40% idle trips the loss block", !!cardFor("R3").querySelector(".pb-loss"), cardFor("R3").innerHTML);
  ok("the loss block names the pool and quotes idleWhy",
     /40\.0% of the r3 pool went nowhere/.test(cardFor("R3").textContent) &&
     /STRANDED/.test(cardFor("R3").textContent), cardFor("R3").textContent.trim());

  send(base({ seq: 9, r3: pool({ pool: 1000, unallocated: 20,
    lanes: [lane({ name: "Wishes", sink: true, took: 980, offered: 1000 })] }) }));
  ok("2% idle stays under the alarm", !cardFor("R3").querySelector(".pb-loss"), cardFor("R3").innerHTML);
  ok("under the alarm it reads as headroom", /2\.0% left idle/.test(cardFor("R3").textContent),
     cardFor("R3").textContent.trim());

  // --- the bench save: every hack refused, so there are NO hack rows to draw --------------------
  // The renderer keeps seated lanes and non-zero takes, so fifteen refused-and-empty lanes produce
  // nothing at all. Without the pool-level message this panel is blank, which is exactly the silence
  // the board was built to end.
  send(base({ seq: 10, r3: pool({
    pool: 482273592059025, unallocated: 0, sinkSeated: true,
    budget: "no R3 was allocated this pass: all 15 hack token(s) in the timeline failed IsValid()",
    lanes: [
      lane({ name: "CAPHACK-0", seated: false, took: 0, why: "at its hard cap, level 6600" }),
      lane({ name: "CAPHACK-1", seated: false, took: 0, why: "at its hard cap, level 6600" }),
      lane({ name: "Wishes", sink: true, took: 482273592059025, offered: 482273592059025 })
    ] }) }));
  ok("refused-and-empty hack lanes draw no rows", laneNames("R3").join(",") === "Wishes",
     laneNames("R3").join(","));
  ok("the pool-level message is displayed instead of a blank panel",
     /no R3 was allocated this pass/.test(cardFor("R3").textContent), cardFor("R3").textContent.trim());
  ok("the pool total still reads the whole R3 bank",
     cardFor("R3").querySelector(".pb-tot").textContent.trim() === "482.27T",
     cardFor("R3").querySelector(".pb-tot").textContent);

  // --- a seated lane's `why` reaches the operator ------------------------------------------------
  // The injector now sends `why` for seated lanes too, and this is where it earns its keep: a hack
  // holding a real share of R3 below the float stall floor converts none of it and would otherwise be
  // an ordinary coloured segment. The row grid has no space for prose, so it rides the tooltip that
  // already carries offered/took.
  send(base({ seq: 11, r3: pool({ pool: 1000, unallocated: 0,
    lanes: [lane({ name: "CAPHACK-9", took: 1000, offered: 1000,
                   why: 'holding 1.00K at 1.49e-8 progress/tick, below the 2^-25 float stall floor — the game\'s "Time Until Next Level" still shows a number' })] }) }));
  const pct = cardFor("R3").querySelector(".pb-lane .pb-pct");
  ok("the stall reason rides the existing offered/took tooltip",
     /stall floor/.test(pct.getAttribute("title") || ""), pct.getAttribute("title"));
  ok("the tooltip still carries offered and took",
     /offered 1\.0K, took 1\.0K/.test(pct.getAttribute("title") || ""), pct.getAttribute("title"));
  // A double quote in the reason must not break out of the title attribute — esc() escapes only
  // &, < and > because every other caller writes into an element body.
  ok("a quoted reason does not escape the attribute",
     /Time Until Next Level/.test(pct.getAttribute("title") || "") &&
     cardFor("R3").querySelectorAll(".pb-lane").length === 1,
     cardFor("R3").innerHTML);

  // --- an r3 payload with no lanes at all must not throw ----------------------------------------
  send(base({ seq: 12, r3: { pool: 0, unallocated: 0 } }));
  ok("an r3 node with no lanes array does not throw", jsdomErrors.length === 0, jsdomErrors.join(" | "));

  done();
}));
