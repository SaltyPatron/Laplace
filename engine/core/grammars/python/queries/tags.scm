;; Python structural tags lowered by GrammarTagWitness.
;; These node and field names are from the registered tree-sitter-python schema.

(function_definition
  name: (identifier) @name) @definition.function

(class_definition
  name: (identifier) @name) @definition.class

(call
  function: (identifier) @name) @reference.call

(call
  function: (attribute
    attribute: (identifier) @name)) @reference.call
