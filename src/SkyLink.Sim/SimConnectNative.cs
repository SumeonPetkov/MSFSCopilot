using System.Runtime.InteropServices;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SkyLink.Integration.Tests")]

namespace SkyLink.Sim;

/// <summary>
/// Direct P/Invoke bindings for the native SimConnect.dll (MSFS 2024 SDK). Only what SkyLink
/// needs. All structures in SimConnect.h are packed to 1 byte, so received messages are read
/// with explicit offsets (see <see cref="Recv"/>), bounds-checked against dwSize.
/// The constants below are cross-checked against the SDK header by
/// <c>SimConnectHeaderTests</c> when <c>MSFS2024_SDK</c> is set.
/// </summary>
internal static class SimConnectNative
{
    public const string Dll = "SimConnect";

    public const uint ObjectIdUser = 0;
    public const uint Unused = 0xFFFFFFFF;
    public const uint GroupPriorityHighest = 1;
    public const uint EventFlagGroupIdIsPriority = 0x10;

    public enum RecvId : uint
    {
        Null = 0,
        Exception = 1,
        Open = 2,
        Quit = 3,
        Event = 4,
        EventFilename = 6,
        SimObjectData = 8,
        SystemState = 15,
        ClientData = 16,
        EnumerateInputEvents = 34,
        GetInputEvent = 35,
        SubscribeInputEvent = 36,
    }

    public enum Period : uint { Never = 0, Once = 1, VisualFrame = 2, SimFrame = 3, Second = 4 }

    [Flags]
    public enum DataRequestFlag : uint { Default = 0, Changed = 1, Tagged = 2 }

    public enum ClientDataPeriod : uint { Never = 0, Once = 1, VisualFrame = 2, OnSet = 3, Second = 4 }

    public enum DataType : uint { Invalid = 0, Int32 = 1, Int64 = 2, Float32 = 3, Float64 = 4, String8 = 5, String32 = 6, String64 = 7, String128 = 8, String256 = 9, String260 = 10 }

    public const int SOk = 0;

    private static bool _resolverInstalled;

    /// <summary>Find SimConnect.dll: SKYLINK_SIMCONNECT_DLL, next to SkyLink, or the MSFS 2024 SDK.</summary>
    public static string? InstallResolver()
    {
        string? found = Candidates().FirstOrDefault(File.Exists);
        if (_resolverInstalled) return found;
        _resolverInstalled = true;
        NativeLibrary.SetDllImportResolver(typeof(SimConnectNative).Assembly, (name, asm, path) =>
        {
            if (name != Dll) return IntPtr.Zero;
            foreach (var c in Candidates())
                if (File.Exists(c) && NativeLibrary.TryLoad(c, out var h)) return h;
            return IntPtr.Zero;
        });
        return found;
    }

