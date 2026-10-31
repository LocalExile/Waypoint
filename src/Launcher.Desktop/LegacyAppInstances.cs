using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Launcher.Core;
using Microsoft.Win32.SafeHandles;

namespace Launcher.Desktop;

/// <summary>Best-effort compatibility check for older, non-cooperating Waypoint builds. Never loads their code.</summary>
public static class LegacyAppInstances
{
    public static IEnumerable<AppInstanceIdentity> Find()
    {
        using var current = Process.GetCurrentProcess();
        using var caller = WindowsIdentity.GetCurrent(); string user = caller.User!.Value;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.ProcessName, "Waypoint", "Launcher.Desktop" };
        var result = new List<AppInstanceIdentity>();
        foreach (var process in Process.GetProcesses())
        using (process)
        {
            try
            {
                if (process.Id == current.Id || !names.Contains(process.ProcessName) || process.SessionId != current.SessionId) continue;
                using var handle = OpenProcess(0x1000, false, process.Id); // Query only, including across elevation where Windows permits it.
                if (handle.IsInvalid || !OpenProcessToken(handle, 8, out var token)) continue;
                using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                    if (identity.User?.Value != user) continue;
                var path = new StringBuilder(32768); int length = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref length)) continue;
                var found = InspectExecutable(path.ToString(), process.Id);
                if (found is not null) result.Add(found);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or JsonException or BadImageFormatException or KeyNotFoundException or System.Security.SecurityException) { }
        }
        return result;
    }
    public static AppInstanceIdentity? InspectExecutable(string executable, int processId)
    {
        string directory = Path.GetDirectoryName(executable)!;
        string brandPath = Path.Combine(directory, "brand.json");
        if (!File.Exists(brandPath) || new FileInfo(brandPath).Length > 16384 || !File.Exists(Path.Combine(directory, "Launcher.Core.dll"))) return null;
        using var brand = JsonDocument.Parse(File.ReadAllText(brandPath));
        string? name = brand.RootElement.GetProperty("executableName").GetString();
        if (name is null || name != Path.GetFileName(name)) return null;
        string dll = Path.Combine(directory, name + ".dll");
        if (!File.Exists(dll)) dll = Path.Combine(directory, "Launcher.Desktop.dll");
        if (!File.Exists(dll)) return null;
        if (!string.Equals(FileVersionInfo.GetVersionInfo(executable).OriginalFilename, Path.GetFileName(dll), StringComparison.OrdinalIgnoreCase)) return null;
        using var file = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var pe = new PEReader(file);
        if (!pe.HasMetadata) return null;
        var reader = pe.GetMetadataReader();
        if (!reader.IsAssembly || !reader.AssemblyReferences.Any(h => reader.GetString(reader.GetAssemblyReference(h).Name) == "Launcher.Core")) return null;
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)parent);
            if (reader.GetString(type.Namespace) != "System.Reflection" || reader.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            var value = reader.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() == 1 && value.ReadSerializedString() == "Waypoint.DesktopProtocol" && value.ReadSerializedString() == "1") return null;
        }
        return new(processId, brand.RootElement.GetProperty("version").GetString() ?? "unknown",
            FileVersionInfo.GetVersionInfo(dll).ProductVersion ?? "unknown", executable);
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}
