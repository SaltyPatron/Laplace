#include <gtest/gtest.h>

#include "laplace/core/codepoint_table.h"

/* Real tests land Chunk 3 — UCD-derived entries match the DB seed
 * sibling artifact byte-for-byte; lookup-by-codepoint is O(1); the
 * mmap'd perf-cache survives a process restart. Stub for now. */

TEST(LaplaceCoreCodepointTable, StubLookupReturnsNull) {
    const codepoint_entry_t* e = codepoint_table_lookup(65 /* 'A' */);
    EXPECT_EQ(e, nullptr);
}
