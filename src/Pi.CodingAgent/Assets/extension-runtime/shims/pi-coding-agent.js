// Shim for `@earendil-works/pi-coding-agent` — the runtime values an extension
// imports from the host package. The `pi` object itself is built by the prelude
// (`__pi_make_api`), not here.

"use strict";

/**
 * `defineTool` is an identity helper that gives editors the generic parameter
 * types. At runtime the host only needs the plain tool definition.
 */
function defineTool(definition) {
  return definition;
}

function getAgentDir() {
  return globalThis.__pi_call("agentDir", {});
}

function getPackageDir() {
  return globalThis.__pi_call("packageDir", {});
}

var CONFIG_DIR_NAME = ".pi";

/** `keyHint` renders the configured key binding for a key id. */
function keyHint(keyId, description) {
  return description === undefined ? keyId : keyId + " " + description;
}

/**
 * Serializes file mutations per path so two extensions cannot interleave writes
 * to the same file. The host is single-threaded per extension; a simple chain
 * keeps the same ordering guarantee.
 */
var fileMutationQueues = new Map();

function withFileMutationQueue(path, mutate) {
  var fn = typeof path === "function" ? path : mutate;
  var key = typeof path === "function" ? "" : String(path);
  var previous = fileMutationQueues.get(key) || Promise.resolve();
  var next = previous.then(function () { return fn(); });
  fileMutationQueues.set(key, next.then(function () {}, function () {}));
  return next;
}

function getSettingsListTheme() {
  return {
    selected: function (text) { return text; },
    unselected: function (text) { return text; },
    description: function (text) { return text; },
    hint: function (text) { return text; },
  };
}

function unsupported(name) {
  return function () {
    throw new Error(name + " is not available in the .NET JavaScript extension host");
  };
}

/**
 * Built-in tool definitions. The upstream host exposes one factory per built-in
 * tool; each returns a definition whose execute runs the real tool through the
 * extension context, so wrapping the renderers keeps the behaviour.
 */
function builtinToolFactory(name, label, description) {
  return function () {
    return globalThis.__pi_builtin_tool(name, label, description);
  };
}

// ---------------------------------------------------------------- truncation
//
// `truncateHead` / `truncateTail` are the host's context-protection helpers: a
// tool result is cut to the first (or last) N lines and bytes, and the caller
// reports the omitted volume. The limits mirror the upstream defaults.

var DEFAULT_MAX_LINES = 2000;
var DEFAULT_MAX_BYTES = 50 * 1024;

function formatSize(bytes) {
  var value = Number(bytes) || 0;
  if (value < 1024) return value + "B";
  if (value < 1024 * 1024) return (value / 1024).toFixed(1) + "KB";
  return (value / (1024 * 1024)).toFixed(1) + "MB";
}

function byteLength(text) {
  return globalThis.Buffer.from(text).length;
}

function truncate(content, options, fromTail) {
  var text = content === undefined || content === null ? "" : String(content);
  var maxLines = (options && options.maxLines) || DEFAULT_MAX_LINES;
  var maxBytes = (options && options.maxBytes) || DEFAULT_MAX_BYTES;
  var lines = text.split("\n");
  var kept = fromTail ? lines.slice(Math.max(0, lines.length - maxLines)) : lines.slice(0, maxLines);
  var output = kept.join("\n");

  // Byte trimming used to walk one character at a time, which is quadratic on a
  // megabyte-sized tool result. Cut proportionally first, then converge: each pass
  // removes at least the measured excess, so this finishes in a handful of steps.
  if (byteLength(output) > maxBytes) {
    var ratio = maxBytes / byteLength(output);
    var chars = Math.max(1, Math.floor(output.length * ratio));
    output = fromTail ? output.slice(output.length - chars) : output.slice(0, chars);
    while (output.length > 0 && byteLength(output) > maxBytes) {
      var excess = byteLength(output) - maxBytes;
      var cut = Math.min(output.length, Math.max(1, excess));
      output = fromTail ? output.slice(cut) : output.slice(0, output.length - cut);
    }
  }

  return {
    content: output,
    truncated: output !== text,
    totalLines: lines.length,
    outputLines: output === "" ? 0 : output.split("\n").length,
    totalBytes: byteLength(text),
    outputBytes: byteLength(output),
  };
}

function truncateHead(content, options) {
  return truncate(content, options, false);
}

function truncateTail(content, options) {
  return truncate(content, options, true);
}

module.exports = {
  defineTool: defineTool,
  DEFAULT_MAX_LINES: DEFAULT_MAX_LINES,
  DEFAULT_MAX_BYTES: DEFAULT_MAX_BYTES,
  formatSize: formatSize,
  truncateHead: truncateHead,
  truncateTail: truncateTail,
  createBashTool: builtinToolFactory("bash", "Bash", "Run a shell command."),
  createBashToolDefinition: builtinToolFactory("bash", "Bash", "Run a shell command."),
  createReadTool: builtinToolFactory("read", "Read", "Read a file from disk."),
  createReadToolDefinition: builtinToolFactory("read", "Read", "Read a file from disk."),
  createWriteTool: builtinToolFactory("write", "Write", "Write a file to disk."),
  createWriteToolDefinition: builtinToolFactory("write", "Write", "Write a file to disk."),
  createEditTool: builtinToolFactory("edit", "Edit", "Apply an edit to a file."),
  createEditToolDefinition: builtinToolFactory("edit", "Edit", "Apply an edit to a file."),
  createGrepTool: builtinToolFactory("grep", "Grep", "Search file contents."),
  createGrepToolDefinition: builtinToolFactory("grep", "Grep", "Search file contents."),
  createFindTool: builtinToolFactory("find", "Find", "Find files by name."),
  createFindToolDefinition: builtinToolFactory("find", "Find", "Find files by name."),
  createLsTool: builtinToolFactory("ls", "Ls", "List a directory."),
  createLsToolDefinition: builtinToolFactory("ls", "Ls", "List a directory."),
  getAgentDir: getAgentDir,
  getPackageDir: getPackageDir,
  CONFIG_DIR_NAME: CONFIG_DIR_NAME,
  keyHint: keyHint,
  withFileMutationQueue: withFileMutationQueue,
  getSettingsListTheme: getSettingsListTheme,
  CustomEditor: unsupported("CustomEditor"),
  registerApiProvider: unsupported("registerApiProvider"),
};
