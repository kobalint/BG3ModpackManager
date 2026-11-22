using BG3ModpackManager.Core.Models;
using BG3ModpackManager.Core.Services;

namespace BG3ModpackManager.Tests;

/// <summary>
/// Isolated temporary directory fixture for safe sandbox testing without ever touching real BG3 files.
/// </summary>
internal sealed class TempSandbox : IDisposable
{
    public string Root { get; }
    public AppSettings Settings { get; }
    public JsonStore Store { get; }
    public LibraryDatabase Db { get; }

    public TempSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "BG3MPM_Sandbox_" + Guid.NewGuid().ToString("N")[..8]);
        string mods = Path.Combine(Root, "Mods");
        string game = Path.Combine(Root, "Game");
        string data = Path.Combine(game, "Data");
        string orders = Path.Combine(Root, "Orders");
        string vault = Path.Combine(Root, "Vault");
        string storeDir = Path.Combine(Root, "AppData");

        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(orders);
        Directory.CreateDirectory(vault);

        Settings = new AppSettings
        {
            ModsFolder = mods,
            GameFolder = game,
            DataFolder = data,
            OrdersFolder = orders,
            VaultFolder = vault,
            ModSettingsLsxPath = Path.Combine(Root, "PlayerProfiles", "Public", "modsettings.lsx"),
            SwitchMode = SwitchMode.FullProfile
        };

        Store = new JsonStore(storeDir);
        Db = new LibraryDatabase();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // Ignore transient cleanup locks.
        }
    }
}
