// jsdom harness for the Gear > Inventory keep/trash readout (the restored InventoryAdvisorPanel).
// Runs the REAL index.html, fakes the WebView2 host, feeds synthetic snapshots down the same path
// MainForm uses, and asserts on the DOM plus the OUTBOUND doAction traffic:
//   - the persist block exists (heads, lists, age chip, recompute button)
//   - no verdict yet -> empty state; opening the view auto-requests computeInventory exactly once
//   - a verdict snapshot fills both lists, splits trailing "  [tag]" runs into faint spans,
//     shows counts in the heads and the chapter chip
//   - a FRESH verdict suppresses the auto-request on re-open (the guard, not just the rate limit,
//     is what keeps a 1 Hz snapshot page from stacking 30-optimization sweeps on the injector)
//   - the recompute button fires the doAction through the page's delegated [data-action] handler
//
// Usage: node test-invverdict.js [path-to-index.html]
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
vc.on("jsdomError", e => { console.log("JSDOM ERROR: " + e.message); });

const SENT = [];
let hostListener = null;
const dom = new JSDOM(html, {
  runScripts: "dangerously",
  pretendToBeVisual: true,
  virtualConsole: vc,
  beforeParse(w) {
    w.chrome = {
      webview: {
        postMessage(s) { try { SENT.push(JSON.parse(s)); } catch (e) { SENT.push({ raw: s }); } },
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
function txt(id) { const el = $(id); return el ? el.textContent.trim() : null; }
function sentActions() { return SENT.filter(m => m && m.cmd === "doAction" && m.action === "computeInventory"); }
function nav(view) {
  const b = window.document.querySelector('[data-view="' + view + '"]');
  if (!b) throw new Error("no nav item for view " + view);
  b.click();
}

function baseSnapshot(overrides) {
  const s = { v: 1, settings: { GlobalEnabled: true, ManageInventory: true }, systems: {}, action: "Default", nextLoopSec: 5 };
  if (overrides) for (const k of Object.keys(overrides)) s[k] = overrides[k];
  return s;
}

window.addEventListener("load", guard(() => {
  // --- the persist block exists ---------------------------------------------------------------
  ok("keep list container exists", !!$("invKeepList"));
  ok("trash list container exists", !!$("invTrashList"));
  ok("keep head exists", !!$("invKeepHead"));
  ok("trash head exists", !!$("invTrashHead"));
  ok("age chip exists and starts unset", txt("invVerdictAge") === "Not computed yet", txt("invVerdictAge"));
  const btn = window.document.querySelector('[data-action="computeInventory"]');
  ok("recompute button exists", !!btn);

  // --- no verdict: empty state, and opening the view auto-requests a compute -------------------
  send(baseSnapshot());
  ok("no-verdict age chip unchanged", txt("invVerdictAge") === "Not computed yet", txt("invVerdictAge"));
  ok("no-verdict keep list shows the empty state", /No verdict yet/.test(txt("invKeepList") || ""), txt("invKeepList"));

  ok("no auto-request before the view opens", sentActions().length === 0, JSON.stringify(sentActions()));
  nav("inventory");
  ok("opening Inventory with no verdict requests a compute", sentActions().length === 1, "sent " + sentActions().length);

  // --- a verdict snapshot fills the readout ----------------------------------------------------
  send(baseSnapshot({
    invVerdict: {
      at: "1000",
      agoSec: 5,
      chapter: "Ch.5 Evil-IDP",
      keep: [
        { id: 91,  n: "The Sands of Time  [guide: cooldown]" },
        { id: 118, n: "Stapler" },
        { id: 210, n: "Pissed Off Key  [chain]" }
      ],
      trash: [
        { id: 63, n: "Cloth Shirt" },
        { id: 114, n: "Office Shoes  [guide horizon passed]" }
      ]
    }
  }));
  ok("keep head counts", txt("invKeepHead") === "KEEP (3)", txt("invKeepHead"));
  ok("trash head counts", txt("invTrashHead") === "TRASH (2)", txt("invTrashHead"));
  ok("age chip reads fresh", txt("invVerdictAge") === "Computed just now", txt("invVerdictAge"));
  ok("chapter chip shows the guide chapter", txt("invVerdictChapter") === "Guide horizons: Ch.5 Evil-IDP", txt("invVerdictChapter"));
  ok("chapter chip is visible", $("invVerdictChapter").style.display !== "none");

  const keepRows = $("invKeepList").querySelectorAll(".inv-row");
  ok("keep list renders one row per item", keepRows.length === 3, String(keepRows.length));
  ok("row shows the item name", keepRows[0].querySelector(".nm").textContent.indexOf("The Sands of Time") === 0, keepRows[0].textContent);
  ok("trailing tag splits into its own faint span", (keepRows[0].querySelector(".tag") || {}).textContent === "[guide: cooldown]",
     keepRows[0].innerHTML);
  ok("untagged row has no tag span", !keepRows[1].querySelector(".tag"), keepRows[1].innerHTML);
  ok("row shows the item id", keepRows[0].querySelector(".iid").textContent === "#91", keepRows[0].innerHTML);
  const trashRows = $("invTrashList").querySelectorAll(".inv-row");
  ok("trash list renders one row per item", trashRows.length === 2, String(trashRows.length));
  ok("trash guide-horizon tag renders", (trashRows[1].querySelector(".tag") || {}).textContent === "[guide horizon passed]",
     trashRows[1].innerHTML);

  // --- a fresh verdict suppresses the auto-request on re-open ----------------------------------
  nav("overview");
  nav("inventory");
  ok("re-opening with a fresh verdict does NOT re-request", sentActions().length === 1, "sent " + sentActions().length);

  // --- staleness is re-stamped without a rebuild ------------------------------------------------
  send(baseSnapshot({ invVerdict: { at: "1000", agoSec: 720, chapter: "Ch.5 Evil-IDP", keep: [], trash: [] } }));
  ok("age chip re-stamps from agoSec", txt("invVerdictAge") === "Computed 12 min ago", txt("invVerdictAge"));
  ok("same `at` keeps the built lists (no wipe from the empty arrays)",
     $("invKeepList").querySelectorAll(".inv-row").length === 3,
     String($("invKeepList").querySelectorAll(".inv-row").length));

  // --- the recompute button goes through the delegated [data-action] handler -------------------
  btn.click();
  ok("recompute button fires the doAction", sentActions().length === 2, "sent " + sentActions().length);

  done();
}));
