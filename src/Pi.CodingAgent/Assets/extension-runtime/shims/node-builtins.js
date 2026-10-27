// Node built-in module shims, registered into the host's module registry.
//
// The host evaluates this file once per extension engine, then serves
// `require("node:fs")` and friends from `__pi_builtins`. Everything that touches
// the real world goes through `__pi_call`, so the host keeps ownership of the
// file system, processes and environment.

"use strict";

var builtins = globalThis.__pi_builtins;

function call(method, payload) {
  return globalThis.__pi_call(method, payload);
}

// ---------------------------------------------------------------------- path

function hostPath(method, args) {
  return call("path." + method, { args: args });
}

var pathModule = {
  sep: call("path.sep", {}),
  delimiter: call("path.delimiter", {}),
  join: function () { return hostPath("join", Array.prototype.slice.call(arguments)); },
  resolve: function () { return hostPath("resolve", Array.prototype.slice.call(arguments)); },
  dirname: function (value) { return hostPath("dirname", [String(value)]); },
  basename: function (value, suffix) { return hostPath("basename", [String(value), suffix]); },
  extname: function (value) { return hostPath("extname", [String(value)]); },
  relative: function (from, to) { return hostPath("relative", [String(from), String(to)]); },
  isAbsolute: function (value) { return hostPath("isAbsolute", [String(value)]) === true; },
  normalize: function (value) { return hostPath("normalize", [String(value)]); },
  parse: function (value) { return hostPath("parse", [String(value)]); },
  format: function (value) { return hostPath("format", [value]); },
  toNamespacedPath: function (value) { return value; },
};
pathModule.posix = pathModule;
pathModule.win32 = pathModule;
builtins.set("node:path", pathModule);
builtins.set("path", pathModule);

// ------------------------------------------------------------------------ fs

function readFileSync(path, options) {
  var encoding = typeof options === "string" ? options : (options && options.encoding) || "utf8";
  var text = call("fs.readFile", { path: String(path), encoding: encoding });
  return text === null || text === undefined ? "" : String(text);
}

function writeFileSync(path, data, options) {
  var encoding = typeof options === "string" ? options : (options && options.encoding) || "utf8";
  call("fs.writeFile", {
    path: String(path),
    data: typeof data === "string" ? data : globalThis.Buffer.from(data).toString("utf8"),
    encoding: encoding,
  });
}

function statToResult(raw) {
  if (!raw) return raw;
  return {
    size: raw.size,
    mtimeMs: raw.mtimeMs,
    mtime: new Date(raw.mtimeMs),
    mode: raw.mode,
    isFile: function () { return raw.kind === "file"; },
    isDirectory: function () { return raw.kind === "directory"; },
    isSymbolicLink: function () { return raw.kind === "symlink"; },
  };
}

var fsModule = {
  readFileSync: readFileSync,
  writeFileSync: writeFileSync,
  appendFileSync: function (path, data, options) {
    var encoding = typeof options === "string" ? options : (options && options.encoding) || "utf8";
    call("fs.appendFile", { path: String(path), data: String(data), encoding: encoding });
  },
  existsSync: function (path) { return call("fs.exists", { path: String(path) }) === true; },
  statSync: function (path) { return statToResult(call("fs.stat", { path: String(path) })); },
  lstatSync: function (path) { return statToResult(call("fs.stat", { path: String(path) })); },
  readdirSync: function (path, options) {
    var entries = call("fs.readdir", { path: String(path) }) || [];
    if (options && options.withFileTypes) {
      return entries.map(function (entry) {
        return {
          name: entry.name,
          isFile: function () { return entry.kind === "file"; },
          isDirectory: function () { return entry.kind === "directory"; },
          isSymbolicLink: function () { return entry.kind === "symlink"; },
        };
      });
    }
    return entries.map(function (entry) { return entry.name; });
  },
  mkdirSync: function (path, options) {
    call("fs.mkdir", { path: String(path), recursive: !!(options && options.recursive) });
  },
  rmSync: function (path, options) {
    call("fs.rm", {
      path: String(path),
      recursive: !!(options && options.recursive),
      force: !!(options && options.force),
    });
  },
  unlinkSync: function (path) { call("fs.unlink", { path: String(path) }); },
  renameSync: function (from, to) { call("fs.rename", { from: String(from), to: String(to) }); },
  copyFileSync: function (from, to) { call("fs.copyFile", { from: String(from), to: String(to) }); },
  realpathSync: function (path) { return call("fs.realpath", { path: String(path) }); },
  mkdtempSync: function (prefix) { return call("fs.mkdtemp", { prefix: String(prefix) }); },
  constants: { F_OK: 0, R_OK: 4, W_OK: 2, X_OK: 1 },
};

