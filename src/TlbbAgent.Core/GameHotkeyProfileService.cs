using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class GameHotkeyProfileService : IGameHotkeyProfileService
{
    private const string MappingFileName = "Acce_Custom.ini";
    private readonly ILogService _log;
    private readonly Func<BoundGameWindow, string?> _accountsRootResolver;
    private readonly ConcurrentDictionary<nint, GameHotkeyProfileLoadResult> _attached = new();

    static GameHotkeyProfileService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public GameHotkeyProfileService(ILogService log)
        : this(log, ResolveAccountsRoot)
    {
    }

    public GameHotkeyProfileService(
        ILogService log,
        Func<BoundGameWindow, string?> accountsRootResolver)
    {
        _log = log;
        _accountsRootResolver = accountsRootResolver;
    }

    public GameHotkeyProfileLoadResult BindToRole(BoundGameWindow window, string roleName)
    {
        ArgumentNullException.ThrowIfNull(window);
        roleName = NormalizeRoleName(roleName);
        if (window.Hwnd == nint.Zero || roleName.Length == 0)
            return Store(window.Hwnd, Failed(GameHotkeyProfileStatus.InvalidRole, roleName, "role_or_window_missing"));

        var accountsRoot = _accountsRootResolver(window);
        if (string.IsNullOrWhiteSpace(accountsRoot) || !Directory.Exists(accountsRoot))
            return Store(window.Hwnd, Failed(GameHotkeyProfileStatus.AccountsRootUnavailable, roleName, "accounts_root_unavailable"));

        var matches = FindExactRoleAccounts(accountsRoot, roleName).ToArray();
        if (matches.Length == 0)
            return Store(window.Hwnd, Failed(GameHotkeyProfileStatus.RoleNotFound, roleName, "exact_role_profile_not_found"));
        if (matches.Length > 1)
            return Store(window.Hwnd, Failed(GameHotkeyProfileStatus.RoleAmbiguous, roleName, "multiple_exact_role_profiles"));

        var mappingPath = Path.Combine(matches[0], MappingFileName);
        if (!File.Exists(mappingPath))
            return Store(window.Hwnd, Failed(GameHotkeyProfileStatus.MappingFileMissing, roleName, "current_mapping_file_missing"));

        try
        {
            var bindings = ParseCurrentMappings(mappingPath);
            if (bindings.Count == 0)
                return Store(window.Hwnd, Failed(GameHotkeyProfileStatus.MappingFileInvalid, roleName, "no_current_key_mappings"));

            var fingerprint = ComputeMappingFingerprint(bindings);
            var result = new GameHotkeyProfileLoadResult(
                GameHotkeyProfileStatus.Loaded,
                roleName,
                fingerprint,
                bindings,
                "exact_role_current_mapping_loaded");
            _attached[window.Hwnd] = result;
            _log.Info(
                "InputBinding",
                $"已加载当前角色键位：{bindings.Count(binding => binding.IsDispatchable)} 个可执行动作，映射 {fingerprint[..12]}。",
                window.Id);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            _log.Warn("InputBinding", $"当前角色键位读取失败：{ex.GetType().Name}。", window.Id);
            return Store(window.Hwnd, Failed(GameHotkeyProfileStatus.MappingFileInvalid, roleName, "current_mapping_parse_failed"));
        }
    }

    public bool TryResolve(nint hwnd, string semanticId, out GameHotkeyBinding binding)
    {
        semanticId = semanticId?.Trim() ?? string.Empty;
        if (semanticId.Length > 0
            && _attached.TryGetValue(hwnd, out var profile)
            && profile.Loaded)
        {
            var match = profile.Bindings.FirstOrDefault(item =>
                item.IsDispatchable
                && string.Equals(item.SemanticId, semanticId, StringComparison.Ordinal));
            if (match is not null)
            {
                binding = match;
                return true;
            }
        }

        binding = null!;
        return false;
    }

    public GameHotkeyProfileLoadResult? GetAttached(nint hwnd) =>
        _attached.TryGetValue(hwnd, out var result) ? result : null;

    public void Unbind(nint hwnd)
    {
        if (hwnd != nint.Zero)
            _attached.TryRemove(hwnd, out _);
    }

    private GameHotkeyProfileLoadResult Store(nint hwnd, GameHotkeyProfileLoadResult result)
    {
        if (hwnd != nint.Zero)
            _attached[hwnd] = result;
        return result;
    }

    private static GameHotkeyProfileLoadResult Failed(
        GameHotkeyProfileStatus status,
        string roleName,
        string reason) =>
        new(status, roleName, string.Empty, Array.Empty<GameHotkeyBinding>(), reason);

    private static IEnumerable<string> FindExactRoleAccounts(string accountsRoot, string roleName)
    {
        foreach (var accountDirectory in Directory.EnumerateDirectories(accountsRoot))
        {
            var exactRoleFound = false;
            foreach (var profilePath in Directory.EnumerateFiles(accountDirectory, "*.pfc", SearchOption.TopDirectoryOnly))
            {
                if (!TryDecodeHexFileStem(Path.GetFileNameWithoutExtension(profilePath), out var decodedRole))
                    continue;
                if (!string.Equals(NormalizeRoleName(decodedRole), roleName, StringComparison.Ordinal))
                    continue;

                exactRoleFound = true;
                break;
            }

            if (exactRoleFound)
                yield return accountDirectory;
        }
    }

    private static IReadOnlyList<GameHotkeyBinding> ParseCurrentMappings(string path)
    {
        var bindings = new List<GameHotkeyBinding>();
        int? id = null;
        long? currentKey = null;

        void Flush()
        {
            if (id is not > 0 || currentKey is null)
                return;

            var semanticId = ResolveSemanticId(id.Value);
            var knownSemantic = semanticId.Length > 0;
            var decoded = TryDecodeCurrentKey(currentKey.Value, out var hotkey, out var decodeReason);
            bindings.Add(new GameHotkeyBinding(
                id.Value,
                semanticId,
                hotkey,
                currentKey.Value,
                knownSemantic && decoded,
                !knownSemantic ? "unknown_action_id" : decodeReason));
        }

        foreach (var sourceLine in File.ReadLines(path))
        {
            var line = sourceLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                id = null;
                currentKey = null;
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("id", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
            {
                id = parsedId;
            }
            else if (key.Equals("key1", StringComparison.OrdinalIgnoreCase)
                     && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedKey))
            {
                currentKey = parsedKey;
            }

            // key2 is intentionally ignored. It is the client default, not the
            // player's current mapping, and must never become runtime input.
        }

        Flush();
        return bindings
            .GroupBy(binding => binding.ActionId)
            .Select(group => group.Last())
            .OrderBy(binding => binding.ActionId)
            .ToArray();
    }

    private static string ResolveSemanticId(int actionId) => actionId switch
    {
        >= 1 and <= 15 => GameInputSemanticIds.SkillSlot(actionId),
        25 => GameInputSemanticIds.TargetSelect,
        26 => GameInputSemanticIds.CharacterPanel,
        27 => GameInputSemanticIds.InventoryPanel,
        28 => GameInputSemanticIds.SkillPanel,
        29 => GameInputSemanticIds.QuestPanel,
        32 => GameInputSemanticIds.TradePanel,
        33 => GameInputSemanticIds.PetPanel,
        41 => GameInputSemanticIds.WorldMap,
        47 => GameInputSemanticIds.AreaMap,
        _ => string.Empty
    };

    private static bool TryDecodeCurrentKey(long encoded, out string hotkey, out string reason)
    {
        hotkey = string.Empty;
        if (encoded <= 0)
        {
            reason = "current_mapping_unassigned";
            return false;
        }

        var modifierBits = encoded & ~0xFFFFL;
        if (modifierBits != 0)
        {
            reason = "modifier_encoding_not_audited";
            return false;
        }

        var virtualKey = (int)(encoded & 0xFFFF);
        if (!TryFormatVirtualKey(virtualKey, out hotkey))
        {
            reason = "virtual_key_not_supported";
            return false;
        }

        reason = "current_mapping_verified";
        return true;
    }

    private static bool TryFormatVirtualKey(int virtualKey, out string hotkey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            hotkey = ((char)virtualKey).ToString();
            return true;
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            hotkey = $"F{virtualKey - 0x6F}";
            return true;
        }

        hotkey = virtualKey switch
        {
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            _ => string.Empty
        };
        return hotkey.Length > 0;
    }

    private static string ComputeMappingFingerprint(IEnumerable<GameHotkeyBinding> bindings)
    {
        var normalized = string.Join(
            ';',
            bindings.OrderBy(binding => binding.ActionId)
                .Select(binding => $"{binding.ActionId}={binding.EncodedCurrentKey}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static bool TryDecodeHexFileStem(string value, out string decoded)
    {
        decoded = string.Empty;
        value = value.Trim();
        if (!value.StartsWith('#') || value.Length <= 1 || (value.Length - 1) % 2 != 0)
            return false;

        var hex = value[1..];
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return false;
        }

        decoded = Encoding.GetEncoding(936).GetString(bytes).Trim('\0', ' ');
        return decoded.Length > 0;
    }

    private static string NormalizeRoleName(string? value) =>
        (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC);

    private static string? ResolveAccountsRoot(BoundGameWindow window)
    {
        if (string.IsNullOrWhiteSpace(window.ProcessPath))
            return null;

        var directory = Path.GetDirectoryName(Path.GetFullPath(window.ProcessPath));
        for (var depth = 0; depth < 5 && !string.IsNullOrWhiteSpace(directory); depth++)
        {
            var candidate = Path.Combine(directory, "Accounts");
            if (Directory.Exists(candidate))
                return candidate;
            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }
}
