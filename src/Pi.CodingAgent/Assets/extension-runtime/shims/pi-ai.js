// Shim for `@earendil-works/pi-ai` — the schema builder, string enums, id helper
// and the compat surface an extension may import at runtime.
//
// `Type` mirrors TypeBox's runtime shape closely enough that the schemas it
// produces normalize to the same JSON Schema the host validates against.

"use strict";

function annotate(schema, options) {
  if (!options) return schema;
  var result = {};
  Object.keys(schema).forEach(function (key) { result[key] = schema[key]; });
  Object.keys(options).forEach(function (key) { result[key] = options[key]; });
  return result;
}

function primitive(type, options) {
  return annotate({ type: type }, options);
}

var Type = {
  String: function (options) { return primitive("string", options); },
  Number: function (options) { return primitive("number", options); },
  Integer: function (options) { var schema = primitive("integer", options); return schema; },
  Boolean: function (options) { return primitive("boolean", options); },
  Null: function (options) { return primitive("null", options); },
  Any: function (options) { return annotate({}, options); },
  Unknown: function (options) { return annotate({}, options); },

  Literal: function (value, options) {
    var schema = { const: value };
    if (typeof value === "string") schema.type = "string";
    else if (typeof value === "number") schema.type = "number";
    else if (typeof value === "boolean") schema.type = "boolean";
    return annotate(schema, options);
  },

  Enum: function (values, options) {
    return annotate({ type: "string", enum: values }, options);
  },

  Array: function (items, options) {
    return annotate({ type: "array", items: items }, options);
  },

  Tuple: function (items, options) {
    return annotate({ type: "array", items: items, minItems: items.length, maxItems: items.length }, options);
  },

  Object: function (properties, options) {
    var schema = {
      type: "object",
      properties: properties || {},
    };
    return annotate(schema, options);
  },

  Record: function (keySchema, valueSchema, options) {
    var schema = {
      type: "object",
      propertyNames: keySchema,
      additionalProperties: valueSchema,
    };
    return annotate(schema, options);
  },

  Union: function (schemas, options) {
    return annotate({ anyOf: schemas }, options);
  },

  Intersect: function (schemas, options) {
    return annotate({ allOf: schemas }, options);
  },

  Optional: function (schema) {
    var result = {};
    Object.keys(schema || {}).forEach(function (key) { result[key] = schema[key]; });
    result.__piOptional = true;
    return result;
  },

  Partial: function (schema) {
    var result = {};
    Object.keys(schema || {}).forEach(function (key) { result[key] = schema[key]; });
    if (result.properties) {
      var properties = {};
      Object.keys(result.properties).forEach(function (name) {
        properties[name] = Type.Optional(result.properties[name]);
      });
      result.properties = properties;
    }
    return result;
  },

  Required: function (schema) { return schema; },
};

/**
 * `StringEnum` is pi-ai's helper for a closed set of strings. TypeBox emits the
 * same shape as `Type.Enum`; the extra `StringEnum` name exists so extensions can
 * import it without pulling in TypeBox itself.
 */
function StringEnum(values, options) {
  return Type.Enum(values, options);
}

/** Monotonic-ish UUIDv7: timestamp prefix plus random tail. */
function uuidv7() {
  var now = Date.now();
  var hex = now.toString(16).padStart(12, "0");
  var random = "";
  for (var i = 0; i < 20; i++) random += Math.floor(Math.random() * 16).toString(16);
  return (
    hex.slice(0, 8) + "-" + hex.slice(8, 12) + "-7" + random.slice(0, 3) + "-" +
    ((8 + Math.floor(Math.random() * 4)).toString(16)) + random.slice(3, 6) + "-" + random.slice(6, 18)
  );
}

function unsupported(name) {
  return function () {
    throw new Error(name + " is not available in the .NET JavaScript extension host");
  };
}

module.exports = {
  Type: Type,
  StringEnum: StringEnum,
  uuidv7: uuidv7,
  Value: {
    Check: function () { return true; },
    Cast: function (_schema, value) { return value; },
    Clone: function (value) { return JSON.parse(JSON.stringify(value)); },
  },

  // Provider-side APIs: an extension that registers a provider needs the real
  // streaming stack, which the host does not expose to JavaScript yet.
  registerApiProvider: unsupported("registerApiProvider"),
  unregisterApiProvider: unsupported("unregisterApiProvider"),
  streamSimple: unsupported("streamSimple"),
  stream: unsupported("stream"),
  complete: unsupported("complete"),
  getModel: function () { return undefined; },
};
