using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PrivacyBrowser.Desktop;

internal sealed record WindowsAudioOutput(string DeviceId, string DisplayName);

internal static class WindowsAudioRouter
{
  private const string AudioPolicyRuntimeClass = "Windows.Media.Internal.AudioPolicyConfig";
  private const string MmDevicePrefix = @"\\?\SWD#MMDEVAPI#";
  private const string RenderInterfaceSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

  internal static IReadOnlyList<WindowsAudioOutput> EnumerateOutputs()
  {
    try
    {
      using var enumerator = new MMDeviceEnumerator();
      return enumerator
          .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
          .Select(device => new WindowsAudioOutput(device.ID, device.FriendlyName))
          .Where(device => !string.IsNullOrWhiteSpace(device.DeviceId))
          .DistinctBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
          .OrderBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
          .ToList();
    }
    catch
    {
      return [];
    }
  }

  internal static bool TryRoute(IEnumerable<int> rootProcessIds, string? endpointId, out string? error)
  {
    error = null;
    if (!OperatingSystem.IsWindows()) return true;
    var processIds = DescendantProcessIds(rootProcessIds).Distinct().ToArray();
    if (processIds.Length == 0) return true;

    object? rawFactory = null;
    IntPtr endpointHString = IntPtr.Zero;
    try
    {
      rawFactory = CreatePolicyFactory();
      if (!string.IsNullOrWhiteSpace(endpointId))
      {
        var packedId = MmDevicePrefix + endpointId + RenderInterfaceSuffix;
        WindowsCreateString(packedId, (uint)packedId.Length, out endpointHString);
      }

      foreach (var processId in processIds)
      {
        if (rawFactory is IAudioPolicyConfigFactory21H2 modern)
        {
          ThrowIfFailed(modern.SetPersistedDefaultAudioEndpoint((uint)processId, AudioDataFlow.Render, AudioRole.Multimedia, endpointHString));
          ThrowIfFailed(modern.SetPersistedDefaultAudioEndpoint((uint)processId, AudioDataFlow.Render, AudioRole.Console, endpointHString));
          ThrowIfFailed(modern.SetPersistedDefaultAudioEndpoint((uint)processId, AudioDataFlow.Render, AudioRole.Communications, endpointHString));
        }
        else if (rawFactory is IAudioPolicyConfigFactoryDownlevel legacy)
        {
          ThrowIfFailed(legacy.SetPersistedDefaultAudioEndpoint((uint)processId, AudioDataFlow.Render, AudioRole.Multimedia, endpointHString));
          ThrowIfFailed(legacy.SetPersistedDefaultAudioEndpoint((uint)processId, AudioDataFlow.Render, AudioRole.Console, endpointHString));
          ThrowIfFailed(legacy.SetPersistedDefaultAudioEndpoint((uint)processId, AudioDataFlow.Render, AudioRole.Communications, endpointHString));
        }
      }
      return true;
    }
    catch (Exception exception)
    {
      error = exception.Message;
      return false;
    }
    finally
    {
      if (endpointHString != IntPtr.Zero) WindowsDeleteString(endpointHString);
      if (rawFactory is not null && Marshal.IsComObject(rawFactory)) Marshal.FinalReleaseComObject(rawFactory);
    }
  }

  private static object CreatePolicyFactory()
  {
    Exception? lastError = null;
    foreach (var iidValue in new[]
             {
               typeof(IAudioPolicyConfigFactory21H2).GUID,
               typeof(IAudioPolicyConfigFactoryDownlevel).GUID,
             })
    {
      var iid = iidValue;
      try
      {
        RoGetActivationFactory(AudioPolicyRuntimeClass, ref iid, out var factory);
        return factory;
      }
      catch (Exception error)
      {
        lastError = error;
      }
    }
    throw new InvalidOperationException("Windows 오디오 출력 정책을 열 수 없습니다.", lastError);
  }

  private static IEnumerable<int> DescendantProcessIds(IEnumerable<int> roots)
  {
    var seed = roots.Where(id => id > 0).Append(Environment.ProcessId).Distinct().ToArray();
    var processes = SnapshotProcesses();
    var selected = new HashSet<int>(seed);
    var queue = new Queue<int>(seed);
    while (queue.Count > 0)
    {
      var parent = queue.Dequeue();
      foreach (var child in processes.Where(process => process.ParentId == parent))
      {
        if (selected.Add(child.ProcessId)) queue.Enqueue(child.ProcessId);
      }
    }
    return selected;
  }

