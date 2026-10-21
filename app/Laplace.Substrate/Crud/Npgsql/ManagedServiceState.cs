namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The operator's persisted stop of a managed service. On Linux it is
/// <c>/var/lib/laplace-managed/&lt;name&gt;.stopped</c>, written by the root helper
/// (deploy/linux/laplace-service-control) and honored by the unit's
/// <c>ConditionPathExists=!</c>. On Windows it is
/// <c>%ProgramData%\Laplace\managed\&lt;name&gt;.stopped</c>, written by the API's service
/// control (the directory grants the site's pool identity, scripts/win/ensure-managed-services.ps1)
/// and honored by the service process itself, which exits 0 at start (NSSM's AppExit 0 is Exit)
/// so a reboot or a redeploy never undoes an operator's stop.
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
            // File.Exists also returns false on access and IO failures. Starting
            // in that state would silently undo an operator's persisted stop.
            var attributes = File.GetAttributes(marker);
            if ((attributes & FileAttributes.Directory) != 0)
                throw new InvalidDataException("The managed-service stop marker is a directory: " + marker);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
