#include <gtest/gtest.h>
#include <array>
#include <cstring>
#include "laplace/core/byte_atoms.h"
#include "laplace/core/super_fibonacci.h"

TEST(ByteAtoms, AllExistingIdsCoordinatesAndHilbertsMatchTheSharedNativeBasis) {
    laplace_byte_atoms_t basis{};
    ASSERT_EQ(laplace_byte_atoms_build(&basis), 0);
    ASSERT_EQ(laplace_byte_atoms_validate(&basis), 0);
    std::array<double, 128u * 4u> legacy{}, copied{};
    std::array<hash128_t, 128u> ids{};
    std::array<hilbert128_t, 128u> hilberts{};
    super_fibonacci(128u, legacy.data());
    ASSERT_EQ(laplace_byte_atoms_copy(ids.data(), copied.data(), hilberts.data(), ids.size()), 0);
    EXPECT_EQ(std::memcmp(legacy.data(), copied.data(), sizeof(legacy)), 0);
    for (size_t i = 0; i < 128u; ++i) {
        const uint8_t value = static_cast<uint8_t>(128u + i);
        hash128_t legacy_id;
        hilbert128_t legacy_hilbert;
        hash128_blake3(&value, 1u, &legacy_id);
        hilbert4d_encode(legacy.data() + i * 4u, &legacy_hilbert);
        EXPECT_TRUE(hash128_equals(&legacy_id, &ids[i]));
        EXPECT_EQ(std::memcmp(&legacy_hilbert, &hilberts[i], sizeof(legacy_hilbert)), 0);
        const laplace_byte_atom_t* found = nullptr;
        ASSERT_EQ(laplace_byte_atoms_lookup(&basis, &legacy_id, &found), 1);
        EXPECT_EQ(found, &basis.atoms[i]);
    }
    const uint8_t ascii = 'A';
    hash128_t id;
    hash128_blake3(&ascii, 1u, &id);
    const laplace_byte_atom_t* found = nullptr;
    EXPECT_EQ(laplace_byte_atoms_lookup(&basis, &id, &found), 0);
    EXPECT_EQ(found, nullptr);
    EXPECT_EQ(laplace_byte_atoms_copy(ids.data(), copied.data(), hilberts.data(), 127), -1);
}

TEST(ByteAtoms, MissingOrChangedBasisCannotPassAuthentication) {
    laplace_byte_atoms_t absent{};
    EXPECT_EQ(laplace_byte_atoms_validate(&absent), -1);
    laplace_byte_atoms_t basis{};
    ASSERT_EQ(laplace_byte_atoms_build(&basis), 0);
    const auto actual = basis;
    basis.atoms[0].coord[0] += 0.125;
    EXPECT_EQ(laplace_byte_atoms_validate(&basis), -1);
    basis = actual;
    basis.receipt.lo ^= 1u;
    EXPECT_EQ(laplace_byte_atoms_validate(&basis), -1);
    basis = actual;
    basis.index[0] = 65535;
    EXPECT_EQ(laplace_byte_atoms_validate(&basis), -1);
}
