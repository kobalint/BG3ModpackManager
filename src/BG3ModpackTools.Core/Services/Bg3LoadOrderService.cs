using System.Text.Json;
using System.Xml.Linq;
using BG3ModpackTools.Core.Models;

namespace BG3ModpackTools.Core.Services;

/// <summary>
/// Active module short descriptor read from BG3's modsettings.lsx.
/// </summary>
public sealed record ModSettingsEntry(string Uuid, string Name, string Folder, string? Version64);

/// <summary>
/// Reads and writes BG3ModManager load order JSON files and Baldur's Gate 3 modsettings.lsx files.
/// </summary>
public static class Bg3LoadOrderService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> BaseModuleUuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "cb555efe-2d9e-131f-8195-a89329d218ea", // GustavX
        "28ac9ce2-2aba-8cda-b3b5-6e922f71b6b8", // GustavDev
        "991c9c7a-fb80-40cb-8f0d-b92d4e80e9b1", // Gustav
        "b77b6210-ac50-4cb1-a3d5-5705fb9c3d4c", // Honour
        "ed539163-bb70-431b-96a7-f5b2eda5376b", // Shared
        "3d0c5ff8-c95d-c907-ff3e-34b204f1c630"  // SharedDev
    };

    private static readonly HashSet<string> BaseModuleFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gustav",
        "GustavDev",
        "GustavX",
        "Honour",
        "Shared",
        "SharedDev"
    };

    public static Bg3LoadOrderFile LoadOrderJson(string jsonPath)
    {
        string text = File.ReadAllText(jsonPath);
        Bg3LoadOrderFile? parsed = JsonSerializer.Deserialize<Bg3LoadOrderFile>(text, JsonOptions);
        return parsed ?? new Bg3LoadOrderFile();
    }

    public static void SaveOrderJson(string jsonPath, IEnumerable<Bg3OrderEntry> entries)
    {
        string? dir = Path.GetDirectoryName(jsonPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var file = new Bg3LoadOrderFile { Order = entries.ToList() };
        string tmp = jsonPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(tmp, jsonPath, overwrite: true);
    }

    public static List<ModSettingsEntry> ReadModSettingsLsx(string lsxPath)
    {
        var result = new List<ModSettingsEntry>();
        if (string.IsNullOrWhiteSpace(lsxPath) || !File.Exists(lsxPath))
        {
            return result;
        }

        XDocument doc = XDocument.Load(lsxPath);
        XElement? modsNode = doc.Descendants("node")
            .FirstOrDefault(n => string.Equals((string?)n.Attribute("id"), "Mods", StringComparison.OrdinalIgnoreCase));

        XElement? children = modsNode?.Element("children");
        if (children is null)
        {
            return result;
        }

        foreach (XElement moduleNode in children.Elements("node"))
        {
            if (!string.Equals((string?)moduleNode.Attribute("id"), "ModuleShortDesc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string uuid = GetAttrValue(moduleNode, "UUID") ?? string.Empty;
            string name = GetAttrValue(moduleNode, "Name") ?? string.Empty;
            string folder = GetAttrValue(moduleNode, "Folder") ?? string.Empty;
            string? version64 = GetAttrValue(moduleNode, "Version64");

            if (IsBaseGameModule(uuid, folder))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(uuid))
            {
                result.Add(new ModSettingsEntry(uuid, name, folder, version64));
            }
        }

        return result;
    }

    /// <summary>
    /// Updates modsettings.lsx with the supplied ordered mods while creating a .bak backup first
    /// and preserving base-game entries (such as GustavX).
    /// </summary>
    public static void WriteModSettingsLsx(string lsxPath, IEnumerable<ModEntry> orderedMods)
    {
        GameProcessGuard.EnsureGameNotRunning("updating BG3 modsettings.lsx");

        if (string.IsNullOrWhiteSpace(lsxPath))
        {
            throw new ArgumentException("modsettings.lsx path is empty.", nameof(lsxPath));
        }

        string? dir = Path.GetDirectoryName(lsxPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        XDocument doc;
        if (File.Exists(lsxPath))
        {
            File.Copy(lsxPath, lsxPath + ".bak", overwrite: true);
            doc = XDocument.Load(lsxPath);
        }
        else
        {
            doc = CreateDefaultModSettingsDocument();
        }

        XElement? modsNode = doc.Descendants("node")
            .FirstOrDefault(n => string.Equals((string?)n.Attribute("id"), "Mods", StringComparison.OrdinalIgnoreCase));

        if (modsNode is null)
        {
            doc = CreateDefaultModSettingsDocument();
            modsNode = doc.Descendants("node")
                .First(n => string.Equals((string?)n.Attribute("id"), "Mods", StringComparison.OrdinalIgnoreCase));
        }

        XElement? children = modsNode.Element("children");
        if (children is null)
        {
            children = new XElement("children");
            modsNode.Add(children);
        }

        List<XElement> preservedBaseNodes = children.Elements("node")
            .Where(n =>
            {
                if (!string.Equals((string?)n.Attribute("id"), "ModuleShortDesc", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string uuid = GetAttrValue(n, "UUID") ?? string.Empty;
                string folder = GetAttrValue(n, "Folder") ?? string.Empty;
                return IsBaseGameModule(uuid, folder);
            })
            .Select(n => new XElement(n))
            .ToList();

        if (preservedBaseNodes.Count == 0)
        {
            preservedBaseNodes.Add(BuildModuleShortDescElement(
                folder: "GustavX",
                md5: "7bda6398eec6be9d83fdc1e9d7f49ca1",
                name: "GustavX",
                publishHandle: "0",
                uuid: "cb555efe-2d9e-131f-8195-a89329d218ea",
                version64: "145241946983020728"));
        }

        children.RemoveAll();
        foreach (XElement baseNode in preservedBaseNodes)
        {
            children.Add(baseNode);
        }

        var writtenUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModEntry mod in orderedMods)
        {
            if (string.IsNullOrWhiteSpace(mod.Uuid) || !mod.HasMetaLsx || mod.EffectiveOverrideKind == OverrideKind.StandaloneOverride)
            {
                continue;
            }

            if (IsBaseGameModule(mod.Uuid, mod.FolderName ?? string.Empty))
            {
                continue;
            }

            if (!writtenUuids.Add(mod.Uuid))
            {
                continue;
            }

            children.Add(BuildModuleShortDescElement(
                folder: mod.FolderName ?? mod.DisplayName,
                md5: mod.Md5 ?? string.Empty,
                name: mod.DisplayName,
                publishHandle: string.IsNullOrWhiteSpace(mod.PublishHandle) ? "0" : mod.PublishHandle,
                uuid: mod.Uuid,
                version64: string.IsNullOrWhiteSpace(mod.Version64) ? "36028797018963968" : mod.Version64));
        }

        string tmp = lsxPath + ".tmp";
        doc.Save(tmp);
        File.Move(tmp, lsxPath, overwrite: true);
    }

    private static bool IsBaseGameModule(string uuid, string folder)
    {
        return BaseModuleUuids.Contains(uuid) || BaseModuleFolders.Contains(folder);
    }

    private static string? GetAttrValue(XElement moduleNode, string attributeId)
    {
        return moduleNode.Elements("attribute")
            .FirstOrDefault(a => string.Equals((string?)a.Attribute("id"), attributeId, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("value")?.Value;
    }

    private static XDocument CreateDefaultModSettingsDocument()
    {
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("save",
                new XElement("version",
                    new XAttribute("major", "4"),
                    new XAttribute("minor", "8"),
                    new XAttribute("revision", "0"),
                    new XAttribute("build", "100")),
                new XElement("region",
                    new XAttribute("id", "ModuleSettings"),
                    new XElement("node",
                        new XAttribute("id", "root"),
                        new XElement("children",
                            new XElement("node",
                                new XAttribute("id", "Mods"),
                                new XElement("children")))))));
    }

    private static XElement BuildModuleShortDescElement(
        string folder,
        string md5,
        string name,
        string publishHandle,
        string uuid,
        string version64)
    {
        return new XElement("node",
            new XAttribute("id", "ModuleShortDesc"),
            new XElement("attribute", new XAttribute("id", "Folder"), new XAttribute("type", "LSString"), new XAttribute("value", folder)),
            new XElement("attribute", new XAttribute("id", "MD5"), new XAttribute("type", "LSString"), new XAttribute("value", md5)),
            new XElement("attribute", new XAttribute("id", "Name"), new XAttribute("type", "LSString"), new XAttribute("value", name)),
            new XElement("attribute", new XAttribute("id", "PublishHandle"), new XAttribute("type", "uint64"), new XAttribute("value", publishHandle)),
            new XElement("attribute", new XAttribute("id", "UUID"), new XAttribute("type", "guid"), new XAttribute("value", uuid)),
            new XElement("attribute", new XAttribute("id", "Version64"), new XAttribute("type", "int64"), new XAttribute("value", version64)));
    }
}
