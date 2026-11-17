using System.Text.RegularExpressions;
using SkyLink.Sim;
using Xunit.Abstractions;

namespace SkyLink.Integration.Tests;

/// <summary>
/// SkyLink calls the native SimConnect.dll directly, so its constants must match the SDK
/// header exactly. When the MSFS 2024 SDK is installed (MSFS2024_SDK set), this parses
/// SimConnect.h and compares. Without the SDK it reports that it could not check.
/// </summary>
public sealed class SimConnectHeaderTests(ITestOutputHelper output)
{
    private static string? Header()
    {
        foreach (var env in new[] { "MSFS2024_SDK", "MSFS_SDK" })
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } sdk)
            {
                string h = Path.Combine(sdk, "SimConnect SDK", "include", "SimConnect.h");
                if (File.Exists(h)) return File.ReadAllText(h);
            }
        const string fallback = @"C:\MSFS 2024 SDK\SimConnect SDK\include\SimConnect.h";
        return File.Exists(fallback) ? File.ReadAllText(fallback) : null;
    }

    /// <summary>Values of a C enum: sequential unless assigned explicitly.</summary>
    private static Dictionary<string, long> Enum(string header, string name)
    {
        // The SDK header declares enums through a macro, sometimes with an underlying type:
        // SIMCONNECT_ENUM NAME { ... };  or  SIMCONNECT_ENUM NAME:int { ... };
        var m = Regex.Match(header, @"(?:\benum|\bSIMCONNECT_ENUM(?:_FLAGS)?)\s+" + name + @"\s*(?::\s*\w+\s*)?\{(?<body>.*?)\}", RegexOptions.Singleline);
        Assert.True(m.Success, $"enum {name} not found in SimConnect.h");
        var result = new Dictionary<string, long>();
        long next = 0;
        foreach (var raw in m.Groups["body"].Value.Split(','))
        {
            string line = Regex.Replace(raw, @"//.*", "").Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('=', 2);
            string id = parts[0].Trim();
            if (parts.Length == 2)
            {
                string val = parts[1].Trim();
                next = val.StartsWith("0x") ? Convert.ToInt64(val[2..], 16) : long.TryParse(val, out long n) ? n : result.GetValueOrDefault(val);
            }
            result[id] = next++;
        }
        return result;
    }

    [Fact]
    public void Constants_match_the_installed_sdk_header()
    {
        string? h = Header();
        if (h is null)
        {
            output.WriteLine("MSFS 2024 SDK not found (set MSFS2024_SDK): SimConnect constants NOT verified against the header.");
            return;
        }
        var recv = Enum(h, "SIMCONNECT_RECV_ID");
        foreach (var v in System.Enum.GetValues<SimConnectNative.RecvId>())
        {
            string cName = "SIMCONNECT_RECV_ID_" + CName(v.ToString());
            Assert.True(recv.ContainsKey(cName), $"{cName} missing from header");
            Assert.Equal((long)v, recv[cName]);
        }
        var period = Enum(h, "SIMCONNECT_PERIOD");
        Assert.Equal((long)SimConnectNative.Period.SimFrame, period["SIMCONNECT_PERIOD_SIM_FRAME"]);
        Assert.Equal((long)SimConnectNative.Period.Once, period["SIMCONNECT_PERIOD_ONCE"]);
        var cdPeriod = Enum(h, "SIMCONNECT_CLIENT_DATA_PERIOD");
        Assert.Equal((long)SimConnectNative.ClientDataPeriod.OnSet, cdPeriod["SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET"]);
        var types = Enum(h, "SIMCONNECT_DATATYPE");
        Assert.Equal((long)SimConnectNative.DataType.Float64, types["SIMCONNECT_DATATYPE_FLOAT64"]);
        Assert.Equal((long)SimConnectNative.DataType.String256, types["SIMCONNECT_DATATYPE_STRING256"]);
        Assert.Equal((long)SimConnectNative.DataType.String64, types["SIMCONNECT_DATATYPE_STRING64"]);
        Assert.Contains("#pragma pack(push, 1)", h.Replace("  ", " "));

        var objTypes = Enum(h, "SIMCONNECT_SIMOBJECT_TYPE");
        foreach (var v in System.Enum.GetValues<SimConnectNative.SimObjectType>())
            Assert.Equal(objTypes["SIMCONNECT_SIMOBJECT_TYPE_" + CName(v.ToString())], (long)v);
        var state = Enum(h, "SIMCONNECT_STATE");
        Assert.Equal((long)SimConnectNative.State.On, state["SIMCONNECT_STATE_ON"]);
        Assert.Equal((long)SimConnectNative.State.Off, state["SIMCONNECT_STATE_OFF"]);
        var ex = Enum(h, "SIMCONNECT_EXCEPTION");
        Assert.Equal((long)SimConnectNative.ExceptionCreateObjectFailed, ex["SIMCONNECT_EXCEPTION_CREATE_OBJECT_FAILED"]);
        Assert.Equal((long)SimConnectNative.ExceptionUnrecognizedId, ex["SIMCONNECT_EXCEPTION_UNRECOGNIZED_ID"]);
        output.WriteLine("SimConnect constants verified against the installed SDK header.");
    }

    private static string CName(string pascal) =>
        Regex.Replace(pascal, "(?<!^)([A-Z])", "_$1").ToUpperInvariant()
            .Replace("SIM_OBJECT", "SIMOBJECT").Replace("ADD_REMOVE", "ADDREMOVE");

    /// <summary>Every function SkyLink imports exists in the header with the same number of parameters.</summary>
    [Fact]
    public void Every_imported_function_matches_its_header_declaration()
    {
        string? h = Header();
        if (h is null)
        {
            output.WriteLine("MSFS 2024 SDK not found: imports NOT verified.");
            return;
        }
        var imports = typeof(SimConnectNative).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), false).Length > 0)
            .ToList();
        Assert.NotEmpty(imports);
        foreach (var m in imports)
        {
            var decl = Regex.Match(h, @"SIMCONNECTAPI\s+" + m.Name + @"\s*\((?<p>[^;]*)\)\s*;");
            Assert.True(decl.Success, $"{m.Name} is not declared in SimConnect.h");
            int count = decl.Groups["p"].Value.Split(',').Length;
            Assert.True(count == m.GetParameters().Length, $"{m.Name}: header has {count} parameters, SkyLink imports {m.GetParameters().Length}");
        }
        // The create call takes the init position BY VALUE, as the struct SkyLink marshals.
        Assert.Matches(@"SimConnect_AICreateNonATCAircraft_EX1\([^;]*SIMCONNECT_DATA_INITPOSITION\s+InitPos", h);
        Assert.Equal(typeof(SimConnectNative.InitPosition),
            imports.Single(m => m.Name == "SimConnect_AICreateNonATCAircraft_EX1").GetParameters()[4].ParameterType);
        output.WriteLine($"{imports.Count} imported functions match their declarations in SimConnect.h.");
    }

    /// <summary>Field offsets SkyLink reads from received messages, and the init-position struct layout.</summary>
    [Fact]
    public void Struct_layouts_match_the_header()
    {
        string? h = Header();
        if (h is null)
        {
            output.WriteLine("MSFS 2024 SDK not found: layouts NOT verified.");
            return;
        }
        var exc = Offsets(h, "SIMCONNECT_RECV_EXCEPTION");
        Assert.Equal(RecvLayout.ExceptionCode, exc["dwException"]);
        Assert.Equal(RecvLayout.ExceptionSendId, exc["dwSendID"]);
        Assert.Equal(RecvLayout.ExceptionIndex, exc["dwIndex"]);

        var addRemove = Offsets(h, "SIMCONNECT_RECV_EVENT_OBJECT_ADDREMOVE");
        Assert.Equal(RecvLayout.EventId, addRemove["uEventID"]);
        Assert.Equal(RecvLayout.EventData, addRemove["dwData"]);
        Assert.Equal(RecvLayout.AddRemoveObjectType, addRemove["eObjType"]);

        var byType = Offsets(h, "SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE");
        Assert.Equal(RecvLayout.DataRequestId, byType["dwRequestID"]);
        Assert.Equal(RecvLayout.DataObjectId, byType["dwObjectID"]);
        Assert.Equal(RecvLayout.DataDefineId, byType["dwDefineID"]);
        Assert.Equal(RecvLayout.DataEntryNumber, byType["dwentrynumber"]);
        Assert.Equal(RecvLayout.DataOutOf, byType["dwoutof"]);
        Assert.Equal(RecvLayout.DataDefineCount, byType["dwDefineCount"]);
        Assert.Equal(RecvLayout.Data, byType["dwData"]);

        var assigned = Offsets(h, "SIMCONNECT_RECV_ASSIGNED_OBJECT_ID");
        Assert.Equal(RecvLayout.AssignedRequestId, assigned["dwRequestID"]);
        Assert.Equal(RecvLayout.AssignedObjectId, assigned["dwObjectID"]);

        var init = Offsets(h, "SIMCONNECT_DATA_INITPOSITION");
        var t = typeof(SimConnectNative.InitPosition);
        foreach (var f in t.GetFields())
            Assert.Equal(init[f.Name], (int)System.Runtime.InteropServices.Marshal.OffsetOf(t, f.Name));
        Assert.Equal(init["<size>"], System.Runtime.InteropServices.Marshal.SizeOf(t));
        output.WriteLine($"received-message offsets and SIMCONNECT_DATA_INITPOSITION ({init["<size>"]} bytes) match the header.");
    }

    /// <summary>Field offsets of a (1-byte packed) header struct, following its base structs.</summary>
    private static Dictionary<string, int> Offsets(string header, string name)
    {
        var m = Regex.Match(header, @"SIMCONNECT_(?:REF)?STRUCT\s+" + name + @"\s*(?::\s*public\s+(?<base>\w+))?\s*(?://[^\n]*)?\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
        Assert.True(m.Success, $"struct {name} not found in SimConnect.h");
        var result = m.Groups["base"].Success ? Offsets(header, m.Groups["base"].Value) : new Dictionary<string, int> { ["<size>"] = 0 };
        int offset = result["<size>"];
        foreach (var raw in m.Groups["body"].Value.Split(';'))
        {
            string line = Regex.Replace(raw, @"//[^\n]*", "").Trim();
            if (line.Length == 0 || line.StartsWith("static")) continue;
            var dv = Regex.Match(line, @"^SIMCONNECT_DATAV\(\s*(\w+)");
            if (dv.Success)
            {
                result[dv.Groups[1].Value] = offset; // variable data starts here
                continue;
            }
            var str = Regex.Match(line, @"^SIMCONNECT_STRING\(\s*(\w+)\s*,\s*(\w+)\s*\)");
            if (str.Success)
            {
                result[str.Groups[1].Value] = offset;
                offset += str.Groups[2].Value == "MAX_PATH" ? 260 : int.Parse(str.Groups[2].Value);
                continue;
            }
            var field = Regex.Match(line, @"^(\w+)\s+(\w+)$");
            Assert.True(field.Success, $"{name}: cannot parse field '{line}'");
            int size = field.Groups[1].Value switch
            {
                "double" => 8,
                "DWORD" or "float" or "SIMCONNECT_SIMOBJECT_TYPE" or "SIMCONNECT_OBJECT_ID" => 4,
                var other => throw new InvalidOperationException($"{name}: unknown field type {other}"),
            };
            result[field.Groups[2].Value] = offset;
            offset += size;
        }
        result["<size>"] = offset;
        return result;
    }
}
