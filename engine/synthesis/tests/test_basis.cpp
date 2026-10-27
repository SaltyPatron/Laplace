#include <gtest/gtest.h>
#include "laplace/synthesis/basis.h"
#include "laplace/synthesis/lookup_compile.h"
#include <algorithm>
#include <cmath>
#include <numeric>
#include <vector>

TEST(LaplaceSynthesisBasis, FullFrameAlignmentRetainsHigherDimensionsAndCanonicalIdentity) {
    constexpr size_t n=10,d=12,rank=6;
    std::vector<int> input(n);std::iota(input.begin(),input.end(),0);input[9]=2;
    std::vector<double> e(n*d,0),coordinates(n*4,0);
    std::vector<double> q(9*rank);double maximum=0;
    const double pi=std::acos(-1.0);
    for(size_t x=0;x<9;x++) {
        double norm=0;for(size_t c=0;c<rank;c++) {
            q[x*rank+c]=std::sqrt(0.2)*std::sin(pi*(x+1)*(c+1)/10);norm+=q[x*rank+c]*q[x*rank+c];
            e[x*d+c]=(c+1)*q[x*rank+c]+(c>0?0.25*q[x*rank+c-1]:0);
        }
        maximum=std::max(maximum,norm);
        for(size_t c=0;c<4;c++)coordinates[x*4+c]=2*q[x*rank+(c+1)%4];
    }
    const auto original_coordinates=coordinates;const auto original_input=input;
    laplace_basis_receipt receipt{};
    ASSERT_EQ(laplace_basis_refine(e.data(),n,d,rank,coordinates.data(),input.data(),&receipt),0);
    EXPECT_EQ(coordinates,original_coordinates);EXPECT_EQ(input,original_input);
    EXPECT_EQ(receipt.rank,rank);EXPECT_EQ(receipt.anchors,9);
    EXPECT_LT(receipt.orthogonality_error,1e-12);EXPECT_LT(receipt.span_relative_error,1e-12);
    EXPECT_LT(receipt.frame_orthogonality_error,1e-12);
    EXPECT_LT(receipt.anchor_error_after,receipt.anchor_error_before);
    // The complete orthogonal frame preserves every pair's feature dot product.
    // Both independent directions beyond the four anchor axes must survive.
    double scale_squared=(d-1)/maximum;
    for(size_t x=0;x<9;x++)for(size_t y=0;y<9;y++) {
        double dot=0,expected=0,nx=0,ny=0,qx=0,qy=0;
        for(size_t c=0;c<rank;c++) {
            dot+=e[x*d+c]*e[y*d+c];expected+=q[x*rank+c]*q[y*rank+c];
            nx+=e[x*d+c]*e[x*d+c];ny+=e[y*d+c]*e[y*d+c];
            qx+=q[x*rank+c]*q[x*rank+c];qy+=q[y*rank+c]*q[y*rank+c];
        }
        EXPECT_NEAR(dot,scale_squared*expected,1e-11);
        EXPECT_NEAR(dot/std::sqrt(nx*ny),expected/std::sqrt(qx*qy),1e-12);
    }
    double complement_energy=0;for(size_t x=0;x<9;x++)for(size_t c=4;c<rank;c++)complement_energy+=e[x*d+c]*e[x*d+c];
    EXPECT_NEAR(complement_energy,2*scale_squared,1e-11);
    for(size_t x=0;x<n;x++) {
        double norm=0;for(size_t c=0;c<d;c++)norm+=e[x*d+c]*e[x*d+c];
        EXPECT_NEAR(norm,d,1e-11);
    }
    EXPECT_TRUE(std::equal(e.begin()+9*d,e.end(),e.begin()+2*d));
}

TEST(LaplaceSynthesisBasis, MissingAnchorsDoNotInventAnAlignment) {
    constexpr size_t n=5,d=8,rank=3;
    std::vector<int> input(n);std::iota(input.begin(),input.end(),0);
    std::vector<double> e(n*d,0),coordinates(n*4,0);
    for(size_t c=0;c<rank;c++)e[c*d+c]=c+1;
    laplace_basis_receipt receipt{};
    ASSERT_EQ(laplace_basis_refine(e.data(),n,d,rank,coordinates.data(),input.data(),&receipt),0);
    EXPECT_EQ(receipt.anchors,0);EXPECT_DOUBLE_EQ(receipt.anchor_error_before,receipt.anchor_error_after);
    for(size_t c=0;c<rank;c++)EXPECT_NEAR(e[c*d+c],std::sqrt(d-1),1e-12);
    e.assign(n*d,0);
    EXPECT_NE(laplace_basis_refine(e.data(),n,d,rank,coordinates.data(),input.data(),&receipt),0);
    EXPECT_NE(laplace_basis_refine(e.data(),n,1,rank,coordinates.data(),input.data(),&receipt),0);
}

