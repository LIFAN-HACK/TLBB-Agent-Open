using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class RoleIdentityService : IRoleIdentityService
{
    private const int StoreSchemaVersion = 1;
    private const int CaptureSampleCount = 3;
    private const double AutoMatchThreshold = 0.86;
    private const double CandidateThreshold = 0.72;
    private const double RequiredMargin = 0.06;
    private const double CompatibleNameThreshold = 0.60;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IWindowCaptureService _capture;
    private readonly IOpenTextRecognizer _openText;
    private readonly ILogService _log;
    private readonly string _storePath;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _storeGate = new();
    private RoleIdentityStore _store;

    public RoleIdentityService(
        IWindowCaptureService capture,
        IOpenTextRecognizer openText,
        IAppInitService appInit,
        ILogService log)
    {
        _capture = capture;
        _openText = openText;
        _log = log;
        _storePath = Path.Combine(appInit.Paths.Data, "role-identities.json");
        _store = LoadStore();
    }

    public IReadOnlyList<CharacterIdentityRecord> Identities
    {
        get
        {
            lock (_storeGate)
                return _store.Identities.OrderBy(item => item.DisplayName, StringComparer.Ordinal).ToArray();
        }
    }

    public async Task<RoleIdentityResolution> ResolveAsync(
        BoundGameWindow window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var observation = await CaptureObservationAsync(window, cancellationToken).ConfigureAwait(false);
            if (!HasUsableObservation(observation))
            {
                return new RoleIdentityResolution(
                    RoleIdentityResolutionStatus.Unavailable,
                    observation,
                    null,
                    Array.Empty<RoleIdentityCandidate>(),
                    "角色区域截图不可用或质量不足，未自动加载任何配置。");
            }

            CharacterIdentityRecord[] identities;
            lock (_storeGate)
                identities = _store.Identities.ToArray();

            var scored = identities
                .Select(identity => Score(observation, identity))
                .Where(candidate => candidate.Score >= CandidateThreshold)
                .OrderByDescending(candidate => candidate.Score)
                .ToArray();
            var candidates = scored
                .Take(4)
                .Select(candidate => new RoleIdentityCandidate(
                    candidate.Identity.CharacterProfileId,
                    candidate.Identity.DisplayName,
                    candidate.Identity.ProfileName,
                    candidate.Score))
                .ToArray();

            if (scored.Length == 0)
            {
                return new RoleIdentityResolution(
                    RoleIdentityResolutionStatus.NeedsEnrollment,
                    observation,
                    null,
                    candidates,
                    "未找到可信历史角色，需要首次确认。");
            }

            var top = scored[0];
            var margin = scored.Length == 1 ? 1 : top.Score - scored[1].Score;
            if (top.StrongEvidence && top.Score >= AutoMatchThreshold && margin >= RequiredMargin)
            {
                var updated = Touch(top.Identity, observation);
                ReplaceAndPersist(updated);
                return new RoleIdentityResolution(
                    RoleIdentityResolutionStatus.Matched,
                    observation,
                    updated,
                    candidates,
                    $"角色指纹匹配成功，评分 {top.Score:0.000}，区分度 {margin:0.000}。");
            }

            return new RoleIdentityResolution(
                RoleIdentityResolutionStatus.Ambiguous,
                observation,
                null,
                candidates,
                $"存在相似角色但证据不足以自动选择，最高评分 {top.Score:0.000}，区分度 {margin:0.000}。");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<RoleIdentityResolution> EnrollAsync(
        BoundGameWindow window,
        string displayName,
        string profileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        displayName = NormalizeDisplayName(displayName);
        profileName = profileName?.Trim() ?? string.Empty;
        if (displayName.Length == 0)
            throw new ArgumentException("角色名称不能为空。", nameof(displayName));
        if (profileName.Length == 0)
            throw new ArgumentException("角色配置名称不能为空。", nameof(profileName));

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var observation = await CaptureObservationAsync(window, cancellationToken).ConfigureAwait(false);
            if (!HasUsableObservation(observation))
            {
                return new RoleIdentityResolution(
                    RoleIdentityResolutionStatus.Unavailable,
                    observation,
                    null,
                    Array.Empty<RoleIdentityCandidate>(),
                    "角色区域截图不可用，未创建不可靠的身份记录。");
            }


            if (!NamesCompatible(displayName, observation.SuggestedName))
            {
                return new RoleIdentityResolution(
                    RoleIdentityResolutionStatus.NeedsEnrollment,
                    observation,
                    null,
                    Array.Empty<RoleIdentityCandidate>(),
                    $"当前画面识别名“{observation.SuggestedName}”与输入名“{displayName}”不一致，已阻止错误建档。");
            }

            CharacterIdentityRecord? existing;
            lock (_storeGate)
            {
                existing = _store.Identities.FirstOrDefault(item =>
                    string.Equals(NormalizeDisplayName(item.DisplayName), displayName, StringComparison.OrdinalIgnoreCase)
                    && ServerCompatible(observation.ServerKey, item.ServerKey)
                    && Score(observation, item) is { StrongEvidence: true, Score: >= CandidateThreshold });
            }

            var now = DateTimeOffset.UtcNow;
            var record = existing is null
                ? new CharacterIdentityRecord(
                    Guid.NewGuid().ToString("N"),
                    displayName,
                    profileName,
                    observation.ServerKey,
                    observation.PortraitHash,
                    observation.NamePlateHash,
                    observation.SuggestedName,
                    now,
                    now)
                : existing with
                {
                    DisplayName = displayName,
                    ProfileName = profileName,
                    ServerKey = PreferNonEmpty(observation.ServerKey, existing.ServerKey),
                    PortraitHash = PreferNonEmpty(observation.PortraitHash, existing.PortraitHash),
                    NamePlateHash = PreferNonEmpty(observation.NamePlateHash, existing.NamePlateHash),
                    RecognizedName = PreferNonEmpty(observation.SuggestedName, existing.RecognizedName),
                    LastSeenAt = now
                };

            ReplaceAndPersist(record);
            _log.Info("RoleIdentity", $"角色配置已建立：{record.DisplayName}。", window.Id);
            return Matched(observation, record, "首次角色确认已保存。");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<RoleIdentityResolution> ConfirmExistingAsync(
        BoundGameWindow window,
        string characterProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        characterProfileId = characterProfileId?.Trim() ?? string.Empty;
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CharacterIdentityRecord? existing;
            lock (_storeGate)
            {
                existing = _store.Identities.FirstOrDefault(item =>
                    string.Equals(item.CharacterProfileId, characterProfileId, StringComparison.Ordinal));
            }

            if (existing is null)
            {
                var empty = EmptyObservation(window, "指定的历史角色不存在。");
                return new RoleIdentityResolution(
                    RoleIdentityResolutionStatus.Unavailable,
                    empty,
                    null,
                    Array.Empty<RoleIdentityCandidate>(),
                    "指定的历史角色不存在。");
            }

            var observation = await CaptureObservationAsync(window, cancellationToken).ConfigureAwait(false);
            if (!HasUsableObservation(observation))
            {
                return new RoleIdentityResolution(
                    RoleIdentityResolutionStatus.Unavailable,
                    observation,
                    null,
                    Array.Empty<RoleIdentityCandidate>(),
                    "当前截图质量不足，未覆盖历史指纹。");
            }

            var verified = Score(observation, existing);
            if (!verified.StrongEvidence || verified.Score < CandidateThreshold)
            {
                return new RoleIdentityResolution(
                    RoleIdentityResolutionStatus.Ambiguous,
                    observation,
                    null,
                    new[]
                    {
                        new RoleIdentityCandidate(
                            existing.CharacterProfileId,
                            existing.DisplayName,
                            existing.ProfileName,
                            verified.Score)
                    },
                    $"当前角色与“{existing.DisplayName}”的视觉/姓名证据不一致，已阻止覆盖历史角色指纹。");
            }

            var updated = existing with
            {
                ServerKey = PreferNonEmpty(observation.ServerKey, existing.ServerKey),
                PortraitHash = PreferNonEmpty(observation.PortraitHash, existing.PortraitHash),
                NamePlateHash = PreferNonEmpty(observation.NamePlateHash, existing.NamePlateHash),
                RecognizedName = PreferNonEmpty(observation.SuggestedName, existing.RecognizedName),
                LastSeenAt = DateTimeOffset.UtcNow
            };
            ReplaceAndPersist(updated);
            return Matched(observation, updated, "已确认并更新该角色的视觉指纹。");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<RoleIdentityObservation> CaptureObservationAsync(
        BoundGameWindow window,
        CancellationToken cancellationToken)
    {
        var portraitHashes = new List<ulong>(CaptureSampleCount);
        var nameHashes = new List<ulong>(CaptureSampleCount);
        var qualities = new List<double>(CaptureSampleCount);
        string suggestedName = string.Empty;
        string recognitionMessage = _openText.IsAvailable ? "姓名文字未确认" : "文字识别不可用";

        for (var index = 0; index < CaptureSampleCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var avatar = await Task.Run(() => _capture.CaptureAvatar(window.Hwnd), cancellationToken).ConfigureAwait(false);
            if (avatar is not null && IsValidFrame(avatar))
            {
                var portraitRect = RelativeRect(avatar, 0.02, 0.04, 0.31, 0.86);
                var nameRect = RelativeRect(avatar, 0.34, 0.03, 0.63, 0.31);
                var portraitQuality = RegionQuality(avatar, portraitRect);
                var nameQuality = RegionQuality(avatar, nameRect);
                var quality = Math.Clamp((portraitQuality * 0.6) + (nameQuality * 0.4), 0, 1);
                if (quality >= 0.12)
                {
                    portraitHashes.Add(ComputeDHash(avatar, portraitRect));
                    nameHashes.Add(ComputeDHash(avatar, nameRect));
                    qualities.Add(quality);
                }

                if (suggestedName.Length == 0 && _openText.IsAvailable && nameQuality >= 0.12)
                {
                    var nameCrop = Crop(avatar, nameRect);
                    if (nameCrop is not null)
                    {
                        var text = _openText.Recognize(nameCrop, OpenTextSlot.Generic);
                        var normalized = NormalizeDisplayName(text.Text);
                        if (text.Confidence >= 0.45 && normalized.Length is >= 1 and <= 16)
                        {
                            suggestedName = normalized;
                            recognitionMessage = $"姓名文字置信度 {text.Confidence:0.00}";
                        }
                        else
                        {
                            recognitionMessage = $"姓名文字未确认，置信度 {text.Confidence:0.00}";
                        }
                    }
                }
            }

            if (index + 1 < CaptureSampleCount)
                await Task.Delay(55, cancellationToken).ConfigureAwait(false);
        }

        if (portraitHashes.Count == 0 || nameHashes.Count == 0)
            return EmptyObservation(window, recognitionMessage);

        var portraitHash = MajorityHash(portraitHashes);
        var nameHash = MajorityHash(nameHashes);
        var qualityAverage = qualities.Count == 0 ? 0 : qualities.Average();
        return new RoleIdentityObservation(
            ExtractServerKey(window.Title),
            suggestedName,
            portraitHash.ToString("X16"),
            nameHash.ToString("X16"),
            Math.Min(portraitHashes.Count, nameHashes.Count),
            qualityAverage,
            $"{portraitHashes.Count} 帧视觉指纹；{recognitionMessage}");
    }

    private static CandidateScore Score(RoleIdentityObservation observation, CharacterIdentityRecord identity)
    {
        if (!ServerCompatible(observation.ServerKey, identity.ServerKey))
            return new CandidateScore(identity, 0, false);

        var portrait = HashSimilarity(observation.PortraitHash, identity.PortraitHash);
        var namePlate = HashSimilarity(observation.NamePlateHash, identity.NamePlateHash);
        var observedName = NormalizeDisplayName(observation.SuggestedName);
        var displayName = NormalizeDisplayName(identity.DisplayName);
        var recognizedName = NormalizeDisplayName(identity.RecognizedName);
        var storedName = recognizedName.Length > 0 ? recognizedName : displayName;
        var identityRecordIsConsistent = NamesCompatible(displayName, recognizedName);
        var textSimilarity = NameSimilarity(observedName, storedName);
        var textCompatible = observedName.Length == 0
                             || storedName.Length == 0
                             || textSimilarity >= CompatibleNameThreshold;

        // A role record whose label conflicts with its captured name is corrupted. Never
        // auto-load it, even if a reused portrait or a stale crop happens to score highly.
        if (!identityRecordIsConsistent || !textCompatible)
            return new CandidateScore(identity, 0, false);

        var score = (namePlate * 0.48) + (portrait * 0.32) + (textSimilarity * 0.17);
        if (!string.IsNullOrWhiteSpace(observation.ServerKey)
            && !string.IsNullOrWhiteSpace(identity.ServerKey))
        {
            score += 0.03;
        }

        score = Math.Clamp(score, 0, 1);
        var strong = (textSimilarity >= CompatibleNameThreshold && namePlate >= 0.62 && portrait >= 0.55)
                     || (namePlate >= 0.90 && portrait >= 0.78);
        return new CandidateScore(identity, score, strong);
    }

    private static CharacterIdentityRecord Touch(
        CharacterIdentityRecord identity,
        RoleIdentityObservation observation) =>
        identity with
        {
            RecognizedName = PreferNonEmpty(observation.SuggestedName, identity.RecognizedName),
            LastSeenAt = DateTimeOffset.UtcNow
        };

    private RoleIdentityResolution Matched(
        RoleIdentityObservation observation,
        CharacterIdentityRecord record,
        string reason) =>
        new(
            RoleIdentityResolutionStatus.Matched,
            observation,
            record,
            new[] { new RoleIdentityCandidate(record.CharacterProfileId, record.DisplayName, record.ProfileName, 1) },
            reason);

    private void ReplaceAndPersist(CharacterIdentityRecord record)
    {
        lock (_storeGate)
        {
            var index = _store.Identities.FindIndex(item =>
                string.Equals(item.CharacterProfileId, record.CharacterProfileId, StringComparison.Ordinal));
            if (index >= 0)
                _store.Identities[index] = record;
            else
                _store.Identities.Add(record);
            PersistStoreLocked();
        }
    }

    private RoleIdentityStore LoadStore()
    {
        try
        {
            if (!File.Exists(_storePath))
                return new RoleIdentityStore();
            var json = File.ReadAllText(_storePath);
            var store = JsonSerializer.Deserialize<RoleIdentityStore>(json, JsonOptions) ?? new RoleIdentityStore();
            store.Identities ??= new List<CharacterIdentityRecord>();
            return store;
        }
        catch (Exception ex)
        {
            _log.Warn("RoleIdentity", $"角色身份索引读取失败，已使用空索引：{ex.Message}");
            return new RoleIdentityStore();
        }
    }

    private void PersistStoreLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var tempPath = _storePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(_store, JsonOptions));
            File.Move(tempPath, _storePath, true);
        }
        catch (Exception ex)
        {
            _log.Warn("RoleIdentity", $"角色身份索引保存失败：{ex.Message}");
        }
    }

    private static bool HasUsableObservation(RoleIdentityObservation observation) =>
        observation.SampleCount >= 2
        && observation.Quality >= 0.12
        && observation.PortraitHash.Length == 16
        && observation.NamePlateHash.Length == 16;

    private static bool IsValidFrame(CaptureResult frame) =>
        frame.Width > 8
        && frame.Height > 8
        && frame.Pixels.Length >= frame.Width * frame.Height * 4;

    private static PixelRect RelativeRect(
        CaptureResult frame,
        double x,
        double y,
        double width,
        double height)
    {
        var left = Math.Clamp((int)Math.Round(frame.Width * x), 0, frame.Width - 1);
        var top = Math.Clamp((int)Math.Round(frame.Height * y), 0, frame.Height - 1);
        var right = Math.Clamp((int)Math.Round(frame.Width * (x + width)), left + 1, frame.Width);
        var bottom = Math.Clamp((int)Math.Round(frame.Height * (y + height)), top + 1, frame.Height);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static double RegionQuality(CaptureResult frame, PixelRect rect)
    {
        double sum = 0;
        double sumSquares = 0;
        var count = 0;
        var stepX = Math.Max(1, rect.Width / 24);
        var stepY = Math.Max(1, rect.Height / 16);
        for (var y = rect.Y; y < rect.Y + rect.Height; y += stepY)
        {
            for (var x = rect.X; x < rect.X + rect.Width; x += stepX)
            {
                var luminance = Luminance(frame, x, y);
                sum += luminance;
                sumSquares += luminance * luminance;
                count++;
            }
        }

        if (count == 0)
            return 0;
        var mean = sum / count;
        var variance = Math.Max(0, (sumSquares / count) - (mean * mean));
        var standardDeviation = Math.Sqrt(variance);
        return Math.Clamp(standardDeviation / 55.0, 0, 1);
    }

    private static ulong ComputeDHash(CaptureResult frame, PixelRect rect)
    {
        ulong hash = 0;
        var bit = 0;
        for (var row = 0; row < 8; row++)
        {
            var y = rect.Y + Math.Min(rect.Height - 1, (row * rect.Height + rect.Height / 2) / 8);
            for (var column = 0; column < 8; column++)
            {
                var leftX = rect.X + Math.Min(rect.Width - 1, (column * rect.Width + rect.Width / 2) / 9);
                var rightX = rect.X + Math.Min(rect.Width - 1, ((column + 1) * rect.Width + rect.Width / 2) / 9);
                if (Luminance(frame, leftX, y) > Luminance(frame, rightX, y))
                    hash |= 1UL << bit;
                bit++;
            }
        }

        return hash;
    }

    private static byte Luminance(CaptureResult frame, int x, int y)
    {
        var index = ((y * frame.Width) + x) * 4;
        var blue = frame.Pixels[index];
        var green = frame.Pixels[index + 1];
        var red = frame.Pixels[index + 2];
        return (byte)((red * 77 + green * 150 + blue * 29) >> 8);
    }

    private static ulong MajorityHash(IReadOnlyList<ulong> hashes)
    {
        ulong result = 0;
        for (var bit = 0; bit < 64; bit++)
        {
            var mask = 1UL << bit;
            if (hashes.Count(value => (value & mask) != 0) * 2 >= hashes.Count)
                result |= mask;
        }
        return result;
    }

    private static double HashSimilarity(string left, string right)
    {
        if (!ulong.TryParse(left, System.Globalization.NumberStyles.HexNumber, null, out var leftValue)
            || !ulong.TryParse(right, System.Globalization.NumberStyles.HexNumber, null, out var rightValue))
        {
            return 0;
        }

        return 1 - (BitOperations.PopCount(leftValue ^ rightValue) / 64.0);
    }

    private static CaptureResult? Crop(CaptureResult frame, PixelRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return null;
        var pixels = new byte[rect.Width * rect.Height * 4];
        var sourceStride = frame.Width * 4;
        var destinationStride = rect.Width * 4;
        for (var row = 0; row < rect.Height; row++)
        {
            Buffer.BlockCopy(
                frame.Pixels,
                ((rect.Y + row) * sourceStride) + rect.X * 4,
                pixels,
                row * destinationStride,
                destinationStride);
        }

        return new CaptureResult(rect.Width, rect.Height, pixels)
        {
            Sequence = frame.Sequence,
            CapturedAt = frame.CapturedAt,
            CaptureSource = frame.CaptureSource
        };
    }

    private static string ExtractServerKey(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;
        var matches = Regex.Matches(title, @"[（(]([^()（）]{2,64})[）)]", RegexOptions.CultureInvariant);
        var candidate = matches
            .Select(match => match.Groups[1].Value.Trim())
            .LastOrDefault(value => value.Contains('区') || value.Contains('服') || value.Contains(':') || value.Contains('：'));
        return NormalizeServer(candidate ?? string.Empty);
    }

    private static string NormalizeServer(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", string.Empty, RegexOptions.CultureInvariant);

    private static string NormalizeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var normalized = new string(value
            .Trim()
            .Where(character => char.IsLetterOrDigit(character) || character is '·' or '丶')
            .Take(20)
            .ToArray());
        return normalized;
    }

    private static bool NamesCompatible(string? left, string? right)
    {
        var normalizedLeft = NormalizeDisplayName(left);
        var normalizedRight = NormalizeDisplayName(right);
        return normalizedLeft.Length == 0
               || normalizedRight.Length == 0
               || NameSimilarity(normalizedLeft, normalizedRight) >= CompatibleNameThreshold;
    }

    private static double NameSimilarity(string? left, string? right)
    {
        var normalizedLeft = NormalizeDisplayName(left);
        var normalizedRight = NormalizeDisplayName(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
            return 0;
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
            return 1;

        var previous = Enumerable.Range(0, normalizedRight.Length + 1).ToArray();
        var current = new int[normalizedRight.Length + 1];
        for (var leftIndex = 1; leftIndex <= normalizedLeft.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= normalizedRight.Length; rightIndex++)
            {
                var substitutionCost = char.ToUpperInvariant(normalizedLeft[leftIndex - 1])
                                       == char.ToUpperInvariant(normalizedRight[rightIndex - 1])
                    ? 0
                    : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1),
                    previous[rightIndex - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return 1.0 - (previous[normalizedRight.Length] / (double)Math.Max(normalizedLeft.Length, normalizedRight.Length));
    }

    private static bool ServerCompatible(string left, string right) =>
        string.IsNullOrWhiteSpace(left)
        || string.IsNullOrWhiteSpace(right)
        || string.Equals(NormalizeServer(left), NormalizeServer(right), StringComparison.OrdinalIgnoreCase);

    private static string PreferNonEmpty(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static RoleIdentityObservation EmptyObservation(BoundGameWindow window, string reason) =>
        new(ExtractServerKey(window.Title), string.Empty, string.Empty, string.Empty, 0, 0, reason);

    private sealed record CandidateScore(
        CharacterIdentityRecord Identity,
        double Score,
        bool StrongEvidence);

    private readonly record struct PixelRect(int X, int Y, int Width, int Height);

    private sealed class RoleIdentityStore
    {
        public int SchemaVersion { get; set; } = StoreSchemaVersion;
        public List<CharacterIdentityRecord> Identities { get; set; } = new();
    }
}
