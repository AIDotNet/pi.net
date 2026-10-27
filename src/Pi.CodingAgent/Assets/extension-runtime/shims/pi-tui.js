// Shim for `@earendil-works/pi-tui` — the component primitives, key matching and
// width helpers an extension uses at runtime.
//
// Components render to `string[]` (one entry per terminal row). That is exactly
// the protocol the host's UI bridge consumes, so a component built here keeps
// working once a rendered component is attached to the TUI.

"use strict";

// ------------------------------------------------------------------ width

var WIDE = [
  [0x1100, 0x115f], [0x2e80, 0x303e], [0x3041, 0x33ff], [0x3400, 0x4dbf],
  [0x4e00, 0x9fff], [0xa000, 0xa4cf], [0xa960, 0xa97f], [0xac00, 0xd7a3],
  [0xf900, 0xfaff], [0xfe10, 0xfe19], [0xfe30, 0xfe6f], [0xff00, 0xff60],
  [0xffe0, 0xffe6], [0x1f300, 0x1f64f], [0x1f900, 0x1f9ff], [0x20000, 0x3fffd],
];

function charWidth(code) {
  if (code === 0) return 0;
  if (code < 32 || (code >= 0x7f && code < 0xa0)) return 0;
  for (var i = 0; i < WIDE.length; i++) {
    if (code >= WIDE[i][0] && code <= WIDE[i][1]) return 2;
  }
  return 1;
}

function visibleWidth(text) {
  var value = String(text === undefined || text === null ? "" : text);
  var width = 0;
  for (var i = 0; i < value.length; i++) {
    var code = value.codePointAt(i);
    if (code > 0xffff) i++;
    width += charWidth(code);
  }
  return width;
}

function truncateToWidth(text, width, ellipsis) {
  var value = String(text === undefined || text === null ? "" : text);
  if (width <= 0) return "";
  if (visibleWidth(value) <= width) return value;
  var suffix = ellipsis === undefined ? "…" : String(ellipsis);
  var budget = width - visibleWidth(suffix);
  if (budget <= 0) return suffix.slice(0, width);
  var out = "";
  var used = 0;
  for (var i = 0; i < value.length; i++) {
    var code = value.codePointAt(i);
    var size = charWidth(code);
    if (used + size > budget) break;
    out += String.fromCodePoint(code);
    used += size;
    if (code > 0xffff) i++;
  }
  return out + suffix;
}

// ------------------------------------------------------------------ keys

var Key = {
  escape: "escape", enter: "enter", tab: "tab", space: "space",
  backspace: "backspace", delete: "delete", up: "up", down: "down",
  left: "left", right: "right", home: "home", end: "end",
  pageUp: "pageup", pageDown: "pagedown",
  ctrl: function (letter) { return "ctrl+" + String(letter).toLowerCase(); },
  alt: function (letter) { return "alt+" + String(letter).toLowerCase(); },
  shift: function (letter) { return "shift+" + String(letter).toLowerCase(); },
  ctrlShift: function (letter) { return "ctrl+shift+" + String(letter).toLowerCase(); },
  ctrlAlt: function (letter) { return "ctrl+alt+" + String(letter).toLowerCase(); },
  altShift: function (letter) { return "alt+shift+" + String(letter).toLowerCase(); },
  shiftAlt: function (letter) { return "alt+shift+" + String(letter).toLowerCase(); },
};

function normalizeKey(data) {
  return String(data === undefined || data === null ? "" : data).toLowerCase();
}

function matchesKey(data, keyId) {
  var actual = normalizeKey(data);
  var expected = normalizeKey(keyId);
  if (actual === expected) return true;
  // Ctrl+letter arrives as the corresponding control character.
  if (expected.indexOf("ctrl+") === 0 && expected.length === 6) {
    return actual.length === 1 && actual.charCodeAt(0) === expected.charCodeAt(5) - 96;
  }
  return false;
}

function isKeyRelease() {
  return false;
}

function isKeyRepeat() {
  return false;
}

// ------------------------------------------------------------------ components

function Component() {}
Component.prototype.render = function () { return []; };
Component.prototype.invalidate = function () {};
Component.prototype.dispose = function () {};

