namespace Laplace.Engine.Dynamics;

public static class MklAvailability
{
    public static void EnsureOrThrow()
    {
        unsafe
        {
            double[] a = [1.0, 0.0];
            double[] b = [0.0, 1.0];
            int[] rows = [0];
            int[] cols = [1];
            double[] vals = [1.0];
            long[] scores = [0];
            nuint cap = 1;
            nuint written = 0;
            int overflow = 0;
            fixed (double* pa = a)
            fixed (double* pb = b)
            fixed (int* pr = rows)
            fixed (int* pc = cols)
            fixed (double* pv = vals)
            fixed (long* ps = scores)
            {
                int rc = NativeInterop.BilinearEdgesTile(
                    pa, 0, 1, pb, 1, 1, 1e-6, pr, pc, pv, ps, cap, &written, &overflow);
                if (rc == -2)
                    throw new InvalidOperationException(
                        "Intel MKL is required but laplace_dynamics bilinear_edges_tile returned -2. "
                        + "Set MKLROOT/TBBROOT/CMPLR_ROOT and rebuild native libraries.");
            }
        }
    }
}
