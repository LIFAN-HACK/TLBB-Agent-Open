using TlbbAgent.Contracts;
using TlbbAgent.Core;

var failures = new List<string>();
var root = Path.Combine(Path.GetTempPath(), "TlbbAgent.RoleIdentityHarness", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    var paths = new HarnessAppInitService(root);
    paths.Initialize();
    var log = new RuntimeLogService();
    var capture = new HarnessCaptureService(CreateAvatarFrame(11, 21));
    var openText = new HarnessOpenTextRecognizer("果咬不白", true);
    var service = new RoleIdentityService(capture, openText, paths, log);
    var firstWindow = Window(0x101, 1001, "（原始一区：江山如画）");
    var enrollment = await service.EnrollAsync(firstWindow, "果咬不白", "角色-果咬不白-A1");
    Check(enrollment.IsMatched && enrollment.Match is not null, "首次确认必须创建稳定角色档案", failures);

    var conflictingEnrollment = await service.EnrollAsync(firstWindow, "沃德霸霸", "角色-错误绑定");
    Check(!conflictingEnrollment.IsMatched && service.Identities.Count == 1,
        "OCR 姓名与输入姓名明显冲突时必须拒绝错误建档", failures);

    var persistedService = new RoleIdentityService(capture, openText, paths, log);
    var restartedWindow = Window(0x909, 9009, "（原始一区：江山如画）");
    var restarted = await persistedService.ResolveAsync(restartedWindow);
    Check(restarted.IsMatched
          && restarted.Match?.CharacterProfileId == enrollment.Match?.CharacterProfileId
          && restarted.Match?.ProfileName == "角色-果咬不白-A1",
        "PID/HWND 改变后必须通过视觉身份恢复同一配置", failures);

    capture.Frame = CreateAvatarFrame(11, 77);
    openText.Text = "另一个角色";
    var differentNamePlate = await persistedService.ResolveAsync(Window(0x202, 2002, "（原始一区：江山如画）"));
    Check(!differentNamePlate.IsMatched,
        "仅头像相似、姓名板不同不得自动加载旧角色配置", failures);

    var conflictingConfirmation = await persistedService.ConfirmExistingAsync(
        Window(0x203, 2003, "（原始一区：江山如画）"),
        enrollment.Match!.CharacterProfileId);
    Check(!conflictingConfirmation.IsMatched,
        "当前视觉/姓名证据与历史角色冲突时不得覆盖历史指纹", failures);

    openText.Text = "果咬不白";
    var sameNameDifferentVisual = await persistedService.EnrollAsync(
        Window(0x212, 2012, "（原始一区：江山如画）"),
        "果咬不白",
        "角色-同名但不同画面-B2");
    Check(sameNameDifferentVisual.IsMatched
          && sameNameDifferentVisual.Match?.CharacterProfileId != enrollment.Match?.CharacterProfileId,
        "同服同名但视觉证据不同必须建立独立档案，不能覆盖旧配置", failures);

    capture.Frame = CreateAvatarFrame(11, 21);
    openText.Text = "果咬不白";
    var differentServer = await persistedService.ResolveAsync(Window(0x303, 3003, "（原始二区：乾坤大挪移）"));
    Check(!differentServer.IsMatched,
        "同画面但服务器不同不得自动加载旧角色配置", failures);

    capture.Frame = CreateBlankFrame();
    var unavailable = await persistedService.ResolveAsync(Window(0x404, 4004, "（原始一区：江山如画）"));
    Check(unavailable.Status == RoleIdentityResolutionStatus.Unavailable,
        "空白或低质量截图必须拒绝自动识别", failures);

    capture.Frame = CreateAvatarFrame(11, 21);
    openText.Text = "果咬不白";
    var confirmed = await persistedService.ConfirmExistingAsync(
        Window(0x505, 5005, "（原始一区：江山如画）"),
        enrollment.Match!.CharacterProfileId);
    Check(confirmed.IsMatched && confirmed.Match?.LastSeenAt >= enrollment.Match.LastSeenAt,
        "人工确认历史角色必须刷新视觉证据与最后使用时间", failures);
}
finally
{
    try
    {
        Directory.Delete(root, true);
    }
    catch
    {
    }
}

if (failures.Count == 0)
{
    Console.WriteLine("ROLE_IDENTITY_HARNESS PASS");
    return 0;
}

