namespace Laplace.Engine.Synthesis;

public static class MklAvailability
{
    public static void EnsureOrThrow()
    {
        int initRc = NativeInterop.LaplaceSynthesisInit();
        if (initRc == -2)
            throw new InvalidOperationException(
                "Intel MKL is required but laplace_synthesis_init returned -2. "
                + "Set MKLROOT/TBBROOT/CMPLR_ROOT and rebuild with -DLAPLACE_SYNTHESIS_REQUIRE_MKL=ON.");
        if (initRc != 0)
            throw new InvalidOperationException(
                $"laplace_synthesis_init failed (rc={initRc}); MKL CBWR or runtime init misconfigured.");

        unsafe
        {
            float[] a = [1.0f, 0.0f, 0.0f, 1.0f];
            float[] u = new float[4];
            float[] s = new float[2];
            float[] vt = new float[4];
            nuint rank = 0;
            fixed (float* pa = a)
            fixed (float* pu = u)
            fixed (float* ps = s)
            fixed (float* pvt = vt)
            {
                int rc = NativeInterop.TensorSvdTruncate(
                    pa, 2, 2, 0.0, &rank, pu, ps, pvt, 2);
                if (rc == -2)
                    throw new InvalidOperationException(
                        "Intel MKL is required but tensor_svd_truncate returned -2.");
            }
        }
    }
}