fsModule.promises = {
  readFile: function (path, options) { return Promise.resolve(readFileSync(path, options)); },
  writeFile: function (path, data, options) {
    writeFileSync(path, data, options);
    return Promise.resolve();
  },
  appendFile: function (path, data, options) {
    fsModule.appendFileSync(path, data, options);
    return Promise.resolve();
  },
  stat: function (path) { return Promise.resolve(fsModule.statSync(path)); },
  readdir: function (path, options) { return Promise.resolve(fsModule.readdirSync(path, options)); },
  mkdir: function (path, options) {
    fsModule.mkdirSync(path, options);
    return Promise.resolve();
  },
  rm: function (path, options) {
    fsModule.rmSync(path, options);
    return Promise.resolve();
  },
  unlink: function (path) {
    fsModule.unlinkSync(path);
    return Promise.resolve();
  },
  rename: function (from, to) {
    fsModule.renameSync(from, to);
    return Promise.resolve();
  },
  copyFile: function (from, to) {
    fsModule.copyFileSync(from, to);
    return Promise.resolve();
  },
  realpath: function (path) { return Promise.resolve(fsModule.realpathSync(path)); },
  access: function (path) {
    return fsModule.existsSync(path) ? Promise.resolve() : Promise.reject(new Error("ENOENT: " + path));
  },
};

builtins.set("node:fs", fsModule);
builtins.set("fs", fsModule);
builtins.set("node:fs/promises", fsModule.promises);
builtins.set("fs/promises", fsModule.promises);

// ------------------------------------------------------------------------ os

var osModule = {
  EOL: call("os.eol", {}) || "\n",
  platform: function () { return call("os.platform", {}); },
  arch: function () { return call("os.arch", {}); },
  homedir: function () { return call("os.homedir", {}); },
  tmpdir: function () { return call("os.tmpdir", {}); },
  hostname: function () { return call("os.hostname", {}); },
  type: function () { return call("os.platform", {}); },
  release: function () { return ""; },
  cpus: function () { return []; },
  totalmem: function () { return 0; },
  freemem: function () { return 0; },
};
builtins.set("node:os", osModule);
builtins.set("os", osModule);

// ------------------------------------------------------------------- process

builtins.set("node:process", globalThis.process);
builtins.set("process", globalThis.process);

// -------------------------------------------------------------------- timers

builtins.set("node:timers", {
  setTimeout: globalThis.setTimeout,
  clearTimeout: globalThis.clearTimeout,
  setInterval: globalThis.setInterval,
  clearInterval: globalThis.clearInterval,
  setImmediate: globalThis.setImmediate,
  clearImmediate: globalThis.clearTimeout,
});
builtins.set("node:timers/promises", {
  setTimeout: function (delay) {
    return new Promise(function (resolve) {
      globalThis.setTimeout(resolve, delay || 0);
    });
  },
});

// ----------------------------------------------------------------------- url

