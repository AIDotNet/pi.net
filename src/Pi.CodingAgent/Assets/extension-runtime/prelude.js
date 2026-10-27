// ============================================================================
// Pi .NET JavaScript extension host — prelude
// ============================================================================
//
// Loaded into every extension engine before any extension module. It provides:
//
//   - the globals a Node-hosted extension expects (console / process / Buffer /
//     timers / URL / TextEncoder),
//   - the callback registry the host uses to call back into JavaScript,
//   - `__pi_make_api`, which builds the `pi` object an extension's default
//     export receives. Every method turns into one `__piHost.call(...)` round
//     trip, so the host owns all state and the JavaScript side stays stateless.
//
// Everything here is plain ES5-ish on purpose: the file is evaluated by Jint
// directly (no transpile step), and it must not rely on host-specific syntax.

(function () {
  "use strict";

  var host = globalThis.__piHost;

  // ------------------------------------------------------------------ errors

  globalThis.__pi_call_error = function (message) {
    var error = new Error(message);
    error.name = "PiHostError";
    error.__pi_host_error = true;
    return error;
  };

  globalThis.__pi_describe = function (value) {
    if (value === null || value === undefined) return String(value);
    if (typeof value === "string") return value;
    if (value instanceof Error) return value.name + ": " + value.message;
    try {
      return JSON.stringify(value);
    } catch (_) {
      return String(value);
    }
  };

  // -------------------------------------------------------- callback registry

  var callbacks = new Map();
  var nextCallbackId = 0;

  globalThis.__pi_register_callback = function (fn) {
    if (typeof fn !== "function") throw new TypeError("Expected a function");
    var id = String(++nextCallbackId);
    callbacks.set(id, fn);
    return id;
  };

  globalThis.__pi_release_callback = function (id) {
    callbacks.delete(String(id));
  };

  globalThis.__pi_invoke_callback = function (id, args) {
    var fn = callbacks.get(String(id));
    if (fn === undefined) throw new Error("Unknown extension callback " + id);
    return fn.apply(undefined, args === undefined || args === null ? [] : args);
  };

  // ---------------------------------------------------------------- host call

  globalThis.__pi_call = function (method, payload) {
    var argsJson = payload === undefined ? "" : JSON.stringify(payload);
    var raw = host.call(String(method), argsJson === undefined ? "" : argsJson);
    if (raw === null || raw === undefined || raw === "") return undefined;
    return JSON.parse(raw);
  };

  // ---------------------------------------------------------------- timers
  //
  // Extensions expect a real event loop. The host drives it: after every
  // callback it fires due timers and pumps promise continuations, so a timer is
  // due when its virtual deadline (measured against Date.now) has passed.

  var timers = new Map();
  var nextTimerId = 0;

  globalThis.setTimeout = function (fn) {
    var args = Array.prototype.slice.call(arguments, 2);
    var delay = Number(arguments[1]) || 0;
    var id = ++nextTimerId;
    timers.set(id, { fn: fn, args: args, at: Date.now() + delay, interval: 0 });
    return id;
  };

  globalThis.setInterval = function (fn) {
    var args = Array.prototype.slice.call(arguments, 2);
    var delay = Number(arguments[1]) || 0;
    var id = ++nextTimerId;
    timers.set(id, { fn: fn, args: args, at: Date.now() + delay, interval: Math.max(delay, 1) });
    return id;
  };

  globalThis.clearTimeout = function (id) {
    timers.delete(id);
  };

  globalThis.clearInterval = globalThis.clearTimeout;

  globalThis.setImmediate = function (fn) {
    var args = Array.prototype.slice.call(arguments, 1);
    return globalThis.setTimeout.apply(undefined, [fn, 0].concat(args));
  };

  globalThis.__pi_pending_timers = function () {
    return timers.size;
  };

  /** Fire every timer whose deadline has passed. Returns how many ran. */
  globalThis.__pi_fire_timers = function () {
    var now = Date.now();
    var due = [];
    timers.forEach(function (timer, id) {
      if (timer.at <= now) due.push(id);
    });
    var ran = 0;
    for (var i = 0; i < due.length; i++) {
      var id = due[i];
      var timer = timers.get(id);
      if (timer === undefined) continue;
      if (timer.interval > 0) timer.at = now + timer.interval;
      else timers.delete(id);
      ran++;
      try {
        timer.fn.apply(undefined, timer.args);
      } catch (error) {
        host.log("error", "timer callback threw: " + __pi_describe(error));
      }
    }
    return ran;
  };

  globalThis.queueMicrotask = function (fn) {
    Promise.resolve().then(fn);
  };

  /**
   * Settlement box: the host hands a value (usually a promise) here and then
   * pumps `ProcessTasks()` plus due timers until `done` flips. Promises are the
   * only way an `async` extension factory can hand control back mid-run, and the
   * host has no event loop of its own.
   */
  globalThis.__pi_track = function () {
    var box = { done: false, ok: false, value: undefined };
    box.settle = function (value) {
      if (value !== null && value !== undefined && typeof value.then === "function") {
        value.then(
          function (resolved) { box.done = true; box.ok = true; box.value = resolved; },
          function (error) { box.done = true; box.ok = false; box.value = error; });
      } else {
        box.done = true;
        box.ok = true;
        box.value = value;
      }
      return box;
    };
    return box;
  };

  if (typeof globalThis.process === "undefined") globalThis.process = {};
  globalThis.process.nextTick = function (fn) {
    var args = Array.prototype.slice.call(arguments, 1);
    Promise.resolve().then(function () {
      fn.apply(undefined, args);
    });
  };

  // ---------------------------------------------------------------- console

  function emit(level, args) {
    var parts = [];
    for (var i = 0; i < args.length; i++) parts.push(__pi_describe(args[i]));
    host.log(level, parts.join(" "));
  }

  globalThis.console = {
    log: function () { emit("log", arguments); },
    info: function () { emit("info", arguments); },
    warn: function () { emit("warn", arguments); },
    error: function () { emit("error", arguments); },
    debug: function () { emit("debug", arguments); },
    trace: function () { emit("trace", arguments); },
    group: function () { emit("log", arguments); },
    groupEnd: function () {},
    time: function () {},
    timeEnd: function () {},
    assert: function (condition) {
      if (!condition) emit("error", Array.prototype.slice.call(arguments, 1));
    },
  };

  // ---------------------------------------------------------------- process

  var env = {};
  try {
    env = JSON.parse(host.env() || "{}");
  } catch (_) {
    env = {};
  }

  globalThis.process = {
    env: env,
    argv: ["pi", ""],
    platform: host.platform(),
    arch: "",
    pid: 0,
    version: "v22.0.0",
    versions: { node: "22.0.0" },
    cwd: function () { return host.cwd(); },
    exitCode: 0,
    exit: function (code) {
      throw __pi_call_error("process.exit(" + (code === undefined ? 0 : code) + ") is not supported inside a Pi extension");
    },
    nextTick: globalThis.process.nextTick,
    hrtime: function () {
      var ms = Date.now();
      return [Math.floor(ms / 1000), (ms % 1000) * 1e6];
    },
    memoryUsage: function () {
      return { rss: 0, heapTotal: 0, heapUsed: 0, external: 0 };
    },
    on: function () { return globalThis.process; },
    once: function () { return globalThis.process; },
    off: function () { return globalThis.process; },
    emit: function () { return false; },
    listeners: function () { return []; },
    stdout: { write: function (text) { host.log("log", String(text).replace(/\n$/, "")); return true; }, isTTY: false },
    stderr: { write: function (text) { host.log("error", String(text).replace(/\n$/, "")); return true; }, isTTY: false },
    stdin: { isTTY: false, on: function () {} },
  };

  // ---------------------------------------------------------------- Buffer
  //
  // A deliberately small subset: the extensions in the upstream corpus use
  // Buffer for base64 and utf8 payloads, not for binary protocols.

  var BASE64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

  function utf8Encode(text) {
    var bytes = [];
    for (var i = 0; i < text.length; i++) {
      var code = text.charCodeAt(i);
      if (code < 0x80) bytes.push(code);
      else if (code < 0x800) {
        bytes.push(0xc0 | (code >> 6), 0x80 | (code & 0x3f));
      } else if (code >= 0xd800 && code <= 0xdbff && i + 1 < text.length) {
        var next = text.charCodeAt(++i);
        var point = 0x10000 + ((code - 0xd800) << 10) + (next - 0xdc00);
        bytes.push(0xf0 | (point >> 18), 0x80 | ((point >> 12) & 0x3f), 0x80 | ((point >> 6) & 0x3f), 0x80 | (point & 0x3f));
      } else {
        bytes.push(0xe0 | (code >> 12), 0x80 | ((code >> 6) & 0x3f), 0x80 | (code & 0x3f));
      }
    }
    return bytes;
  }

  function utf8Decode(bytes) {
    var out = "";
    for (var i = 0; i < bytes.length;) {
      var byte = bytes[i++];
      if (byte < 0x80) out += String.fromCharCode(byte);
      else if (byte < 0xe0) out += String.fromCharCode(((byte & 0x1f) << 6) | (bytes[i++] & 0x3f));
      else if (byte < 0xf0) out += String.fromCharCode(((byte & 0x0f) << 12) | ((bytes[i++] & 0x3f) << 6) | (bytes[i++] & 0x3f));
      else {
        var point = ((byte & 0x07) << 18) | ((bytes[i++] & 0x3f) << 12) | ((bytes[i++] & 0x3f) << 6) | (bytes[i++] & 0x3f);
        point -= 0x10000;
        out += String.fromCharCode(0xd800 + (point >> 10), 0xdc00 + (point & 0x3ff));
      }
    }
    return out;
  }

  function base64Encode(bytes) {
    var out = "";
    for (var i = 0; i < bytes.length; i += 3) {
      var b0 = bytes[i], b1 = bytes[i + 1], b2 = bytes[i + 2];
      out += BASE64[b0 >> 2];
      out += BASE64[((b0 & 3) << 4) | ((b1 === undefined ? 0 : b1) >> 4)];
      out += b1 === undefined ? "=" : BASE64[((b1 & 15) << 2) | ((b2 === undefined ? 0 : b2) >> 6)];
      out += b2 === undefined ? "=" : BASE64[b2 & 63];
    }
    return out;
  }

  function base64Decode(text) {
    var clean = String(text).replace(/[^A-Za-z0-9+/]/g, "");
    var bytes = [];
    for (var i = 0; i < clean.length; i += 4) {
      var c0 = BASE64.indexOf(clean[i]);
      var c1 = BASE64.indexOf(clean[i + 1]);
      var c2 = clean[i + 2] === undefined ? -1 : BASE64.indexOf(clean[i + 2]);
      var c3 = clean[i + 3] === undefined ? -1 : BASE64.indexOf(clean[i + 3]);
      bytes.push((c0 << 2) | (c1 >> 4));
      if (c2 >= 0) bytes.push(((c1 & 15) << 4) | (c2 >> 2));
      if (c3 >= 0) bytes.push(((c2 & 3) << 6) | c3);
    }
    return bytes;
  }

  function BufferView(bytes) {
    this.bytes = bytes;
    this.length = bytes.length;
  }

  BufferView.prototype.toString = function (encoding) {
    var format = (encoding || "utf8").toLowerCase();
    if (format === "base64") return base64Encode(this.bytes);
    if (format === "hex") {
      return this.bytes.map(function (byte) { return (byte < 16 ? "0" : "") + byte.toString(16); }).join("");
    }
    return utf8Decode(this.bytes);
  };

  BufferView.prototype.slice = function (start, end) {
    return new BufferView(this.bytes.slice(start, end));
  };

  BufferView.prototype.subarray = BufferView.prototype.slice;

  globalThis.Buffer = {
    from: function (value, encoding) {
      if (value instanceof BufferView) return new BufferView(value.bytes.slice());
      if (Array.isArray(value)) return new BufferView(value.slice());
      var text = String(value);
      var format = (encoding || "utf8").toLowerCase();
      if (format === "base64") return new BufferView(base64Decode(text));
      if (format === "hex") {
        var bytes = [];
        for (var i = 0; i < text.length; i += 2) bytes.push(parseInt(text.substr(i, 2), 16));
        return new BufferView(bytes);
      }
      return new BufferView(utf8Encode(text));
    },
    alloc: function (size) {
      var bytes = [];
      for (var i = 0; i < size; i++) bytes.push(0);
      return new BufferView(bytes);
    },
    isBuffer: function (value) {
      return value instanceof BufferView;
    },
    concat: function (list) {
      var bytes = [];
      for (var i = 0; i < list.length; i++) bytes = bytes.concat(list[i].bytes);
      return new BufferView(bytes);
    },
    byteLength: function (value, encoding) {
      return globalThis.Buffer.from(value, encoding).length;
    },
  };

  globalThis.__pi_buffer_view = BufferView;

  // ---------------------------------------------------------------- text codecs

  globalThis.TextEncoder = function TextEncoder() {};
  globalThis.TextEncoder.prototype.encode = function (text) {
    var bytes = utf8Encode(String(text));
    return { buffer: bytes, length: bytes.length, toString: function () { return utf8Decode(bytes); } };
  };

  globalThis.TextDecoder = function TextDecoder() {};
  globalThis.TextDecoder.prototype.decode = function (bytes) {
    if (bytes === undefined || bytes === null) return "";
    return utf8Decode(bytes.buffer || bytes);
  };

  // ---------------------------------------------------------------- url

  function URLSearchParamsShim(init) {
    var pairs = [];
    if (typeof init === "string" && init.length > 0) {
      init.replace(/^\?/, "").split("&").forEach(function (part) {
        if (part === "") return;
        var index = part.indexOf("=");
        var key = index < 0 ? part : part.slice(0, index);
        var value = index < 0 ? "" : part.slice(index + 1);
        pairs.push([decodeURIComponent(key), decodeURIComponent(value)]);
      });
    } else if (init && typeof init === "object") {
      Object.keys(init).forEach(function (key) { pairs.push([key, String(init[key])]); });
    }
    this.pairs = pairs;
  }
  URLSearchParamsShim.prototype.get = function (name) {
    for (var i = 0; i < this.pairs.length; i++) if (this.pairs[i][0] === name) return this.pairs[i][1];
    return null;
  };
  URLSearchParamsShim.prototype.has = function (name) { return this.get(name) !== null; };
  URLSearchParamsShim.prototype.append = function (name, value) { this.pairs.push([name, String(value)]); };
  URLSearchParamsShim.prototype.set = function (name, value) {
    this.remove(name);
    this.append(name, value);
  };
  URLSearchParamsShim.prototype.remove = function (name) {
    this.pairs = this.pairs.filter(function (pair) { return pair[0] !== name; });
  };
  URLSearchParamsShim.prototype.toString = function () {
    return this.pairs.map(function (pair) {
      return encodeURIComponent(pair[0]) + "=" + encodeURIComponent(pair[1]);
    }).join("&");
  };
  URLSearchParamsShim.prototype.forEach = function (fn) {
    this.pairs.forEach(function (pair) { fn(pair[1], pair[0]); });
  };

  globalThis.URLSearchParams = URLSearchParamsShim;

  globalThis.URL = function URLShim(value, base) {
    var text = String(value);
    if (base && !/^[a-zA-Z]+:/.test(text)) {
      var baseText = String(base);
      var prefix = /^[a-zA-Z]+:\/\/[^/]+/.exec(baseText);
      if (prefix) text = prefix[0] + (text.charAt(0) === "/" ? "" : "/") + text;
    }
    var match = /^([a-zA-Z][a-zA-Z0-9+.-]*):\/\/([^/?#]*)([^?#]*)(?:\?([^#]*))?(?:#(.*))?$/.exec(text);
    if (!match) throw new TypeError("Invalid URL: " + text);
    this.protocol = match[1] + ":";
    this.hostname = match[2].split(":")[0];
    this.port = match[2].indexOf(":") >= 0 ? match[2].split(":")[1] : "";
    this.pathname = match[3] || "/";
    this.search = match[4] ? "?" + match[4] : "";
    this.hash = match[5] ? "#" + match[5] : "";
    this.origin = this.protocol + "//" + match[2];
    this.searchParams = new URLSearchParamsShim(match[4]);
  };
  globalThis.URL.prototype.toString = function () {
    return this.origin + this.pathname + this.search + this.hash;
  };
  globalThis.URL.prototype.href = function () {
    return this.toString();
  };

  // ---------------------------------------------------------------- api facade

  function register(callback) {
    return globalThis.__pi_register_callback(callback);
  }

  // TypeBox schemas carry optionality out of band (`Type.Optional`). The host
  // wants plain JSON Schema, so optionality turns into `required` entries here.
  globalThis.__pi_normalize_schema = function normalize(schema) {
    if (schema === null || schema === undefined || typeof schema !== "object") return schema;
    if (Array.isArray(schema)) return schema.map(normalize);

    var out = {};
    var keys = Object.keys(schema);
    for (var i = 0; i < keys.length; i++) {
      var key = keys[i];
      if (key === "__piOptional" || key === "__pi_optional") continue;
      out[key] = normalize(schema[key]);
    }

    if (schema.properties && typeof schema.properties === "object") {
      var required = [];
      var names = Object.keys(schema.properties);
      for (var j = 0; j < names.length; j++) {
        var property = schema.properties[names[j]];
        var isOptional = property && typeof property === "object"
          && (property.__piOptional === true || property.__pi_optional === true);
        if (!isOptional) required.push(names[j]);
      }
      if (required.length > 0) out.required = required;
      else delete out.required;
    }

    return out;
  };

  function makeSignal() {
    return {
      aborted: false,
      reason: undefined,
      addEventListener: function () {},
      removeEventListener: function () {},
      throwIfAborted: function () {},
    };
  }

  /**
   * Upstream themes colorize text with `theme.fg(name, text)` and style it with
   * `theme.bold(text)`. The host does not ship the theme table to JavaScript yet,
   * so the facade keeps every function callable and returns the text unchanged.
   */
  function makeTheme(name) {
    var pass = function (text) { return text === undefined || text === null ? "" : String(text); };
    return {
      name: typeof name === "string" ? name : "default",
      fg: function (_color, text) { return pass(text); },
      bg: function (_color, text) { return pass(text); },
      color: function (_color, text) { return pass(text); },
      bold: pass,
      dim: pass,
      italic: pass,
      underline: pass,
      inverse: pass,
      accent: pass,
      muted: pass,
      error: pass,
      warning: pass,
      success: pass,
      info: pass,
      border: pass,
      toolTitle: pass,
      toolOutput: pass,
      reset: pass,
    };
  }

  function makeUi(handle, snapshot) {
    var ui = {};
    var data = snapshot || {};

    ui.notify = function (message, level) {
      __pi_call("ui.notify", { handle: handle, message: String(message), level: level || "info" });
    };

    ui.setStatus = function (key, text) {
      __pi_call("ui.setStatus", { handle: handle, key: String(key), text: text === undefined ? undefined : String(text) });
    };

    ui.setWidget = function (key, lines) {
      __pi_call("ui.setWidget", { handle: handle, key: String(key), lines: lines });
    };

    ui.setTitle = function (title) {
      __pi_call("ui.setTitle", { handle: handle, title: String(title) });
    };

    ui.setEditorText = function (text) {
      __pi_call("ui.setEditorText", { handle: handle, text: String(text) });
    };

    ui.getEditorText = function () {
      return __pi_call("ui.getEditorText", { handle: handle }) || "";
    };

    ui.pasteToEditor = function (text) {
      __pi_call("ui.setEditorText", { handle: handle, text: String(text) });
    };

    ui.select = function (title, options, opts) {
      return Promise.resolve(__pi_call("ui.select", { handle: handle, title: title, options: options, opts: opts }));
    };

    ui.confirm = function (title, message, opts) {
      return Promise.resolve(__pi_call("ui.confirm", { handle: handle, title: title, message: message, opts: opts }));
    };

    ui.input = function (title, placeholder, opts) {
      return Promise.resolve(__pi_call("ui.input", { handle: handle, title: title, placeholder: placeholder, opts: opts }));
    };

    ui.editor = function (title, prefill) {
      return Promise.resolve(__pi_call("ui.editor", { handle: handle, title: title, prefill: prefill }));
    };

    ui.custom = function (factory, options) {
      // The component protocol needs the TUI render loop; the host reports whether
      // it is available instead of silently returning an empty component.
      var result = __pi_call("ui.custom", { handle: handle, hasFactory: typeof factory === "function" });
      if (result && result.unsupported) throw __pi_call_error(result.unsupported);
      if (options && options.overlay) return undefined;
      return undefined;
    };

    ui.setFooter = function () { __pi_call("ui.unsupported", { handle: handle, method: "setFooter" }); };
    ui.setHeader = function () { __pi_call("ui.unsupported", { handle: handle, method: "setHeader" }); };
    ui.setEditorComponent = function () { __pi_call("ui.unsupported", { handle: handle, method: "setEditorComponent" }); };
    ui.addAutocompleteProvider = function () { __pi_call("ui.unsupported", { handle: handle, method: "addAutocompleteProvider" }); };
    ui.setWorkingMessage = function () {};
    ui.setWorkingVisible = function () {};
    ui.setWorkingIndicator = function () {};
    ui.setHiddenThinkingLabel = function () {};
    ui.setToolsExpanded = function () {};
    ui.getToolsExpanded = function () { return false; };
    ui.requestRender = function () {};
    ui.theme = makeTheme(data.theme);
    ui.getAllThemes = function () { return [ui.theme]; };
    ui.getTheme = function () { return ui.theme; };
    ui.setTheme = function () { return { success: false, error: "Theme switching is not supported" }; };
    ui.showOverlay = function () {};
    ui.setEditorText = ui.setEditorText;

    return ui;
  }

  /**
   * Built-in tool definition (`createReadToolDefinition(cwd)`, `createBashTool(cwd)`, …).
   *
   * Upstream hands extensions the host's own tool definitions so they can wrap the
   * rendering. The real schema is not available to JavaScript here, so the definition
   * carries a permissive object schema and delegates execution to the host's tool
   * runner through the extension context.
   */
  function __piBuiltinTool(name, label, description) {
    return {
      name: name,
      label: label,
      description: description,
      parameters: { type: "object", properties: {}, additionalProperties: true },
      async execute(_toolCallId, params, _signal, _onUpdate, ctx) {
        var result = __pi_call("tool.executeBuiltin", {
          name: name,
          params: params === undefined ? {} : params,
          handle: ctx && ctx.__handle,
        });
        return result === null || result === undefined ? { content: [] } : result;
      },
    };
  }

  globalThis.__pi_builtin_tool = __piBuiltinTool;

  globalThis.__pi_make_context = function (payload) {
    if (!payload) return undefined;
    var handle = payload.handle;
    var ctx = {
      // Host-side identity for capabilities that must call back in later (a tool
      // definition running the built-in tool it wraps).
      __handle: handle,
      cwd: payload.cwd,
      mode: payload.mode,
      hasUI: payload.hasUI,
      isIdle: payload.isIdle,
      model: payload.model,
      modelRegistry: payload.modelRegistry,
      scopedModels: payload.scopedModels,
      thinkingLevel: payload.thinkingLevel,
      signal: makeSignal(),
      sessionManager: payload.sessionManager,
      ui: makeUi(handle, payload.ui),
    };

    ctx.shutdown = function () { return Promise.resolve(__pi_call("ctx.shutdown", { handle: handle })); };
    ctx.reload = function () { return Promise.resolve(__pi_call("ctx.reload", { handle: handle })); };
    ctx.compact = function (options) { return Promise.resolve(__pi_call("ctx.compact", { handle: handle, options: options })); };
    ctx.getSystemPrompt = function () { return __pi_call("ctx.getSystemPrompt", { handle: handle }); };
    ctx.getContextUsage = function () { return __pi_call("ctx.getContextUsage", { handle: handle }); };
    ctx.newSession = function (options) { return Promise.resolve(__pi_call("ctx.newSession", { handle: handle, options: options })); };
    ctx.fork = function (entryId, options) { return Promise.resolve(__pi_call("ctx.fork", { handle: handle, entryId: entryId, options: options })); };
    ctx.switchSession = function (sessionPath, options) { return Promise.resolve(__pi_call("ctx.switchSession", { handle: handle, sessionPath: sessionPath, options: options })); };
    ctx.navigateTree = function (targetId, options) { return Promise.resolve(__pi_call("ctx.navigateTree", { handle: handle, targetId: targetId, options: options })); };
    ctx.isProjectTrusted = function () { return payload.isProjectTrusted === true; };
    return ctx;
  };

  globalThis.__pi_make_api = function () {
    var api = {};

    api.registerTool = function (tool) {
      var payload = {
        name: tool.name,
        label: tool.label,
        description: tool.description,
        parameters: globalThis.__pi_normalize_schema(tool.parameters),
        promptSnippet: tool.promptSnippet,
        promptGuidelines: tool.promptGuidelines,
        renderShell: tool.renderShell,
        exposure: tool.exposure,
        defaultActive: tool.defaultActive,
        annotations: tool.annotations,
        namespace: tool.namespace,
      };

      if (typeof tool.execute === "function") {
        payload.execute = register(function (toolCallId, params, ctxPayload, updateToken) {
          return tool.execute(
            toolCallId,
            params,
            makeSignal(),
            function (partial) {
              __pi_call("tool.update", { token: updateToken, partial: partial });
            },
            __pi_make_context(ctxPayload));
        });
      }

      __pi_call("registerTool", payload);
    };

    api.registerCommand = function (name, options) {
      var payload = { name: String(name), description: options && options.description };
      if (options && typeof options.handler === "function") {
        payload.handler = register(function (args, ctxPayload) {
          return options.handler(args, __pi_make_context(ctxPayload));
        });
      }
      if (options && typeof options.getArgumentCompletions === "function") {
        payload.completions = register(function (args) {
          return options.getArgumentCompletions(args);
        });
      }
      __pi_call("registerCommand", payload);
    };

    api.registerShortcut = function (shortcut, options) {
      var payload = { shortcut: String(shortcut), description: options && options.description };
      if (options && typeof options.handler === "function") {
        payload.handler = register(function (ctxPayload) {
          return options.handler(__pi_make_context(ctxPayload));
        });
      }
      __pi_call("registerShortcut", payload);
    };

    api.registerFlag = function (name, options) {
      __pi_call("registerFlag", {
        name: String(name),
        description: options && options.description,
        type: (options && options.type) || "boolean",
        defaultValue: options && options.default,
      });
    };

    api.getFlag = function (name) {
      var value = __pi_call("getFlag", { name: String(name) });
      return value === null ? undefined : value;
    };

    api.on = function (eventName, handler) {
      var id = register(handler);
      __pi_call("on", { event: String(eventName), handler: id });
      return function () {
        __pi_call("off", { event: String(eventName), handler: id });
        __pi_release_callback(id);
      };
    };

    api.events = {
      on: function (name, handler) {
        var id = register(handler);
        __pi_call("events.subscribe", { name: String(name), handler: id });
        return function () {
          __pi_call("events.unsubscribe", { name: String(name), handler: id });
          __pi_release_callback(id);
        };
      },
      emit: function (name, data) {
        __pi_call("events.emit", { name: String(name), data: data });
      },
    };

    api.exec = function (command, args, options) {
      return Promise.resolve(__pi_call("exec", {
        command: String(command),
        args: args || [],
        options: options || {},
      }));
    };

    api.sendMessage = function (message, options) {
      __pi_call("sendMessage", { message: message, options: options });
    };

    api.sendUserMessage = function (content, options) {
      __pi_call("sendUserMessage", { content: content, options: options });
    };

    api.appendEntry = function (customType, data) {
      __pi_call("appendEntry", { customType: String(customType), data: data });
    };

    api.setSessionName = function (name) {
      __pi_call("setSessionName", { name: String(name) });
    };

    api.getSessionName = function () {
      return __pi_call("getSessionName", {});
    };

    api.setLabel = function (entryId, label) {
      __pi_call("setLabel", { entryId: String(entryId), label: label });
    };

    api.getActiveTools = function () {
      return __pi_call("getActiveTools", {}) || [];
    };

    api.getAllTools = function () {
      return __pi_call("getAllTools", {}) || [];
    };

    api.setActiveTools = function (names) {
      __pi_call("setActiveTools", { names: names || [] });
    };

    api.getCommands = function () {
      return __pi_call("getCommands", {}) || [];
    };

    api.getSettings = function () {
      return __pi_call("getSettings", {}) || {};
    };

    api.setModel = function (model) {
      return Promise.resolve(__pi_call("setModel", { model: model }));
    };

    api.getThinkingLevel = function () {
      return __pi_call("getThinkingLevel", {});
    };

    api.setThinkingLevel = function (level) {
      __pi_call("setThinkingLevel", { level: level });
    };

    function unsupported(name) {
      return function () {
        throw __pi_call_error("pi." + name + " is not supported by the .NET JavaScript extension host yet");
      };
    }

    api.registerProvider = unsupported("registerProvider");
    api.unregisterProvider = unsupported("unregisterProvider");
    api.registerMcpServer = unsupported("registerMcpServer");
    api.unregisterMcpServer = unsupported("unregisterMcpServer");
    api.getMcpServers = unsupported("getMcpServers");
    api.registerVirtualModel = unsupported("registerVirtualModel");
    api.unregisterVirtualModel = unsupported("unregisterVirtualModel");

    // Renderers only change how output is drawn. The host cannot yet marshal a
    // JavaScript component into the terminal render loop, so registration degrades
    // to a warning: the extension keeps its tools and commands, the transcript keeps
    // the default rendering.
    function degradedRenderer(name) {
      return function () {
        __pi_call("host.warn", {
          message: "pi." + name + " is ignored by the .NET JavaScript extension host: the extension loads, "
            + "but its custom rendering is not applied.",
        });
      };
    }

    api.registerMessageRenderer = degradedRenderer("registerMessageRenderer");
    api.registerEntryRenderer = degradedRenderer("registerEntryRenderer");
    api.registerToolRenderer = degradedRenderer("registerToolRenderer");
    api.registerMarkdownTransformer = degradedRenderer("registerMarkdownTransformer");

    return api;
  };
})();
