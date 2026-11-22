using System.Runtime.InteropServices;
using System.Text;
using Laplace.Native;

namespace Laplace.Engine.Core;

public static unsafe class TrustClassRegistry
{
    public static Hash128 Id(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _ = Prior(name);
        return Hash128.Text(name);
    }

    public static double Prior(Hash128 trustClassId)
    {
        void* text = NativeTextIdentity.ThreadContext();
        lp_id nid = trustClassId.ToNative();
        lp_trust_class* tc = LaplaceNative.lp_trust_class_for_id(text, &nid);
        if (tc == null)
            throw new InvalidOperationException($"No governed witness prior is registered for trust class {trustClassId}.");
        return tc->prior;
    }

    public static double Prior(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        byte[] utf8 = Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* p = utf8)
        {
            lp_trust_class* tc = LaplaceNative.lp_trust_class_named(p);
            if (tc == null)
                throw new InvalidOperationException($"trust class '{name}' is not declared in manifest/trust_classes.toml");
            return tc->prior;
        }
    }
}