  private static IReadOnlyList<ProcessLink> SnapshotProcesses()
  {
    var result = new List<ProcessLink>();
    var snapshot = CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
    if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;
    try
    {
      var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
      if (!Process32First(snapshot, ref entry)) return result;
      do
      {
        result.Add(new ProcessLink((int)entry.ProcessId, (int)entry.ParentProcessId));
        entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
      } while (Process32Next(snapshot, ref entry));
      return result;
    }
    finally
    {
      CloseHandle(snapshot);
    }
  }

  private static void ThrowIfFailed(int result)
  {
    if (result < 0) Marshal.ThrowExceptionForHR(result);
  }

  [DllImport("combase.dll", PreserveSig = false)]
  private static extern void RoGetActivationFactory(
      [MarshalAs(UnmanagedType.HString)] string activatableClassId,
      [In] ref Guid iid,
      [Out, MarshalAs(UnmanagedType.IInspectable)] out object factory);

  [DllImport("combase.dll", PreserveSig = false)]
  private static extern void WindowsCreateString(
      [MarshalAs(UnmanagedType.LPWStr)] string source,
      uint length,
      out IntPtr hstring);

  [DllImport("combase.dll")]
  private static extern int WindowsDeleteString(IntPtr hstring);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern IntPtr CreateToolhelp32Snapshot(SnapshotFlags flags, uint processId);

  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

  [DllImport("kernel32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CloseHandle(IntPtr handle);

  private sealed record ProcessLink(int ProcessId, int ParentId);

  [Flags]
  private enum SnapshotFlags : uint
  {
    Process = 0x00000002,
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct ProcessEntry32
  {
    public uint Size;
    public uint Usage;
    public uint ProcessId;
    public IntPtr DefaultHeapId;
    public uint ModuleId;
    public uint Threads;
    public uint ParentProcessId;
    public int BasePriority;
    public uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
  }

  private enum AudioDataFlow
  {
    Render = 0,
    Capture = 1,
    All = 2,
  }

  [Flags]
  private enum AudioRole
  {
    Console = 0,
    Multimedia = 1,
    Communications = 2,
  }

  [Guid("ab3d4648-e242-459f-b02f-541c70306324")]
  [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
  private interface IAudioPolicyConfigFactory21H2
  {
    int AddContextVolumeChanged();
    int RemoveContextVolumeChanged();
    int AddRingerVibrateStateChanged();
    int RemoveRingerVibrateStateChanged();
    int SetVolumeGroupGainForId();
    int GetVolumeGroupGainForId();
    int GetActiveVolumeGroupForEndpointId();
    int GetVolumeGroupsForEndpoint();
    int GetCurrentVolumeContext();
    int SetVolumeGroupMuteForId();
    int GetVolumeGroupMuteForId();
    int SetRingerVibrateState();
    int GetRingerVibrateState();
    int SetPreferredChatApplication();
    int ResetPreferredChatApplication();
    int GetPreferredChatApplication();
    int GetCurrentChatApplications();
    int AddChatContextChanged();
    int RemoveChatContextChanged();
    [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, AudioDataFlow flow, AudioRole role, IntPtr deviceId);
  }

  [Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258")]
  [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
  private interface IAudioPolicyConfigFactoryDownlevel
  {
    int AddContextVolumeChanged();
    int RemoveContextVolumeChanged();
    int AddRingerVibrateStateChanged();
    int RemoveRingerVibrateStateChanged();
    int SetVolumeGroupGainForId();
    int GetVolumeGroupGainForId();
    int GetActiveVolumeGroupForEndpointId();
    int GetVolumeGroupsForEndpoint();
    int GetCurrentVolumeContext();
    int SetVolumeGroupMuteForId();
    int GetVolumeGroupMuteForId();
    int SetRingerVibrateState();
    int GetRingerVibrateState();
    int SetPreferredChatApplication();
    int ResetPreferredChatApplication();
    int GetPreferredChatApplication();
    int GetCurrentChatApplications();
    int AddChatContextChanged();
    int RemoveChatContextChanged();
    [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, AudioDataFlow flow, AudioRole role, IntPtr deviceId);
  }
}
