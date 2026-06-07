#include <gtest/gtest.h>

#include <cstdint>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

#include "laplace/core/normalize_nfc.h"

namespace {

struct TestCase {
    std::vector<uint32_t> source;
    std::vector<uint32_t> nfc;
    std::vector<uint32_t> nfd;
    size_t                line_number;
    std::string           part;
};

static std::vector<uint32_t> parse_hex_seq(const std::string& s) {
    std::vector<uint32_t> out;
    std::istringstream iss(s);
    std::string tok;
    while (iss >> tok) {
        try { out.push_back((uint32_t)std::stoul(tok, nullptr, 16)); }
        catch (...) { break; }
    }
    return out;
}

static std::vector<TestCase> load(const std::string& path) {
    std::vector<TestCase> out;
    std::ifstream f(path);
    if (!f) { std::cerr << "cannot open " << path << "\n"; return out; }
    std::string line; size_t lineno = 0; std::string part = "@Part?";
    while (std::getline(f, line)) {
        ++lineno;
        if (lineno == 1 && line.size() >= 3
            && (unsigned char)line[0] == 0xef
            && (unsigned char)line[1] == 0xbb
            && (unsigned char)line[2] == 0xbf) line.erase(0, 3);
        auto hash = line.find('#');
        std::string comment = (hash == std::string::npos) ? "" : line.substr(hash);
        std::string data = (hash == std::string::npos) ? line : line.substr(0, hash);

        if (comment.find("@Part") != std::string::npos) {
            auto p = comment.find("@Part");
            part = comment.substr(p, 6);
        }

        auto first = data.find_first_not_of(" \t\r\n");
        auto last  = data.find_last_not_of(" \t\r\n");
        if (first == std::string::npos) continue;
        data = data.substr(first, last - first + 1);
        if (data.empty()) continue;

        std::vector<std::string> cols;
        size_t pos = 0;
        while (pos != std::string::npos) {
            size_t next = data.find(';', pos);
            if (next == std::string::npos) { cols.push_back(data.substr(pos)); break; }
            cols.push_back(data.substr(pos, next - pos));
            pos = next + 1;
        }
        if (cols.size() < 3) continue;
        TestCase tc; tc.line_number = lineno; tc.part = part;
        tc.source = parse_hex_seq(cols[0]);
        tc.nfc    = parse_hex_seq(cols[1]);
        tc.nfd    = parse_hex_seq(cols[2]);
        if (tc.source.empty() || tc.nfc.empty() || tc.nfd.empty()) continue;
        out.push_back(std::move(tc));
    }
    return out;
}

static std::vector<uint32_t> nfc(const std::vector<uint32_t>& in) {
    if (in.empty()) return {};
    size_t need = laplace_normalize_nfc(in.data(), in.size(), nullptr, 0);
    std::vector<uint32_t> out(need);
    size_t got = laplace_normalize_nfc(in.data(), in.size(), out.data(), out.size());
    out.resize(got);
    return out;
}

}

TEST(LaplaceCoreNormalizeNfc, UAX15ConformancePerLine) {
    const std::string path = std::string(LAPLACE_UCD_PATH_FOR_TESTS)
                           + "/NormalizationTest.txt";
    auto cases = load(path);
    ASSERT_FALSE(cases.empty()) << "could not load any tests from " << path;

    size_t pass = 0, fail = 0;
    std::vector<size_t> failed_lines;
    for (const auto& tc : cases) {
        bool a = nfc(tc.source) == tc.nfc;
        bool b = nfc(tc.nfc)    == tc.nfc;
        bool c = nfc(tc.nfd)    == tc.nfc;
        if (a && b && c) ++pass;
        else { ++fail; if (failed_lines.size() < 20) failed_lines.push_back(tc.line_number); }
    }
    std::cerr << "[NormalizeNFC conformance UCD " << LAPLACE_UCD_PATH_FOR_TESTS
              << "] pass=" << pass << " fail=" << fail << " total=" << cases.size() << "\n";
    if (fail > 0) {
        std::cerr << "First failing line numbers: ";
        for (auto ln : failed_lines) std::cerr << ln << " ";
        std::cerr << "\n";
    }
    EXPECT_EQ(0u, fail);
}
