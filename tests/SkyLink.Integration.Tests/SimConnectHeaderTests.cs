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
        var m = Regex.Match(header, @"enum\s+" + name + @"\s*\{(?<body>.*?)\}", RegexOptions.Singleline);
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
            string cName = "SIMCONNECT_RECV_ID_" + Regex.Replace(v.ToString(), "(?<!^)([A-Z])", "_$1").ToUpperInvariant().Replace("SIM_OBJECT", "SIMOBJECT");
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
        output.WriteLine("SimConnect constants verified against the installed SDK header.");
    }
}
