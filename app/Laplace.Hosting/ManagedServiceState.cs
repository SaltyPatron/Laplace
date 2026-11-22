namespace Laplace.Hosting;

/// <summary>
/// The operator's persisted stop of a managed service. On Linux it is
/// <c>/var/lib/laplace-managed/&lt;name&gt;.stopped</c>. On Windows it is
/// <c>%ProgramData%\Laplace\managed\&lt;name&gt;.stopped</c>. Services check this at
/// startup and exit 0 so a reboot or redeploy never undoes an operator's stop.
/// </summary>
public static class ManagedServiceState
{
    public static string Directory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Laplace", "managed")
        : "/var/lib/laplace-managed";

    public static string StopMarker(string service) => Path.Combine(Directory, service + ".stopped");

    public static bool OperatorStopped(string service)
        => OperatorStoppedAtPath(StopMarker(service));

    internal static bool OperatorStoppedAtPath(string marker)
    {
        try
        {
            var attributes = File.GetAttributes(marker);
            if ((attributes & FileAttributes.Directory) != 0)
                throw new InvalidDataException("The managed-service stop marker is a directory: " + marker);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
