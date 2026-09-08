; Supplement the pinned C++ grammar's definition tags with witnessed call sites.
(call_expression function: (identifier) @name) @reference.call
(call_expression function: (qualified_identifier name: (identifier) @name)) @reference.call
(call_expression function: (field_expression field: (field_identifier) @name)) @reference.call
