#!/usr/bin/env node
// Generates docs/models-configuration.md from the published models.json JSON Schema.
//
// Upstream (packages/coding-agent/scripts/generate-references.ts) renders this page from the TypeBox
// schemas in model-config.ts and groups the compatibility fields by API. The port publishes one
// flattened schema instead (src/Pi.CodingAgent/Assets/schemas/models.schema.json), so this generator
// renders one compatibility table. Everything else - headings, intros, table columns, type/default
// notation, required markers - mirrors the upstream page.
//
// Usage: node scripts/gen-models-configuration.mjs [--check]
//   --check  exit 1 when docs/models-configuration.md is out of date (for CI)

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, "..");
const schemaPath = resolve(root, "src/Pi.CodingAgent/Assets/schemas/models.schema.json");
const outputPath = resolve(root, "docs/usage/models-configuration.md");

const schema = JSON.parse(readFileSync(schemaPath, "utf8"));
const defs = schema.$defs ?? {};
const providerSchema = Object.values(schema.properties.providers.patternProperties)[0];
const modelSchema = providerSchema.properties.models.items;
const modelOverrideSchema =
  Object.values(providerSchema.properties.modelOverrides.patternProperties ?? {})[0] ??
  providerSchema.properties.modelOverrides;
const inputLimitsSchema = defs.ModelInputLimits;
const compatSchema = defs.ProviderCompat;

/** Follow a local `$ref`; leaves anything else alone. */
const deref = (node) => {
  let at = node ?? {};
  while (at.$ref && at.$ref.startsWith("#/$defs/")) {
    at = defs[at.$ref.slice("#/$defs/".length)] ?? {};
  }
  return at;
};

/** A JSON Schema value as the single-line type text upstream renders, without the code ticks. */
const typeText = (node) => {
  const at = deref(node);
  if (at.const !== undefined) return JSON.stringify(at.const);
  if (at.enum) return `(${at.enum.map((value) => JSON.stringify(value)).join(" \\| ")})`;
  if (at.anyOf) return at.anyOf.map((member) => typeText(member)).join(" \\| ");
  if (at.type === "array") {
    const inner = at.items ? typeText(at.items) : "unknown";
    return `${inner.startsWith("(") || inner.includes(" \\| ") ? inner : inner}[]`;
  }
  if (Array.isArray(at.type)) return at.type.join(" \\| ");
  return at.type ?? "unknown";
};

// Table cells: escape pipes, and angle brackets so a description like `<thinking>` is not read as HTML.
const descriptionOf = (node) =>
  (deref(node).description ?? node?.description ?? "")
    .replaceAll("|", "\\|")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;");

const defaultText = (node) => {
  const at = deref(node);
  if (!Object.hasOwn(at, "default")) return "";
  return `\`${JSON.stringify(at.default)}\``;
};

/**
 * One table for an object schema. `links` renders an object field as a link to another section
 * instead of expanding it; `expand` lists the object fields whose children become rows.
 */
const table = (objectSchema, { links = {}, expand = new Set() } = {}) => {
  const at = deref(objectSchema);
  const required = new Set(at.required ?? []);
  const lines = ["| Field | Type | Default | Description |", "|---|---|---|---|"];

  const walk = (name, node, prefix, requiredHere, expandChildren) => {
    const field = prefix ? `${prefix}.${name}` : name;
    const target = deref(node);
    const link = prefix ? undefined : links[name];
    const isObject = target.type === "object" || target.properties;
    const isObjectArray = target.type === "array" && deref(target.items).properties;
    // Upstream renders scalars bare and every composite type in code ticks; links follow the same
    // split (an object link bare, an object[] link in ticks).
    const text = typeText(node);
    const composite =
      text.includes("[]") || text.startsWith("(") || text.startsWith('"') || text.includes(" \| ");
    const type = link
      ? isObjectArray
        ? `[\`object[]\`](${link})`
        : `[object](${link})`
      : composite
        ? `\`${text}\``
        : text;
    const description = descriptionOf(node);
    lines.push(
      `| \`${field}\` | ${type} | ${defaultText(node)} | ${requiredHere ? "Required. " : ""}${description} |`,
    );

    if (!expandChildren) return;
    if (isObject) {
      for (const [childName, child] of Object.entries(target.properties)) {
        walk(childName, child, field, (target.required ?? []).includes(childName), expand.has(`${field}.${childName}`));
      }
    } else if (isObjectArray) {
      const items = deref(target.items);
      for (const [childName, child] of Object.entries(items.properties)) {
        walk(`${childName}`, child, `${field}[]`, (items.required ?? []).includes(childName),
          expand.has(`${field}[].${childName}`));
      }
    }
  };

  for (const [name, node] of Object.entries(at.properties ?? {})) {
    walk(name, node, "", required.has(name), expand.has(name));
  }

  return lines.join("\n");
};

const header = [
  "# Models Configuration",
  "",
  "<!-- Generated from src/Pi.CodingAgent/Assets/schemas/models.schema.json. Do not edit by hand; run `node scripts/gen-models-configuration.mjs`. -->",
  "",
  "This reference lists the fields of `models.json`, generated from the JSON Schema the port publishes.",
  "",
  'Add `"$schema": "https://pi.dev/schemas/models.schema.json"` to `models.json` for editor validation and completion.',
  "",
  "",
].join("\n");

const sections = [
  [
    "## Providers",
    "",
    "Each key under `providers` is a provider ID. A built-in provider ID changes that provider. Any other ID adds a provider, which needs `baseUrl` and an `api` at provider or model level.",
    "",
    table(providerSchema, {
      links: { models: "#models", modelOverrides: "#model-overrides", compat: "#compatibility" },
    }),
  ],
  [
    "## Models",
    "",
    "Each `models` entry adds a model to the provider. An entry whose `id` matches an existing model replaces it.",
    "",
    table(modelSchema, {
      links: { inputLimits: "#input-limits", compat: "#compatibility" },
      expand: new Set(["cost", "cost.tiers"]),
    }),
  ],
  [
    "## Model overrides",
    "",
    "Each `modelOverrides` key is the ID of a built-in or extension model. Unknown IDs are ignored. Object fields, such as `cost`, merge with the model's value field by field; other fields replace it.",
    "",
    table(modelOverrideSchema, {
      links: { inputLimits: "#input-limits", compat: "#compatibility" },
      expand: new Set(["cost", "cost.tiers"]),
    }),
  ],
  [
    "## Input limits",
    "",
    "Describes the input a model accepts: the largest provider request and the images it takes, including the resize profile applied before a new image enters conversation history.",
    "",
    table(inputLimitsSchema, { expand: new Set(["images", "images.resize"]) }),
  ],
  [
    "## Compatibility",
    "",
    "Provider and model compatibility overrides. The port publishes one schema for every API, so this table lists the union: a field only affects the APIs that read it, and an unsupported field is ignored rather than rejected.",
    "",
    table(compatSchema),
  ],
];

const document = `${header}${sections.map((section) => section.join("\n")).join("\n\n")}\n`;

if (process.argv.includes("--check")) {
  let current = "";
  try {
    current = readFileSync(outputPath, "utf8");
  } catch {
    // Missing counts as out of date.
  }
  if (current !== document) {
    console.error("docs/usage/models-configuration.md is out of date; run node scripts/gen-models-configuration.mjs");
    process.exit(1);
  }
  console.log("docs/usage/models-configuration.md is up to date");
} else {
  writeFileSync(outputPath, document);
  console.log(`wrote ${outputPath} (${document.split("\n").length - 1} lines)`);
}
