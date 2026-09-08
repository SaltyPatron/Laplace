#pragma once
#ifdef __cplusplus
extern "C" {
#endif
/* Immutable PostgreSQL statements shared by native readers and managed adapters.
 * Parameters are positional and their PostgreSQL types are part of the contract.
 * Unknown keys return NULL; callers must fail rather than invent fallback SQL. */
const char *laplace_sql_query_text(const char *name);
const char *laplace_sql_query_parameters(const char *name);
#ifdef __cplusplus
}
#endif
