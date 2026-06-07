#include <gtest/gtest.h>

#include <cstdint>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <string_view>
#include <vector>

#include "laplace/core/grapheme_break.h"

namespace {

struct TestCase {
    std::vector<uint32_t> codepoints;
    std::vector<size_t>   breaks;
    std::string           comment;
    size_t                line_number;
};

constexpr const char* kBreakMarker = "\xc3\xb7";
constexpr const char* kNoBreakMarker = "\xc3\x97";

static std::vector<TestCase> load_conformance(const std::string& path) {
    std::vector<TestCase> out;
    std::ifstream f(path);
    if (!f) {
        std::cerr << "cannot open conformance file: " << path << "\n";
        return out;
    }
    std::string line;
    size_t lineno = 0;
    while (std::getline(f, line)) {
        ++lineno;
        if (lineno == 1 && line.size() >= 3
            && (unsigned char)line[0] == 0xef
            && (unsigned char)line[1] == 0xbb
            && (unsigned char)line[2] == 0xbf) {
            line.erase(0, 3);
        }
        std::string comment;
        auto hash = line.find('#');
        if (hash != std::string::npos) {
            comment = line.substr(hash);
            line = line.substr(0, hash);
        }
        auto first = line.find_first_not_of(" \t\r\n");
        auto last  = line.find_last_not_of(" \t\r\n");
        if (first == std::string::npos) continue;
        line = line.substr(first, last - first + 1);
        if (line.empty()) continue;

        TestCase tc;
        tc.comment = comment;
        tc.line_number = lineno;

        std::istringstream iss(line);
        std::string tok;
        size_t cp_pos = 0;
        while (iss >> tok) {
            if (tok == kBreakMarker) {
                tc.breaks.push_back(cp_pos);
            } else if (tok == kNoBreakMarker) {
            } else {
                uint32_t cp;
                try { cp = (uint32_t)std::stoul(tok, nullptr, 16); }
                catch (...) { continue; }
                tc.codepoints.push_back(cp);
                cp_pos += 1;
            }
        }
        out.push_back(std::move(tc));
    }
    return out;
}

static std::vector<size_t> segment_all(const std::vector<uint32_t>& cps) {
    std::vector<size_t> b;
    b.push_back(0);
    size_t pos = 0;
    while (true) {
        size_t nxt = laplace_grapheme_break_next(cps.data(), cps.size(), pos);
        b.push_back(nxt);
        if (nxt >= cps.size()) break;
        pos = nxt;
    }
    return b;
}

}

TEST(LaplaceCoreGraphemeBreak, UAX29ConformancePerLine) {
    const std::string path = std::string(LAPLACE_UCD_PATH_FOR_TESTS)
                           + "/auxiliary/GraphemeBreakTest.txt";
    auto cases = load_conformance(path);
    ASSERT_FALSE(cases.empty()) << "could not load any tests from " << path;

    size_t pass = 0, fail = 0;
    std::vector<size_t> failed_lines;
    failed_lines.reserve(64);
    for (const auto& tc : cases) {
        auto got = segment_all(tc.codepoints);
        if (got == tc.breaks) {
            pass += 1;
        } else {
            fail += 1;
            if (failed_lines.size() < 20) failed_lines.push_back(tc.line_number);
        }
    }

    std::cerr << "[GraphemeBreak conformance UCD " << LAPLACE_UCD_PATH_FOR_TESTS
              << "] pass=" << pass << " fail=" << fail
              << " total=" << cases.size() << "\n";
    if (fail > 0) {
        std::cerr << "First failing line numbers: ";
        for (auto ln : failed_lines) std::cerr << ln << " ";
        std::cerr << "\n";
    }
    EXPECT_EQ(0u, fail) << "first failing line(s) above";
}
