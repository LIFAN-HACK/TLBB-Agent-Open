using System.Diagnostics;
using System.IO;
using System.Text;

namespace TlbbAgent.App;

public static class ToolLauncher
{
    public static bool TryLaunchTargetSampler(out string detail)
    {
        try
        {
            var root = FindWorkspaceRoot();
            var project = Path.Combine(root, "tools", "TlbbTargetSampler", "TlbbTargetSampler.csproj");
            if (!File.Exists(project))
            {
                detail = $"找不到目标采集器：{project}";
                return false;
            }

            var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!File.Exists(dotnet))
                dotnet = "dotnet";

            Process.Start(new ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = $"run --project \"{project}\"",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            detail = "已打开目标采集器。";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"打开目标采集器失败：{ex.Message}";
            return false;
        }
    }

    public static bool TryOpenPath(string path, out string detail)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                detail = "路径为空。";
                return false;
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                detail = $"路径不存在：{path}";
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });

            detail = $"已打开：{path}";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"打开失败：{ex.Message}";
            return false;
        }
    }

    public static async Task<ToolRunResult> RunPowerShellScriptAsync(
        string scriptName,
        IEnumerable<string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var root = FindWorkspaceRoot();
            var script = Path.Combine(root, "tools", scriptName);
            if (!File.Exists(script))
                return new ToolRunResult(false, 1, "", $"找不到脚本：{script}");

            var start = new ProcessStartInfo
            {
                FileName = "powershell",
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };

            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);

            if (arguments is not null)
            {
                foreach (var arg in arguments)
                    start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start);
            if (process is null)
                return new ToolRunResult(false, 1, "", "脚本进程启动失败。");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ToolRunResult(process.ExitCode == 0, process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ToolRunResult(false, 1, "", ex.Message);
        }
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TlbbAgent.sln"))
                && Directory.Exists(Path.Combine(current.FullName, "tools", "TlbbTargetSampler")))
                return current.FullName;

            current = current.Parent;
        }

        return AppContext.BaseDirectory;
    }
}

public sealed record ToolRunResult(bool Success, int ExitCode, string Output, string Error);
