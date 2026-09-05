#include <gtest/gtest.h>
#include "laplace/core/xml_stream.h"
#include <algorithm>
#include <string>
#include <vector>

namespace {
std::vector<std::string> parse(const std::string& xml, size_t chunk) {
    laplace_xml_stream_t* stream = nullptr;
    EXPECT_EQ(0, laplace_xml_stream_new(&stream));
    std::vector<std::string> result;
    for (size_t offset = 0; offset < xml.size();) {
        size_t length = std::min(chunk, xml.size() - offset);
        const laplace_xml_event_t* events = nullptr;
        size_t count = 0;
        int rc = laplace_xml_stream_feed(stream,
            (const uint8_t*)xml.data() + offset, length,
            offset + length == xml.size(), &events, &count);
        EXPECT_EQ(0, rc) << laplace_xml_stream_error(stream);
        for (size_t i = 0; i < count; ++i) {
            const auto& e = events[i];
            std::string key = std::to_string(e.kind) + ":" + std::to_string(e.depth) + ":" + e.name + ":";
            std::string value(e.value, e.value_len);
            if (e.kind == 3 && !result.empty() && result.back().rfind(key, 0) == 0)
                result.back() += value;
            else result.push_back(key + value);
        }
        offset += length;
    }
    laplace_xml_stream_free(stream);
    return result;
}
}
TEST(XmlStream, ChunkBoundariesPreserveDecodedContentAndNestedStructure) {
    const std::string xml = "<?xml version='1.0'?><r xmlns:x='urn:test'><x:e a='a&amp;b&#xA;β'>a&lt;b<![CDATA[<β>]]><empty/> tail</x:e></r>";
    const auto expected = parse(xml, xml.size());
    EXPECT_EQ((std::vector<std::string>{"1:0:r:", "1:1:e:", "4:1:a:a&b\nβ", "3:1::a<b<β>", "1:2:empty:", "2:2:empty:", "3:1:: tail", "2:1:e:", "2:0:r:"}), expected);
    for (size_t chunk : {1, 2, 3, 7, 31}) EXPECT_EQ(expected, parse(xml, chunk));
}
TEST(XmlStream, PublicDtdDoesNotRequireAnotherArtifact) {
    auto events = parse("<!DOCTYPE r SYSTEM 'file:///nonexistent/laplace.dtd'><r>&amp;&#x41;</r>", 3);
    EXPECT_EQ((std::vector<std::string>{"1:0:r:", "3:0::&A", "2:0:r:"}), events);
}
TEST(XmlStream, MalformedAndUnresolvedEntityFailWithoutAnAcceptedPartialBatch) {
    for (const std::string xml : {"<r><a></r>", "<r>&missing;</r>", "<r>&#0;</r>", "<!DOCTYPE r [<!ENTITY x SYSTEM 'file:///etc/hostname'>]><r>&x;</r>"}) {
        laplace_xml_stream_t* stream = nullptr;
        ASSERT_EQ(0, laplace_xml_stream_new(&stream));
        const laplace_xml_event_t* events = nullptr; size_t count = 99;
        EXPECT_EQ(-2, laplace_xml_stream_feed(stream, (const uint8_t*)xml.data(), xml.size(), 1, &events, &count));
        EXPECT_EQ(0u, count); EXPECT_EQ(nullptr, events);
        EXPECT_NE(std::string(), laplace_xml_stream_error(stream));
        laplace_xml_stream_free(stream);
    }
}
TEST(XmlStream, NamespacesDistinguishEqualLocalNames) {
    const std::string xml = "<r xmlns:a='urn:a' xmlns:b='urn:b' a:id='1' b:id='2'><a:item/><b:item/></r>";
    laplace_xml_stream_t* stream = nullptr;
    ASSERT_EQ(0, laplace_xml_stream_new(&stream));
    const laplace_xml_event_t* events = nullptr; size_t count = 0;
    ASSERT_EQ(0, laplace_xml_stream_feed(stream, (const uint8_t*)xml.data(), xml.size(), 1, &events, &count));
    std::vector<std::string> names;
    for (size_t i = 0; i < count; ++i)
        if (events[i].kind == 4 || (events[i].kind == 1 && events[i].depth == 1))
            names.push_back(std::string(events[i].namespace_uri) + ":" + events[i].prefix + ":" + events[i].name);
    EXPECT_EQ((std::vector<std::string>{"urn:a:a:id", "urn:b:b:id", "urn:a:a:item", "urn:b:b:item"}), names);
    laplace_xml_stream_free(stream);
}
