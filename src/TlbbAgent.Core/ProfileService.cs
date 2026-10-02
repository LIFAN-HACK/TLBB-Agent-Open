using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ProfileConfigService : IProfileService
{
    private readonly string _dir;
    private readonly ILogService _log;
    private const string Module = "Profile";

    public ProfileConfigService(IAppInitService init, ILogService log)
    {
        _dir = init.Paths.Profiles;
        _log = log;
    }

    public string? CurrentProfileName { get; private set; }

    public IReadOnlyList<string> ListProfileNames()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<string>();
        return Directory.GetFiles(_dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n)
            .ToList();
    }

    public void Save(string name, string jsonPayload)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathFor(name), jsonPayload);
        CurrentProfileName = name;
        _log.Info(Module, $"已保存配置方案：{name}");
    }

    public string? Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            _log.Warn(Module, $"配置方案不存在：{name}");
            return null;
        }
        CurrentProfileName = name;
        _log.Info(Module, $"已加载配置方案：{name}");
        return File.ReadAllText(path);
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
        if (CurrentProfileName == name) CurrentProfileName = null;
        _log.Info(Module, $"已删除配置方案：{name}");
    }

    private string PathFor(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return Path.Combine(_dir, name + ".json");
    }
}
