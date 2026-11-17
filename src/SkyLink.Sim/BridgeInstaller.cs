using System.Text.Json;
using System.Text.RegularExpressions;

namespace SkyLink.Sim;

public sealed record PackageInfo(string Folder, string Title, string Creator, string Version);

/// <summary>
/// Finds the MSFS 2024 Community folder (from UserCfg.opt, Steam or Microsoft Store) and installs
/// the skylink-bridge package into it. Never touches a folder it did not create.
/// </summary>
public static class BridgeInstaller
{
    public const string PackageFolderName = "skylink-bridge";
    public const string ModuleFile = "modules/SkyLinkBridge.wasm";
    private const string OurCreator = "SkyLink";

    /// <summary>UserCfg.opt locations: Steam, then Microsoft Store.</summary>
    public static IEnumerable<string> UserCfgCandidates()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(appData, "Microsoft Flight Simulator 2024", "UserCfg.opt");
        yield return Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt");
    }

    /// <summary>The <c>InstalledPackagesPath "..."</c> line of UserCfg.opt.</summary>
    public static string? ParseInstalledPackagesPath(string userCfg)
    {
        var m = Regex.Match(userCfg, "^\\s*InstalledPackagesPath\\s+\"(?<p>[^\"]+)\"", RegexOptions.Multiline);
        return m.Success ? m.Groups["p"].Value.Trim() : null;
    }

    public static string? FindCommunityFolder(out string? userCfgUsed)
    {
        foreach (var cfg in UserCfgCandidates())
        {
            if (!File.Exists(cfg)) continue;
            if (ParseInstalledPackagesPath(File.ReadAllText(cfg)) is not { } packages) continue;
            userCfgUsed = cfg;
            return Path.Combine(packages, "Community");
        }
        userCfgUsed = null;
        return null;
    }

    public static PackageInfo? ReadPackage(string folder)
    {
        string manifest = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            string S(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            return new PackageInfo(folder, S("title"), S("creator"), S("package_version"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Checks that a built package (manifest, layout, module) is ready to install.</summary>
    public static string? ValidateSource(string source)
    {
        if (ReadPackage(source) is not { } info) return $"{source} has no valid manifest.json";
        if (info.Creator != OurCreator) return $"{source} is not a SkyLink package (creator '{info.Creator}')";
        if (!File.Exists(Path.Combine(source, ModuleFile)))
            return $"the bridge module is not built yet ({ModuleFile} missing): install the MSFS 2024 SDK and run wasm\\build.ps1";
        if (!File.Exists(Path.Combine(source, "layout.json"))) return $"{source} has no layout.json (run wasm\\build.ps1)";
        return null;
    }

    /// <summary>Installs (or updates) the package. Returns a message describing what happened.</summary>
    public static string Install(string source, string community, bool dryRun = false)
    {
        if (ValidateSource(source) is { } problem) throw new InvalidOperationException(problem);
        if (!Directory.Exists(community)) throw new DirectoryNotFoundException($"Community folder not found: {community}");

        string target = Path.Combine(community, PackageFolderName);
        var existing = ReadPackage(target);
        if (Directory.Exists(target) && (existing is null || existing.Creator != OurCreator))
            throw new InvalidOperationException($"{target} exists but is not a SkyLink package; not touching it");

        var newInfo = ReadPackage(source)!;
        string action = existing is null ? "installed" : $"updated {existing.Version} ->";
        if (dryRun) return $"would have {action} {newInfo.Version} at {target}";

        // Copy to a temporary sibling first, then swap, so a failed copy never leaves half a package.
        string staging = target + ".skylink-new";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        CopyTree(source, staging);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.Move(staging, target);
        return $"{action} {newInfo.Version} at {target}. Restart MSFS to load it.";
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file)); // layout.json dates must still match
        }
    }
}