function Text(text, x, y) {
  Component.call(this);
  this.text = text === undefined || text === null ? "" : String(text);
  this.x = x || 0;
  this.y = y || 0;
}
Text.prototype = Object.create(Component.prototype);
Text.prototype.constructor = Text;
Text.prototype.setText = function (text) { this.text = String(text); };
Text.prototype.render = function (width) {
  var lines = this.text.split("\n");
  var pad = "";
  for (var i = 0; i < (this.x || 0); i++) pad += " ";
  var out = [];
  for (var line = 0; line < (this.y || 0); line++) out.push("");
  for (var index = 0; index < lines.length; index++) {
    out.push(pad + (width === undefined ? lines[index] : truncateToWidth(lines[index], width - this.x)));
  }
  return out;
};

function Box(paddingTop, paddingRight, paddingBottom, paddingLeft) {
  Component.call(this);
  this.padding = [
    paddingTop || 0,
    paddingRight === undefined ? paddingTop || 0 : paddingRight,
    paddingBottom === undefined ? paddingTop || 0 : paddingBottom,
    paddingLeft === undefined ? paddingTop || 0 : paddingLeft,
  ];
  this.children = [];
}
Box.prototype = Object.create(Component.prototype);
Box.prototype.constructor = Box;
Box.prototype.addChild = function (child) { this.children.push(child); return child; };
Box.prototype.removeChild = function (child) {
  var index = this.children.indexOf(child);
  if (index >= 0) this.children.splice(index, 1);
};
Box.prototype.render = function (width) {
  var inner = Math.max(0, width - this.padding[1] - this.padding[3]);
  var lines = [];
  for (var i = 0; i < this.padding[0]; i++) lines.push("");
  for (var child = 0; child < this.children.length; child++) {
    var rendered = this.children[child].render(inner) || [];
    var padLeft = " ".repeat(this.padding[3]);
    for (var line = 0; line < rendered.length; line++) lines.push(padLeft + rendered[line]);
  }
  for (var j = 0; j < this.padding[2]; j++) lines.push("");
  return lines;
};

function Container() {
  Component.call(this);
  this.children = [];
}
Container.prototype = Object.create(Component.prototype);
Container.prototype.constructor = Container;
Container.prototype.addChild = function (child) { this.children.push(child); return child; };
Container.prototype.removeChild = Box.prototype.removeChild;
Container.prototype.clear = function () { this.children = []; };
Container.prototype.render = function (width) {
  var lines = [];
  for (var i = 0; i < this.children.length; i++) {
    lines = lines.concat(this.children[i].render(width) || []);
  }
  return lines;
};

function Spacer(lines) {
  Component.call(this);
  this.lines = lines || 1;
}
Spacer.prototype = Object.create(Component.prototype);
Spacer.prototype.constructor = Spacer;
Spacer.prototype.render = function () {
  var out = [];
  for (var i = 0; i < this.lines; i++) out.push("");
  return out;
};

function SettingsList(options) {
  Component.call(this);
  this.options = options || {};
  this.items = (options && options.items) || [];
  this.selectedIndex = 0;
}
SettingsList.prototype = Object.create(Component.prototype);
SettingsList.prototype.constructor = SettingsList;
SettingsList.prototype.render = function () {
  var lines = [];
  for (var i = 0; i < this.items.length; i++) {
    var item = this.items[i];
    var label = typeof item === "string" ? item : (item.label || item.id || "");
    lines.push((i === this.selectedIndex ? "> " : "  ") + label);
  }
  return lines;
};
SettingsList.prototype.handleInput = function (data) {
  if (matchesKey(data, Key.up) || matchesKey(data, Key.down)) {
    var delta = matchesKey(data, Key.down) ? 1 : -1;
    this.selectedIndex = (this.selectedIndex + delta + this.items.length) % Math.max(this.items.length, 1);
    return true;
  }
  return false;
};

function CustomEditor() {
  Component.call(this);
}
CustomEditor.prototype = Object.create(Component.prototype);
CustomEditor.prototype.constructor = CustomEditor;

function SelectList(items) {
  this.items = items || [];
  this.selectedIndex = 0;
}
SelectList.prototype.render = SettingsList.prototype.render;
SelectList.prototype.handleInput = SettingsList.prototype.handleInput;

module.exports = {
  Component: Component,
  Text: Text,
  Box: Box,
  Container: Container,
  Spacer: Spacer,
  SettingsList: SettingsList,
  SelectList: SelectList,
  CustomEditor: CustomEditor,
  Key: Key,
  matchesKey: matchesKey,
  isKeyRelease: isKeyRelease,
  isKeyRepeat: isKeyRepeat,
  visibleWidth: visibleWidth,
  truncateToWidth: truncateToWidth,
};
