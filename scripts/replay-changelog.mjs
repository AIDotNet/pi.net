// Replay corpus vectors against the real v1.5.2 TypeScript getNewEntries semantics (#11703).
// Staged verbatim from v1.5.2:packages/coding-agent/src/utils/changelog.ts lines 173-198.
import fs from "node:fs";

function compareVersions(v1, v2) {
  if (v1.major !== v2.major) return v1.major - v2.major;
  if (v1.minor !== v2.minor) return v1.minor - v2.minor;
  return v1.patch - v2.patch;
}

function getNewEntries(entries, lastVersion) {
  const parts = lastVersion.trim().match(/^v?(\d+)\.(\d+)\.(\d+)/);
  if (!parts) return undefined;
  const last = {
    major: Number(parts[1]),
    minor: Number(parts[2]),
    patch: Number(parts[3]),
    content: "",
  };
  return entries.filter((entry) => compareVersions(entry, last) > 0);
}

const path = process.argv[2];
const corpus = JSON.parse(fs.readFileSync(path, "utf-8"));
const out = corpus.getNewChangelogEntries.map((v) => {
  const result = getNewEntries(v.entries, v.lastVersion);
  return {
    lastVersion: v.lastVersion,
    entries: v.entries,
    expected: result === undefined ? null : result.map((e) => e.content),
  };
});

// Verify the v1.5.0 replay reproduces the OLD expected values (proves the harness is faithful
// to the prior TS semantics for well-formed versions, and isolates which vectors change).
function oldCompare(v1, v2) {
  const a = [v1.major || 0, v1.minor || 0, v1.patch || 0];
  const b = [v2.major || 0, v2.minor || 0, v2.patch || 0];
  if (a[0] !== b[0]) return a[0] - b[0];
  if (a[1] !== b[1]) return a[1] - b[1];
  return a[2] - b[2];
}
function toNumber(part) {
  const trimmed = part.trim();
  if (trimmed === "") return 0;
  const n = Number(trimmed);
  return Number.isNaN(n) || n === 0 ? 0 : n;
}
function oldGetNewEntries(entries, lastVersion) {
  const parts = lastVersion.split(".");
  const last = {
    major: parts.length > 0 ? toNumber(parts[0]) : 0,
    minor: parts.length > 1 ? toNumber(parts[1]) : 0,
    patch: parts.length > 2 ? toNumber(parts[2]) : 0,
    content: "",
  };
  return entries.filter((e) => oldCompare(e, last) > 0).map((e) => e.content);
}

let baselineOk = true;
for (const v of corpus.getNewChangelogEntries) {
  const replay = oldGetNewEntries(v.entries, v.lastVersion);
  if (JSON.stringify(replay) !== JSON.stringify(v.expected)) baselineOk = false;
}
console.log("v1.5.0 baseline replay matches stored expected:", baselineOk);

// Add explicit #11703 regression vectors from upstream changelog.test.ts.
const entries152 = [
  { major: 0, minor: 1, patch: 5, content: "0.1.5" },
  { major: 0, minor: 1, patch: 4, content: "0.1.4" },
];
const regression = [
  "0.1.5-20261020.7",
  "0.1.5+build.3",
  "0.1.4-beta.1",
  "0.1.5",
  "0.1.3",
  "latest",
  "0.1",
  "",
].map((lastVersion) => ({
  lastVersion,
  entries: entries152,
  expected: (() => {
    const r = getNewEntries(entries152, lastVersion);
    return r === undefined ? null : r.map((e) => e.content);
  })(),
}));

fs.writeFileSync(
  process.argv[3],
  JSON.stringify({ baselineOk, vectors: out, regression }, null, 2),
  "utf-8",
);
for (const v of out) {
  console.log(`  ${JSON.stringify(v.lastVersion).padEnd(20)} -> ${JSON.stringify(v.expected)}`);
}
console.log("--- #11703 regression ---");
for (const v of regression) {
  console.log(`  ${JSON.stringify(v.lastVersion).padEnd(20)} -> ${JSON.stringify(v.expected)}`);
}