foreach (var failure in failures)
    Console.Error.WriteLine("FAIL: " + failure);
return 1;

static BoundGameWindow Window(long hwnd, int pid, string title) => new()
{
    Hwnd = new nint(hwnd),
    Pid = pid,
    Title = $"《新天龙八部》 0.08.1808 {title}",
    ProcessName = "Game",
    ProcessPath = @"C:\Game\TLBB\Bin64\Game.exe",
    Bitness = ClientBitness.X64,
    ClientType = ClientType.Tlbb64,
    ClientWidth = 1920,
    ClientHeight = 1080
};

static CaptureResult CreateAvatarFrame(int portraitSeed, int nameSeed)
{
    const int width = 224;
    const int height = 86;
    var pixels = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var baseValue = (byte)(18 + ((x + y) % 17));
            SetPixel(pixels, width, x, y, baseValue, baseValue, baseValue);
        }
    }

    PaintPattern(pixels, width, 4, 3, 70, 74, portraitSeed);
    PaintPattern(pixels, width, 76, 2, 141, 27, nameSeed);
    return new CaptureResult(width, height, pixels) { Sequence = 1, CaptureSource = "harness" };
}

static CaptureResult CreateBlankFrame()
{
    const int width = 224;
    const int height = 86;
    return new CaptureResult(width, height, new byte[width * height * 4]) { Sequence = 1, CaptureSource = "harness" };
}

static void PaintPattern(byte[] pixels, int strideWidth, int left, int top, int width, int height, int seed)
{
    for (var y = top; y < top + height; y++)
    {
        for (var x = left; x < left + width; x++)
        {
            var value = (byte)(((x * (seed + 3)) ^ (y * (seed + 11)) ^ (x * y)) & 0xFF);
            SetPixel(pixels, strideWidth, x, y, value, (byte)(255 - value), (byte)((value + seed * 7) & 0xFF));
        }
    }
}

static void SetPixel(byte[] pixels, int width, int x, int y, byte red, byte green, byte blue)
{
    var index = ((y * width) + x) * 4;
    pixels[index] = blue;
    pixels[index + 1] = green;
    pixels[index + 2] = red;
    pixels[index + 3] = 255;
}

static void Check(bool condition, string message, ICollection<string> failures)
{
    if (!condition)
        failures.Add(message);
}

internal sealed class HarnessAppInitService : IAppInitService
{
    public HarnessAppInitService(string root)
    {
        Paths = new AppPaths
        {
            Root = root,
            Profiles = Path.Combine(root, "profiles"),
            Logs = Path.Combine(root, "logs"),
            Data = Path.Combine(root, "data"),
            Ocr = Path.Combine(root, "ocr"),
            Backups = Path.Combine(root, "backups"),
            LegacyImports = Path.Combine(root, "imports")
        };
    }

    public AppPaths Paths { get; }

    public void Initialize()
    {
        Directory.CreateDirectory(Paths.Root);
        Directory.CreateDirectory(Paths.Profiles);
        Directory.CreateDirectory(Paths.Logs);
        Directory.CreateDirectory(Paths.Data);
        Directory.CreateDirectory(Paths.Ocr);
        Directory.CreateDirectory(Paths.Backups);
        Directory.CreateDirectory(Paths.LegacyImports);
    }
}

internal sealed class HarnessCaptureService(CaptureResult frame) : IWindowCaptureService
{
    public CaptureResult Frame { get; set; } = frame;
    public CaptureResult? CaptureClient(nint hwnd) => Frame;
    public CaptureResult? CaptureRegion(nint hwnd, int x, int y, int w, int h) => Frame;
    public CaptureResult? CaptureAvatar(nint hwnd) => Frame;
}

internal sealed class HarnessOpenTextRecognizer(string text, bool available) : IOpenTextRecognizer
{
    public string Text { get; set; } = text;
    public bool IsAvailable { get; set; } = available;
    public string EngineId => "harness";

    public OpenTextResult Recognize(CaptureResult crop, OpenTextSlot slot = OpenTextSlot.Generic) =>
        IsAvailable
            ? new OpenTextResult(Text, 0.99, slot, PerceptionSourceKind.OpenText, EngineId)
            : OpenTextResult.Empty(slot, "disabled", EngineId);
}