builtins.set("node:url", {
  URL: globalThis.URL,
  URLSearchParams: globalThis.URLSearchParams,
  fileURLToPath: function (value) {
    var text = String(value);
    if (text.indexOf("file://") === 0) text = text.slice(7);
    return decodeURIComponent(text);
  },
  pathToFileURL: function (value) {
    var text = String(value).replace(/\\/g, "/");
    if (text.charAt(0) !== "/") text = "/" + text;
    return { href: "file://" + encodeURI(text), toString: function () { return "file://" + encodeURI(text); } };
  },
});

// -------------------------------------------------------------------- crypto

builtins.set("node:crypto", {
  randomUUID: function () { return call("crypto.randomUUID", {}); },
  randomBytes: function (size) {
    var hex = call("crypto.randomHex", { size: size || 16 });
    return globalThis.Buffer.from(hex, "hex");
  },
  randomInt: function (min, max) {
    var low = max === undefined ? 0 : min;
    var high = max === undefined ? min : max;
    return low + Math.floor(Math.random() * (high - low));
  },
  createHash: function () {
    throw new Error("crypto.createHash is not available in the .NET JavaScript extension host");
  },
});

// --------------------------------------------------------------------- util

function inspect(value) {
  if (typeof value === "string") return "'" + value + "'";
  try {
    return JSON.stringify(value, null, 2);
  } catch (_) {
    return String(value);
  }
}

builtins.set("node:util", {
  format: function () {
    var parts = [];
    for (var i = 0; i < arguments.length; i++) {
      var value = arguments[i];
      parts.push(typeof value === "string" ? value : inspect(value));
    }
    return parts.join(" ");
  },
  inspect: inspect,
  promisify: function (fn) {
    return function () {
      var args = Array.prototype.slice.call(arguments);
      var self = this;
      return new Promise(function (resolve, reject) {
        args.push(function (error, value) { return error ? reject(error) : resolve(value); });
        fn.apply(self, args);
      });
    };
  },
  inherits: function (ctor, superCtor) {
    ctor.prototype = Object.create(superCtor.prototype);
    ctor.prototype.constructor = ctor;
  },
  types: { isPromise: function (value) { return !!value && typeof value.then === "function"; } },
  TextEncoder: globalThis.TextEncoder,
  TextDecoder: globalThis.TextDecoder,
});

// ------------------------------------------------------------------- events

function EventEmitter() {
  this._events = new Map();
}

EventEmitter.prototype.on = function (name, listener) {
  var list = this._events.get(name) || [];
  list.push(listener);
  this._events.set(name, list);
  return this;
};
EventEmitter.prototype.addListener = EventEmitter.prototype.on;
EventEmitter.prototype.once = function (name, listener) {
  var self = this;
  function wrapper() {
    self.off(name, wrapper);
    listener.apply(undefined, arguments);
  }
  wrapper.listener = listener;
  return this.on(name, wrapper);
};
EventEmitter.prototype.off = function (name, listener) {
  var list = this._events.get(name);
  if (!list) return this;
  var filtered = list.filter(function (item) { return item !== listener && item.listener !== listener; });
  if (filtered.length === 0) this._events.delete(name);
  else this._events.set(name, filtered);
  return this;
};
EventEmitter.prototype.removeListener = EventEmitter.prototype.off;
EventEmitter.prototype.removeAllListeners = function (name) {
  if (name === undefined) this._events.clear();
  else this._events.delete(name);
  return this;
};
EventEmitter.prototype.emit = function (name) {
  var list = this._events.get(name);
  if (!list || list.length === 0) return false;
  var args = Array.prototype.slice.call(arguments, 1);
  list.slice().forEach(function (listener) { listener.apply(undefined, args); });
  return true;
};
EventEmitter.prototype.listeners = function (name) {
  return (this._events.get(name) || []).slice();
};
EventEmitter.prototype.listenerCount = function (name) {
  return (this._events.get(name) || []).length;
};
EventEmitter.prototype.setMaxListeners = function () { return this; };

var eventsModule = EventEmitter;
eventsModule.EventEmitter = EventEmitter;
builtins.set("node:events", eventsModule);
builtins.set("events", eventsModule);

