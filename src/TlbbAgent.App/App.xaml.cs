using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using TlbbAgent.App.ViewModels;
using TlbbAgent.Contracts;
using TlbbAgent.Core;

namespace TlbbAgent.App;

public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;
    public static IServiceProvider Services { get; private set; } = null!;
    public static string BuildFingerprint { get; private set; } = "UNKNOWN";

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = SingleInstanceGuard.Acquire();
        if (!_singleInstance.IsPrimaryInstance)
        {
            MessageBox.Show(
                "龙伴行已经运行，请使用现有窗口。",
                "龙伴行",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // 用户只看到状态和反馈编号；异常类型、堆栈、路径等内部细节不落用户日志。
        DispatcherUnhandledException += (_, args) =>
        {
            var incidentId = WriteSafeIncidentLog("运行", args.Exception);
            MessageBox.Show(
                $"TLBB Agent 运行出现异常，请重新启动应用。\n\n反馈编号：{incidentId}",
                "TLBB Agent",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteSafeIncidentLog("运行", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteSafeIncidentLog("后台任务", args.Exception);
            args.SetObserved();
        };

        try
        {
            base.OnStartup(e);
            BuildFingerprint = RuntimeBuildIdentity.ValidateCurrentBuild();

            var services = new ServiceCollection();
            services.AddTlbbCore();
            services.AddSingleton<BindingPanelViewModel>();
            services.AddSingleton<LogPanelViewModel>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
            Services = services.BuildServiceProvider();

            var init = Services.GetRequiredService<IAppInitService>();
            init.Initialize();

            var log = Services.GetRequiredService<ILogService>();
            log.Info("App", $"龙伴行已启动，运行版本 {BuildFingerprint}。");

            var window = Services.GetRequiredService<MainWindow>();
            window.DataContext = Services.GetRequiredService<MainViewModel>();
            // Keep first paint on primary screen so it does not look like a flash-crash off-screen.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 100;
            window.Top = 80;
            window.Show();
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;

            _ = Services.GetRequiredService<IRuntimeWarmupService>().WarmupAsync();
        }
        catch (Exception ex)
        {
            var incidentId = WriteSafeIncidentLog("启动", ex);
            MessageBox.Show(
                $"TLBB Agent 启动失败，请重新打开应用。\n\n反馈编号：{incidentId}",
                "TLBB Agent",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (Services as IDisposable)?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static string WriteSafeIncidentLog(string area, Exception? exception)
    {
        var incidentId = $"{DateTime.Now:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(6)}";
        try
        {
            var logs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ProductIdentity.DataFolderName,
                "logs");
            Directory.CreateDirectory(logs);
            var path = Path.Combine(logs, "support-incidents.log");
            File.AppendAllText(
                path,
                $"{DateTime.Now:O}\t{incidentId}\t{area}\t操作未完成{Environment.NewLine}");
        }
        catch
        {
            // best-effort only
        }

#if DEBUG
        WriteDeveloperIncidentDetails(incidentId, area, exception);
#endif

        return incidentId;
    }

#if DEBUG
    private static void WriteDeveloperIncidentDetails(string incidentId, string area, Exception? exception)
    {
        if (exception is null
            || !string.Equals(
                Environment.GetEnvironmentVariable("TLBB_AGENT_INTERNAL_DIAGNOSTICS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var root = Path.Combine(Path.GetTempPath(), "TLBB-Agent-internal-diagnostics");
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, $"incident-{incidentId}.txt"),
                $"Area: {area}{Environment.NewLine}{exception}");
        }
        catch
        {
            // 显式开发诊断也不能影响应用运行。
        }
    }
#endif
}
