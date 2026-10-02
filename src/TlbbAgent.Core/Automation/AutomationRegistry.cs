using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class AutomationRegistry : IAutomationRegistry
{
    private readonly object _gate = new();
    private readonly IServiceProvider _rootServices;
    private readonly IWindowAutomationControllerFactory _factory;
    private readonly IReadOnlyList<IAutomationController> _catalogControllers;
    private readonly Dictionary<nint, WindowRuntime> _windows = new();

    public AutomationRegistry(
        INormalHangController normalHang,
        IReturnPointController returnPoint,
        IHorseBanditModeController horseBandit,
        IDungeonController dungeon,
        INpcGrabController npcGrab,
        IAutoRebelController autoRebel,
        IAutoMonkController autoMonk,
        ILifeCollectController lifeCollect,
        ITradeController trade,
        IMaterialCraftController materialCraft,
        ISchoolTaskController schoolTask,
        ITreasureMapController treasureMap,
        IGuildContributionController guildContribution,
        IUtilityController utility,
        IShoutController shout,
        IAutoLoginController autoLogin,
        IAutoReconnectController autoReconnect,
        IServiceProvider rootServices,
        IWindowAutomationControllerFactory factory)
    {
        _rootServices = rootServices;
        _factory = factory;
        _catalogControllers =
        [
            normalHang, returnPoint, horseBandit, dungeon, utility, npcGrab, autoRebel,
            autoMonk, lifeCollect, trade, materialCraft, schoolTask, treasureMap, guildContribution,
            shout, autoLogin, autoReconnect
        ];
    }

    public event EventHandler<AutomationControllerRegisteredEventArgs>? ControllerRegistered;

    public IReadOnlyList<IAutomationController> Controllers
    {
        get
        {
            lock (_gate)
            {
                return _catalogControllers
                    .Concat(_windows.Values.SelectMany(window => window.Controllers.Values))
                    .ToArray();
            }
        }
    }

    public void AttachAll(BoundGameWindow? window)
    {
        if (window is null)
            return;

        IAutomationController[] controllers;
        lock (_gate)
        {
            var runtime = GetOrCreateRuntime(window);
            runtime.Window = window;
            runtime.Services.ConfigureWindowInput(window);
            controllers = runtime.Controllers.Values.ToArray();
        }

        foreach (var controller in controllers)
            controller.Attach(window);
    }

    public IAutomationController? Find(string key) =>
        _catalogControllers.FirstOrDefault(controller => controller.Key == key);

    public IAutomationController? Find(BoundGameWindow window, string key) =>
        GetOrCreateController(window, key, refreshConfiguration: false);

    public IAutomationController? Prepare(BoundGameWindow window, string key) =>
        GetOrCreateController(window, key, refreshConfiguration: true);

    public IReadOnlyList<IAutomationController> FindForWindow(BoundGameWindow window)
    {
        lock (_gate)
        {
            return _windows.TryGetValue(window.Hwnd, out var runtime)
                ? runtime.Controllers.Values.ToArray()
                : Array.Empty<IAutomationController>();
        }
    }

    public BoundGameWindow? GetWindow(IAutomationController controller)
    {
        lock (_gate)
        {
            foreach (var runtime in _windows.Values)
            {
                if (runtime.Controllers.Values.Any(item => ReferenceEquals(item, controller)))
                    return runtime.Window;
            }
        }

        return null;
    }

    public bool RefreshConfiguration(BoundGameWindow window)
    {
        lock (_gate)
        {
            var runtime = GetOrCreateRuntime(window);
            if (runtime.Controllers.Values.Any(controller => IsActive(controller.RunState)))
                return false;

            runtime.Services.RefreshConfiguration();
            return true;
        }
    }

    public void StopWindow(BoundGameWindow window)
    {
        foreach (var controller in FindForWindow(window))
        {
            if (IsActive(controller.RunState))
                controller.Stop();
        }
    }

    public void StopAll()
    {
        IAutomationController[] controllers;
        lock (_gate)
            controllers = _windows.Values.SelectMany(window => window.Controllers.Values).ToArray();

        foreach (var controller in controllers)
        {
            if (IsActive(controller.RunState))
                controller.Stop();
        }
    }

    private IAutomationController? GetOrCreateController(
        BoundGameWindow window,
        string key,
        bool refreshConfiguration)
    {
        IAutomationController? controller;
        var created = false;

        lock (_gate)
        {
            var runtime = GetOrCreateRuntime(window);
            runtime.Window = window;

            if (refreshConfiguration
                && !runtime.Controllers.Values.Any(item => IsActive(item.RunState)))
            {
                runtime.Services.RefreshConfiguration();
            }

            runtime.Services.ConfigureWindowInput(window);

            if (!runtime.Controllers.TryGetValue(key, out controller))
            {
                controller = _factory.Create(key, runtime.Services);
                if (controller is null)
                    return null;
                runtime.Controllers.Add(key, controller);
                created = true;
            }

            controller.Attach(window);
        }

        if (created)
            ControllerRegistered?.Invoke(this, new AutomationControllerRegisteredEventArgs(window, controller));

        return controller;
    }

    private WindowRuntime GetOrCreateRuntime(BoundGameWindow window)
    {
        if (_windows.TryGetValue(window.Hwnd, out var existing))
            return existing;

        var created = new WindowRuntime(window, new WindowAutomationServiceProvider(_rootServices));
        _windows.Add(window.Hwnd, created);
        return created;
    }

    private static bool IsActive(RunState state) =>
        state is RunState.Running
            or RunState.Paused
            or RunState.Recovering
            or RunState.DeathReturn
            or RunState.Reconnecting
            or RunState.Switching;

    private sealed class WindowRuntime(
        BoundGameWindow window,
        WindowAutomationServiceProvider services)
    {
        public BoundGameWindow Window { get; set; } = window;
        public WindowAutomationServiceProvider Services { get; } = services;
        public Dictionary<string, IAutomationController> Controllers { get; } =
            new(StringComparer.Ordinal);
    }
}