TEST(LaplaceSynthesisBasis, EqualCentroidsRemainDistinguishableThroughTheirDifferentGraphOperands) {
    constexpr size_t n=16,d=12;
    std::vector<int> input(n),rows,cols;std::iota(input.begin(),input.end(),0);
    std::vector<double> coordinates(n*4,0),weights,e(n*d);
    for(size_t x=0;x<n;x++) {
        coordinates[x*4]=1;coordinates[x*4+1]=std::sin(double(x));
        if(x+1<n){rows.push_back(x);cols.push_back(x+1);weights.push_back(1+double(x)/n);}
    }
    std::copy_n(coordinates.data()+3*4,4,coordinates.data()+4*4);
    auto original=coordinates;size_t features=0;
    laplace_spectral_receipt spectral{};
    ASSERT_EQ(laplace_spectral_embed(n,d,input.data(),coordinates.data(),rows.data(),cols.data(),weights.data(),weights.size(),e.data(),&features,&spectral),0);
    EXPECT_EQ(spectral.active_vertices,n);EXPECT_EQ(spectral.components,1);EXPECT_EQ(spectral.retained_modes,d-6);
    EXPECT_LT(spectral.eigen_residual_max,1e-8);EXPECT_LT(spectral.mass_orthogonality_error,1e-10);
    ASSERT_GT(features,6);ASSERT_LE(features,d);
    laplace_basis_receipt receipt{};
    ASSERT_EQ(laplace_basis_refine(e.data(),n,d,features-2,coordinates.data(),input.data(),&receipt),0);
    EXPECT_EQ(coordinates,original);double distance=0;
    for(size_t c=0;c<d;c++)distance+=std::pow(e[3*d+c]-e[4*d+c],2);
    EXPECT_GT(distance,1e-4);EXPECT_LT(receipt.span_relative_error,1e-12);
}

TEST(LaplaceSynthesisBasis, RecompiledScoreAndValueEquationsSurviveAnInvertibleBasisChange) {
    constexpr size_t n=3,d=3;
    double r[]={1,2,0, 0,1,1, 1,0,1},b[]={2,1,0, 0,1,0.5, 0,0,3},rp[9]{};
    for(size_t x=0;x<n;x++)for(size_t c=0;c<d;c++)for(size_t z=0;z<d;z++)rp[x*d+c]+=r[x*d+z]*b[z*d+c];
    int rows[]={0,0,1,2},cols[]={1,2,2,0};double weights[]={2,-1,0.75,3},a[9]{};
    for(size_t e=0;e<4;e++)a[rows[e]*n+cols[e]]+=weights[e];
    double inverse[]={0.5,-0.5,1.0/12, 0,1,-1.0/6, 0,0,1.0/3};
    double original_score[9]{},original_value[9]{};
    for(auto* representation:{r,rp}) {
        double score[9],value[9],score_error,value_error;size_t rank=0;
        ASSERT_EQ(laplace_operator_compile(representation,n,d,rows,cols,weights,4,score,value,&rank,&score_error,&value_error),0);
        EXPECT_EQ(rank,d);
        if(representation==r) {
            std::copy_n(score,9,original_score);std::copy_n(value,9,original_value);
        } else {
            double uncompensated_error=0;
            for(size_t i=0;i<d;i++)for(size_t j=0;j<d;j++) {
                double expected_score=0,expected_value=0,uncompensated=0;
                for(size_t z=0;z<d;z++)for(size_t w=0;w<d;w++) {
                    expected_score+=inverse[i*d+z]*original_score[z*d+w]*inverse[j*d+w];
                    expected_value+=inverse[i*d+z]*original_value[z*d+w]*b[w*d+j];
                    uncompensated+=rp[i*d+z]*original_score[z*d+w]*rp[j*d+w];
                }
                EXPECT_NEAR(score[i*d+j],expected_score,1e-11);EXPECT_NEAR(value[i*d+j],expected_value,1e-11);
                uncompensated_error+=std::pow(uncompensated-a[i*n+j],2);
            }
            EXPECT_GT(uncompensated_error,1);
        }
        for(size_t x=0;x<n;x++)for(size_t y=0;y<n;y++) {
            double actual=0;for(size_t i=0;i<d;i++)for(size_t j=0;j<d;j++)actual+=representation[x*d+i]*score[i*d+j]*representation[y*d+j];
            EXPECT_NEAR(actual,a[x*n+y],1e-11);
        }
        for(size_t x=0;x<n;x++)for(size_t c=0;c<d;c++) {
            double actual=0,target=0;
            for(size_t z=0;z<d;z++)actual+=representation[x*d+z]*value[z*d+c];
            for(size_t y=0;y<n;y++)target+=a[x*n+y]*representation[y*d+c];
            EXPECT_NEAR(actual,target,1e-11);
        }
    }
}
