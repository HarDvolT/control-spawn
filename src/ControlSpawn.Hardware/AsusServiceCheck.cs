using System.ServiceProcess;

namespace ControlSpawn.Hardware;

public sealed record ServiceState(string Name, bool Running, string? Error = null);
public interface IServiceQuery { IReadOnlyList<ServiceState> Query(); }

/// <summary>Read-only adapter. Never used by mock mode or unit tests.</summary>
public sealed class WindowsServiceQuery : IServiceQuery
{
    public IReadOnlyList<ServiceState> Query()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var result = new List<ServiceState>();
        foreach (var service in ServiceController.GetServices())
        {
            using (service)
            {
                string name = service.ServiceName;
                if (!AsusServiceCheck.IsConflict(name)) continue;
                try { result.Add(new(name, service.Status == ServiceControllerStatus.Running)); }
                catch (Exception ex) { result.Add(new(name, false, ex.Message)); }
            }
        }
        return result;
    }
}
public sealed class AsusServiceCheck(IServiceQuery query)
{
    public static bool IsConflict(string name) => name.Equals("AsusFanControlService", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LightingService", StringComparison.OrdinalIgnoreCase) || name.Contains("iCUE", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Corsair", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<ServiceState> Read() => query.Query().Where(s => IsConflict(s.Name)).ToArray();
}