    public static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable("SKYLINK_SIMCONNECT_DLL") is { Length: > 0 } explicitPath) yield return explicitPath;
        yield return Path.Combine(AppContext.BaseDirectory, "SimConnect.dll");
        foreach (var env in new[] { "MSFS2024_SDK", "MSFS_SDK" })
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } sdk)
                yield return Path.Combine(sdk, "SimConnect SDK", "lib", "SimConnect.dll");
        yield return @"C:\MSFS 2024 SDK\SimConnect SDK\lib\SimConnect.dll";
        yield return @"C:\MSFS SDK\SimConnect SDK\lib\SimConnect.dll";
    }

    [DllImport(Dll, CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern int SimConnect_Open(out IntPtr handle, string name, IntPtr hwnd, uint userEventWin32, IntPtr eventHandle, uint configIndex);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_Close(IntPtr handle);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_GetNextDispatch(IntPtr handle, out IntPtr data, out uint size);

    [DllImport(Dll, CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern int SimConnect_AddToDataDefinition(IntPtr handle, uint defineId, string datumName, string? unitsName, DataType datumType, float epsilon, uint datumId);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_ClearDataDefinition(IntPtr handle, uint defineId);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_RequestDataOnSimObject(IntPtr handle, uint requestId, uint defineId, uint objectId, Period period, DataRequestFlag flags, uint origin, uint interval, uint limit);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern unsafe int SimConnect_SetDataOnSimObject(IntPtr handle, uint defineId, uint objectId, uint flags, uint arrayCount, uint unitSize, void* data);

    [DllImport(Dll, CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern int SimConnect_MapClientEventToSimEvent(IntPtr handle, uint eventId, string eventName);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_TransmitClientEvent_EX1(IntPtr handle, uint objectId, uint eventId, uint groupId, uint flags, uint d0, uint d1, uint d2, uint d3, uint d4);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_AddClientEventToNotificationGroup(IntPtr handle, uint groupId, uint eventId, int maskable);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_SetNotificationGroupPriority(IntPtr handle, uint groupId, uint priority);

    [DllImport(Dll, CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern int SimConnect_SubscribeToSystemEvent(IntPtr handle, uint eventId, string systemEventName);

    [DllImport(Dll, CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern int SimConnect_RequestSystemState(IntPtr handle, uint requestId, string state);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_GetLastSentPacketID(IntPtr handle, out uint sendId);

    [DllImport(Dll, CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern int SimConnect_MapClientDataNameToID(IntPtr handle, string clientDataName, uint clientDataId);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_CreateClientData(IntPtr handle, uint clientDataId, uint size, uint flags);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_AddToClientDataDefinition(IntPtr handle, uint defineId, uint offset, uint sizeOrType, float epsilon, uint datumId);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_RequestClientData(IntPtr handle, uint clientDataId, uint requestId, uint defineId, ClientDataPeriod period, uint flags, uint origin, uint interval, uint limit);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern unsafe int SimConnect_SetClientData(IntPtr handle, uint clientDataId, uint defineId, uint flags, uint reserved, uint unitSize, void* data);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_EnumerateInputEvents(IntPtr handle, uint requestId);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_GetInputEvent(IntPtr handle, uint requestId, ulong hash);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern unsafe int SimConnect_SetInputEvent(IntPtr handle, ulong hash, uint unitSize, void* value);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int SimConnect_SubscribeInputEvent(IntPtr handle, ulong hash);
}

/// <summary>Bounds-checked reader over one received SimConnect message (1-byte packed).</summary>
internal readonly ref struct Recv
{
    public const int HeaderSize = 12;

    private readonly ReadOnlySpan<byte> _data;

    public unsafe Recv(IntPtr ptr, uint size)
    {
        _data = new ReadOnlySpan<byte>((void*)ptr, checked((int)size));
        if (_data.Length < HeaderSize) throw new InvalidDataException("SimConnect message shorter than its header.");
        uint declared = U32(0);
        if (declared > size) throw new InvalidDataException("SimConnect message size field exceeds the buffer.");
        _data = _data[..(int)declared];
    }

    public int Length => _data.Length;
    public SimConnectNative.RecvId Id => (SimConnectNative.RecvId)U32(8);

    public uint U32(int offset) => MemoryMarshal.Read<uint>(Slice(offset, 4));
    public ulong U64(int offset) => MemoryMarshal.Read<ulong>(Slice(offset, 8));
    public ReadOnlySpan<byte> Bytes(int offset, int length) => Slice(offset, length);
    public double F64(int offset) => MemoryMarshal.Read<double>(Slice(offset, 8));

    public string AnsiString(int offset, int maxLength)
    {
        var s = Slice(offset, Math.Min(maxLength, _data.Length - offset));
        int nul = s.IndexOf((byte)0);
        return System.Text.Encoding.Latin1.GetString(nul >= 0 ? s[..nul] : s);
    }

    private ReadOnlySpan<byte> Slice(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > _data.Length)
            throw new InvalidDataException($"SimConnect message ({Id}, {_data.Length} bytes) too short for field at {offset}+{length}.");
        return _data.Slice(offset, length);
    }
}