// ------------------------------------------------------------- child_process

function spawnSync(command, args, options) {
  var result = call("childProcess.run", {
    command: String(command),
    args: args || [],
    cwd: (options && options.cwd) || undefined,
    env: (options && options.env) || undefined,
    input: (options && options.input) || undefined,
  });
  return {
    status: result.code,
    code: result.code,
    signal: null,
    stdout: result.stdout,
    stderr: result.stderr,
    output: [null, result.stdout, result.stderr],
    pid: result.pid,
  };
}

function execSync(command, options) {
  var result = spawnSync(command, [], options);
  if (result.status !== 0) {
    var error = new Error("Command failed: " + command + "\n" + result.stderr);
    error.status = result.status;
    error.stdout = result.stdout;
    error.stderr = result.stderr;
    throw error;
  }
  return result.stdout;
}

function spawn(command, args, options) {
  var result = spawnSync(command, args, options);
  var emitter = new EventEmitter();
  var child = {
    pid: result.pid,
    stdout: emitter,
    stderr: emitter,
    on: function (name, listener) { emitter.on(name, listener); return child; },
    once: function (name, listener) { emitter.once(name, listener); return child; },
    kill: function () {},
  };
  globalThis.queueMicrotask(function () {
    if (result.stdout) emitter.emit("data", result.stdout);
    emitter.emit("close", result.status);
  });
  return child;
}

builtins.set("node:child_process", {
  execSync: execSync,
  spawnSync: spawnSync,
  spawn: spawn,
  exec: function (command, options, callback) {
    var fn = typeof options === "function" ? options : callback;
    try {
      var stdout = execSync(command, typeof options === "function" ? undefined : options);
      if (fn) fn(null, stdout, "");
    } catch (error) {
      if (fn) fn(error, error.stdout || "", error.stderr || "");
    }
  },
});

// ------------------------------------------------------------------- buffer

builtins.set("node:buffer", { Buffer: globalThis.Buffer });
builtins.set("buffer", { Buffer: globalThis.Buffer });

// ------------------------------------------------------------------- module

builtins.set("node:module", {
  createRequire: function () { return globalThis.__pi_require; },
  builtinModules: [
    "fs", "path", "os", "url", "util", "events", "crypto", "child_process", "timers", "buffer", "zlib", "module", "readline",
  ],
  default: { createRequire: function () { return globalThis.__pi_require; } },
});

// --------------------------------------------------------------------- zlib

builtins.set("node:zlib", {
  inflateSync: function (data) {
    var encoded = globalThis.Buffer.from(data).toString("base64");
    var result = call("zlib.inflate", { data: encoded });
    return globalThis.Buffer.from(result, "base64");
  },
  deflateSync: function (data) {
    var encoded = globalThis.Buffer.from(data).toString("base64");
    var result = call("zlib.deflate", { data: encoded });
    return globalThis.Buffer.from(result, "base64");
  },
  gunzipSync: function (data) {
    var encoded = globalThis.Buffer.from(data).toString("base64");
    var result = call("zlib.gunzip", { data: encoded });
    return globalThis.Buffer.from(result, "base64");
  },
});

// ----------------------------------------------------------------- readline
//
// A terminal-backed readline needs a real stdin; the host has none in headless
// mode, so the shim answers with a never-emitting emitter and a question() that
// fails loudly instead of hanging.

builtins.set("node:readline", {
  createInterface: function () {
    var emitter = new EventEmitter();
    emitter.close = function () {};
    emitter.question = function (_prompt, callback) {
      if (callback) callback("");
    };
    emitter.setPrompt = function () {};
    emitter.prompt = function () {};
    emitter.write = function () {};
    return emitter;
  },
});

builtins.set("node:readline/promises", {
  createInterface: function () {
    var emitter = new EventEmitter();
    emitter.close = function () {};
    emitter.question = function () { return Promise.resolve(""); };
    return emitter;
  },
});

