;; Laplace SQL structural tags (#765 / W3).
;; Capture names must match grammar_tags.c tag_type_of:
;;   @name, @definition.function, @reference.call, @reference.type
;;
;; Note: plpgsql dollar bodies are only partially parsed by tree-sitter-sql;
;; CALLS fire for `invocation` nodes the grammar actually builds (LANGUAGE sql
;; bodies, top-level statements, and some nested SQL). DEFINES always targets
;; the CREATE FUNCTION name.

(create_function
  (object_reference
    name: (identifier) @name)) @definition.function

(invocation
  (object_reference
    name: (identifier) @name)) @reference.call
