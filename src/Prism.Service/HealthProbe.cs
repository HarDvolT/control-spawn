using System.Security.Principal;
using System.ServiceProcess;
using Prism.Hardware;

namespace Prism.Service;

public sealed record HealthSnapshot(bool Elevated, IReadOnlyList<string> Warnings);
public interface IHealthProbe { HealthSnapshot Read(); }
public sealed class FakeHealthProbe : IHealthProbe
{
    public HealthSnapshot Read() => new(false, []);
}
public sealed class WindowsHealthProbe : IHealthProbe
{
    public HealthSnapshot Read()
    {
        using var identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var warnings = new List<string>();
        if (!elevated) warnings.Add("Prism is not elevated.");
        foreach (var service in ServiceController.GetServices())
        {
            using (service)
            {
                if (!AsusServiceCheck.IsConflict(service.ServiceName)) continue;
                try { if (service.Status == ServiceControllerStatus.Running) warnings.Add($"{service.ServiceName} is running."); }
                catch (Exception) { warnings.Add($"Cannot query {service.ServiceName}."); }
            }
        }
        // GetServices excludes kernel drivers. Query the named driver service directly instead.
        try { using var pawnIo = new ServiceController("PawnIO"); _ = pawnIo.Status; }
        catch (InvalidOperationException e) when (e.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 })
        { warnings.Add("PawnIO is missing."); }
        catch (Exception) { warnings.Add("Cannot query PawnIO status."); }
        return new(elevated, warnings);
    }
}
