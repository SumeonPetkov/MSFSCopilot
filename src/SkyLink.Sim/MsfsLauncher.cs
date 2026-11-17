using System.Diagnostics;

namespace SkyLink.Sim;

/// <summary>Starts Microsoft Flight Simulator 2024 the way it is installed (Steam or Microsoft Store).</summary>
public static class MsfsLauncher
{
    public const string ProcessName = "FlightSimulator2024";
    public const string SteamAppId = "2537590";
    public const string StoreAppId = @"shell:AppsFolder\Microsoft.Limitless_8wekyb3d8bbwe!App";

    public static bool IsRunning() => Process.GetProcessesByName(ProcessName).Length > 0;

    /// <summary>"Steam", "Microsoft Store" or null, from where MSFS keeps its UserCfg.opt.</summary>
    public static string? Edition()
    {
        var candidates = BridgeInstaller.UserCfgCandidates().ToList();
        if (File.Exists(candidates[0])) return "Steam";
        if (File.Exists(candidates[1])) return "Microsoft Store";
        return null;
    }

    /// <summary>Launch MSFS 2024 if it is not running. Returns what was done.</summary>
    public static string Launch()
    {
        if (IsRunning()) return "MSFS is already running";
        string target = Edition() == "Microsoft Store" ? StoreAppId : $"steam://rungameid/{SteamAppId}";
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return $"starting MSFS 2024 ({Edition() ?? "Steam"})";
    }
}
