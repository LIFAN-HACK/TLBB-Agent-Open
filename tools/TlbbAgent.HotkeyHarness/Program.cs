using System.Text;
using TlbbAgent.Contracts;
using TlbbAgent.Core;

namespace TlbbAgent.HotkeyHarness;

internal static class Program
{
    private const string RoleName = "测试角色";
    private const string PrivateAccountDirectory = "#private-account-do-not-expose";

    private static int Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = Path.Combine(Path.GetTempPath(), $"tlbb-hotkey-harness-{Guid.NewGuid():N}");
        try
        {
            var gamePath = CreateFixture(root, PrivateAccountDirectory, RoleName);
            var log = new RuntimeLogService();
            var service = new GameHotkeyProfileService(log);
            var window = Window(new nint(0x101), gamePath);

            var loaded = service.BindToRole(window, RoleName);
            Assert("exact role profile loads", loaded.Loaded);
            Assert("mapping fingerprint is SHA256", loaded.MappingFingerprint.Length == 64);
            Assert("current attack mapping uses key1 Q", Resolve(service, window, GameInputSemanticIds.SkillSlot(1)) == "Q");
            Assert("target selection uses current key1 F7", Resolve(service, window, GameInputSemanticIds.TargetSelect) == "F7");
            Assert("character panel uses current key1 V", Resolve(service, window, GameInputSemanticIds.CharacterPanel) == "V");
            Assert("inventory panel uses current key1 B", Resolve(service, window, GameInputSemanticIds.InventoryPanel) == "B");
            Assert("skill panel uses current key1 J", Resolve(service, window, GameInputSemanticIds.SkillPanel) == "J");
            Assert("quest panel uses current key1 L", Resolve(service, window, GameInputSemanticIds.QuestPanel) == "L");
            Assert("trade panel uses current key1 Y", Resolve(service, window, GameInputSemanticIds.TradePanel) == "Y");
            Assert("pet panel uses current key1 N", Resolve(service, window, GameInputSemanticIds.PetPanel) == "N");
            Assert("world map uses current key1 M", Resolve(service, window, GameInputSemanticIds.WorldMap) == "M");
            Assert("area map uses current key1 Tab", Resolve(service, window, GameInputSemanticIds.AreaMap) == "Tab");
            Assert("default key2 F1 is ignored", loaded.Bindings.All(binding => binding.Hotkey != "F1"));

            var modifierBinding = loaded.Bindings.Single(binding => binding.ActionId == 2);
            Assert("unverified modifier encoding fails closed",
                !modifierBinding.IsDispatchable && modifierBinding.Reason == "modifier_encoding_not_audited");
            var unknownBinding = loaded.Bindings.Single(binding => binding.ActionId == 99);
            Assert("unknown action id is never dispatched",
                !unknownBinding.IsDispatchable && unknownBinding.Reason == "unknown_action_id");

            var wrongRole = service.BindToRole(Window(new nint(0x102), gamePath), "相近但不同的角色");
            Assert("near role name does not match", wrongRole.Status == GameHotkeyProfileStatus.RoleNotFound);

            CreateRoleProfile(root, "#second-private-account", RoleName, FixtureIni());
            var ambiguous = service.BindToRole(Window(new nint(0x103), gamePath), RoleName);
            Assert("duplicate exact roles fail closed", ambiguous.Status == GameHotkeyProfileStatus.RoleAmbiguous);

            var publicEvidence = string.Join('|', new[]
            {
                loaded.ToString(),
                wrongRole.ToString(),
                ambiguous.ToString(),
                string.Join('|', log.Entries.Select(entry => entry.Message))
            });
            Assert("account path is not exposed", !publicEvidence.Contains(PrivateAccountDirectory, StringComparison.Ordinal));
            Assert("fixture root is not exposed", !publicEvidence.Contains(root, StringComparison.OrdinalIgnoreCase));

            Console.WriteLine("HotkeyHarness PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"HotkeyHarness FAIL: {ex.Message}");
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Test cleanup must not hide the actual assertion result.
            }
        }
    }

    private static string CreateFixture(string root, string accountDirectory, string roleName)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "Bin")).FullName;
        var gamePath = Path.Combine(bin, "Game.exe");
        File.WriteAllBytes(gamePath, Array.Empty<byte>());
        CreateRoleProfile(root, accountDirectory, roleName, FixtureIni());
        return gamePath;
    }

    private static void CreateRoleProfile(string root, string accountDirectory, string roleName, string ini)
    {
        var account = Directory.CreateDirectory(Path.Combine(root, "Accounts", accountDirectory)).FullName;
        var roleBytes = Encoding.GetEncoding(936).GetBytes(roleName);
        var roleFile = $"#{Convert.ToHexString(roleBytes)}.pfc";
        File.WriteAllBytes(Path.Combine(account, roleFile), Array.Empty<byte>());
        File.WriteAllText(Path.Combine(account, "Acce_Custom.ini"), ini, new UTF8Encoding(false));
    }

    private static string FixtureIni() => """
        [action1]
        id=1
        key1=81
        key2=112

        [action2]
        id=2
        key1=65623
        key2=113

        [target]
        id=25
        key1=118
        key2=9

        [character]
        id=26
        key1=86
        key2=67

        [inventory]
        id=27
        key1=66
        key2=65

        [skill]
        id=28
        key1=74
        key2=83

        [quest]
        id=29
        key1=76
        key2=81

        [trade]
        id=32
        key1=89
        key2=69

        [pet]
        id=33
        key1=78
        key2=88

        [world-map]
        id=41
        key1=77
        key2=77

        [area-map]
        id=47
        key1=9
        key2=66

        [unknown]
        id=99
        key1=65
        key2=65
        """;

    private static BoundGameWindow Window(nint hwnd, string gamePath) => new()
    {
        Hwnd = hwnd,
        Pid = 1000 + (int)hwnd,
        ProcessPath = gamePath,
        Bitness = ClientBitness.X64,
        ClientWidth = 1920,
        ClientHeight = 1080
    };

    private static string Resolve(
        IGameHotkeyProfileService service,
        BoundGameWindow window,
        string semanticId)
    {
        Assert($"semantic mapping exists: {semanticId}", service.TryResolve(window.Hwnd, semanticId, out var binding));
        return binding.Hotkey;
    }

    private static void Assert(string name, bool condition)
    {
        if (!condition)
            throw new InvalidOperationException(name);
        Console.WriteLine($"PASS: {name}");
    }
}
