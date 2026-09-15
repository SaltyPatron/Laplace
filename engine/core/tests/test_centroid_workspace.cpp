#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <random>
#include <vector>

#include "laplace/core/hash_composer.h"
#include "laplace/core/math4d.h"

namespace {

using Point = std::array<double, 4>;

std::vector<double> flatten(const std::vector<Point>& points) {
    std::vector<double> result;
    result.reserve(points.size() * 4u);
    for (const auto& point : points) result.insert(result.end(), point.begin(), point.end());
    return result;
}

Point independently_ordered_centroid(std::vector<Point> points) {
    std::sort(points.begin(), points.end());
    Point result{};
    for (const auto& point : points)
        for (size_t axis = 0; axis < 4u; ++axis) result[axis] += point[axis];
    const double inverse = points.empty() ? 0.0 : 1.0 / static_cast<double>(points.size());
    for (auto& value : result) value *= inverse;
    return result;
}

TEST(LaplaceCoreCentroidWorkspace, WidePermutationsMatchCanonicalOwnerAndIndependentOrderedReduction) {
    std::mt19937 random(19);
    for (const size_t count : {size_t{1}, size_t{2}, size_t{3}, size_t{64},
             size_t{65}, size_t{257}, size_t{2048}}) {
        SCOPED_TRACE(count);
        std::vector<Point> points(count);
        for (size_t i = 0; i < count; ++i)
            points[i] = Point{static_cast<double>((i * 13u) % 29u) / 32.0 - 0.5,
                static_cast<double>((i * 19u) % 31u) / 37.0 - 0.5,
                (i % 2u == 0u ? -1.0 : 1.0) * std::ldexp(1.0, -static_cast<int>(i % 55u)),
                i % 3u == 0u ? -0.0 : 0.0};
        const Point expected = independently_ordered_centroid(points);
        size_t required = 0;
        ASSERT_EQ(math4d_centroid_workspace_size(count, &required), 0);
        std::vector<uint8_t> workspace(required + 1u);
        for (unsigned permutation = 0; permutation < 5u; ++permutation) {
            std::shuffle(points.begin(), points.end(), random);
            const auto input = flatten(points);
            Point bounded{}, legacy{};
            ASSERT_EQ(math4d_centroid_with_workspace(input.data(), count,
                workspace.data() + 1u, required, bounded.data()), 0);
            math4d_centroid(input.data(), count, legacy.data());
            EXPECT_EQ(std::memcmp(bounded.data(), expected.data(), sizeof(Point)), 0);
            EXPECT_EQ(std::memcmp(bounded.data(), legacy.data(), sizeof(Point)), 0);
        }
    }
}

TEST(LaplaceCoreCentroidWorkspace, WideCancellationUsesCanonicalOrderRatherThanArrivalOrder) {
    std::vector<double> points(65u * 4u, 0.0);
    points[0] = 1.0;
    points[4] = -1.0;
    points[8] = std::ldexp(1.0, -54);
    size_t required = 0;
    ASSERT_EQ(math4d_centroid_workspace_size(65u, &required), 0);
    std::vector<uint8_t> workspace(required);
    Point output{};
    ASSERT_EQ(math4d_centroid_with_workspace(points.data(), 65u,
        workspace.data(), workspace.size(), output.data()), 0);
    EXPECT_DOUBLE_EQ(output[0], 0.0);
    EXPECT_NE(output[0], points[8] / 65.0);
}

TEST(LaplaceCoreCentroidWorkspace, InsufficientOrInvalidWorkspaceLeavesOutputUntouched) {
    const std::vector<double> points(65u * 4u, 0.125);
    size_t required = 0;
    ASSERT_EQ(math4d_centroid_workspace_size(65u, &required), 0);
    std::vector<uint8_t> workspace(required);
    const Point sentinel{3.0, -4.0, 5.0, -6.0};
    Point output = sentinel;
    EXPECT_NE(math4d_centroid_with_workspace(points.data(), 65u,
        workspace.data(), required - 1u, output.data()), 0);
    EXPECT_EQ(output, sentinel);
    EXPECT_NE(math4d_centroid_with_workspace(points.data(), 65u,
        nullptr, required, output.data()), 0);
    EXPECT_EQ(output, sentinel);
    EXPECT_NE(math4d_centroid_with_workspace(nullptr, 65u,
        workspace.data(), required, output.data()), 0);
    EXPECT_EQ(output, sentinel);
    EXPECT_NE(math4d_centroid_with_workspace(points.data(), SIZE_MAX,
        workspace.data(), required, output.data()), 0);
    EXPECT_EQ(output, sentinel);
    EXPECT_NE(math4d_centroid_workspace_size(SIZE_MAX, &required), 0);
    EXPECT_EQ(required, 0u);
    EXPECT_NE(math4d_centroid_workspace_size(1u, nullptr), 0);
}

TEST(LaplaceCoreCentroidWorkspace, CompositionPreservesIdentityAndHilbertAndPublishesNothingOnFailure) {
    std::vector<hash128_t> ids(97u);
    std::vector<Point> points(ids.size());
    for (size_t i = 0; i < ids.size(); ++i) {
        ids[i].lo = i + 1u;
        ids[i].hi = 17u;
        points[i] = Point{0.25, static_cast<double>(i % 17u) / 32.0, -0.125, 0.0};
    }
    const auto coordinates = flatten(points);
    hash128_t expected_id{};
    Point expected_coord{};
    hilbert128_t expected_hilbert{};
    hash_composer_compose_node(4, ids.data(), coordinates.data(), ids.size(),
        &expected_id, expected_coord.data(), &expected_hilbert);
    size_t required = 0;
    ASSERT_EQ(math4d_centroid_workspace_size(ids.size(), &required), 0);
    std::vector<uint8_t> workspace(required);
    const hash128_t sentinel_id{99, 101};
    const Point sentinel_coord{3.0, -4.0, 5.0, -6.0};
    hilbert128_t sentinel_hilbert{};
    std::memset(sentinel_hilbert.bytes, 0xa5, sizeof(sentinel_hilbert.bytes));
    auto actual_id = sentinel_id;
    auto actual_coord = sentinel_coord;
    auto actual_hilbert = sentinel_hilbert;
    EXPECT_NE(hash_composer_compose_node_with_workspace(200, ids.data(), coordinates.data(), ids.size(),
        workspace.data(), required - 1u, &actual_id, actual_coord.data(), &actual_hilbert), 0);
    EXPECT_TRUE(hash128_equals(&actual_id, &sentinel_id));
    EXPECT_EQ(actual_coord, sentinel_coord);
    EXPECT_EQ(std::memcmp(&actual_hilbert, &sentinel_hilbert, sizeof(actual_hilbert)), 0);
    ASSERT_EQ(hash_composer_compose_node_with_workspace(200, ids.data(), coordinates.data(), ids.size(),
        workspace.data(), required, &actual_id, actual_coord.data(), &actual_hilbert), 0);
    EXPECT_TRUE(hash128_equals(&actual_id, &expected_id));
    EXPECT_EQ(std::memcmp(actual_coord.data(), expected_coord.data(), sizeof(Point)), 0);
    EXPECT_EQ(std::memcmp(&actual_hilbert, &expected_hilbert, sizeof(actual_hilbert)), 0);
    hash128_t independent_id{};
    hash128_merkle(0, ids.data(), ids.size(), &independent_id);
    EXPECT_TRUE(hash128_equals(&actual_id, &independent_id));
}

TEST(LaplaceCoreCentroidWorkspace, EmptyAndSingletonCompositionRetainExistingCollapseLaws) {
    size_t required = 99;
    ASSERT_EQ(math4d_centroid_workspace_size(0, &required), 0);
    EXPECT_EQ(required, 0u);
    hash128_t id{};
    Point coord{1.0, 2.0, 3.0, 4.0};
    hilbert128_t hilbert{};
    ASSERT_EQ(hash_composer_compose_node_with_workspace(4, nullptr, nullptr, 0,
        nullptr, 0, &id, coord.data(), &hilbert), 0);
    const hash128_t zero{};
    const hilbert128_t zero_hilbert{};
    EXPECT_TRUE(hash128_equals(&id, &zero));
    EXPECT_EQ(coord, Point{});
    EXPECT_EQ(std::memcmp(&hilbert, &zero_hilbert, sizeof(hilbert)), 0);
    const hash128_t child{19, 23};
    const Point point{0.25, -0.5, 0.125, -0.0};
    ASSERT_EQ(math4d_centroid_workspace_size(1, &required), 0);
    std::vector<uint8_t> workspace(required);
    ASSERT_EQ(hash_composer_compose_node_with_workspace(4, &child, point.data(), 1,
        workspace.data(), required, &id, coord.data(), &hilbert), 0);
    EXPECT_TRUE(hash128_equals(&id, &child));
    Point legacy{};
    math4d_centroid(point.data(), 1, legacy.data());
    EXPECT_EQ(std::memcmp(coord.data(), legacy.data(), sizeof(Point)), 0);
}

} // namespace
