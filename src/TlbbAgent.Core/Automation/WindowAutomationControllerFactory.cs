using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public interface IWindowAutomationControllerFactory
{
    IAutomationController? Create(string key, IServiceProvider services);
}

public sealed class WindowAutomationControllerFactory : IWindowAutomationControllerFactory
{
    private static readonly IReadOnlyDictionary<string, Type> ControllerTypes =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["hang.normal"] = typeof(NormalHangController),
            ["hang.return"] = typeof(ReturnPointController),
            ["hang.horse"] = typeof(HorseBanditModeController),
            ["hang.dungeon"] = typeof(DungeonController),
            ["hang.utility"] = typeof(UtilityController),
            ["act.grab"] = typeof(NpcGrabController),
            ["act.rebel"] = typeof(AutoRebelController),
            ["act.monk"] = typeof(AutoMonkController),
            ["life.collect"] = typeof(LifeCollectController),
            ["life.trade"] = typeof(TradeController),
            ["hang.material"] = typeof(MaterialCraftController),
            ["life.map"] = typeof(TreasureMapController),
            ["task.school"] = typeof(SchoolTaskController),
            ["task.guild"] = typeof(GuildContributionController),
            [FeatureKeys.LifeShout] = typeof(ShoutController),
            [FeatureKeys.UtilityAutoLogin] = typeof(AutoLoginController),
            [FeatureKeys.UtilityAutoReconnect] = typeof(AutoReconnectController)
        };

    public IAutomationController? Create(string key, IServiceProvider services) =>
        ControllerTypes.TryGetValue(key, out var controllerType)
            ? (IAutomationController)ActivatorUtilities.CreateInstance(services, controllerType)
            : null;
}

internal sealed class WindowAutomationServiceProvider : IServiceProvider
{
    private static readonly HashSet<Type> SnapshotServiceTypes =
    [
        typeof(IGlobalConfigService),
        typeof(IReturnPointService),
        typeof(IHorseBanditConfigService),
        typeof(INpcGrabConfigService),
        typeof(IAutoRebelConfigService),
        typeof(IAutoMonkConfigService),
        typeof(IDungeonConfigService),
        typeof(ILifeCollectConfigService),
        typeof(ITradeConfigService),
        typeof(IMaterialCraftConfigService),
        typeof(ISchoolTaskConfigService),
        typeof(ITreasureMapConfigService),
        typeof(IGuildContributionConfigService),
        typeof(IUtilityConfigService),
        typeof(IShoutConfigService),
        typeof(IAutoLoginConfigService),
        typeof(IAutoReconnectConfigService)
    ];

    private readonly object _gate = new();
    private readonly IServiceProvider _root;
    private readonly Dictionary<Type, SessionConfigProxy> _configProxies = new();

    public WindowAutomationServiceProvider(IServiceProvider root)
    {
        _root = root;
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceProvider))
            return this;

        if (!SnapshotServiceTypes.Contains(serviceType))
            return _root.GetService(serviceType);

        lock (_gate)
        {
            if (_configProxies.TryGetValue(serviceType, out var existing))
                return existing.Proxy;

            var source = _root.GetService(serviceType)
                ?? throw new InvalidOperationException($"Configuration service {serviceType.Name} is not registered.");
            var proxyObject = DispatchProxy.Create(serviceType, typeof(SessionConfigProxy));
            var proxy = (SessionConfigProxy)proxyObject;
            proxy.Initialize(serviceType, source, proxyObject);
            proxy.Refresh();
            _configProxies.Add(serviceType, proxy);
            return proxyObject;
        }
    }

    public void RefreshConfiguration()
    {
        lock (_gate)
        {
            foreach (var proxy in _configProxies.Values)
                proxy.Refresh();
        }
    }

    public void ConfigureWindowInput(BoundGameWindow window)
    {
        if (_root.GetService(typeof(IGlobalConfigService)) is not IGlobalConfigService)
            return;

        var global = GetService(typeof(IGlobalConfigService)) as IGlobalConfigService;
        var input = _root.GetService(typeof(IInputService)) as IInputService;
        if (global is null || input is null)
            return;
        input.ConfigureWindowInput(
            window.Hwnd,
            global.Current.InputDispatchMode,
            global.Current.YieldToManualInput,
            global.Current.ManualInputYieldMs);
    }
}

public class SessionConfigProxy : DispatchProxy
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true
    };

    private readonly Dictionary<string, object?> _getterSnapshots = new(StringComparer.Ordinal);
    private Type _interfaceType = null!;
    private object _source = null!;
    private EventHandler? _changed;

    public object Proxy { get; private set; } = null!;

    public void Initialize(Type interfaceType, object source, object proxy)
    {
        _interfaceType = interfaceType;
        _source = source;
        Proxy = proxy;
    }

    public void Refresh()
    {
        var refreshed = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in _interfaceType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is null)
                continue;
            var value = property.GetValue(_source);
            refreshed[property.GetMethod.Name] = CloneValue(value, property.PropertyType);
        }

        _getterSnapshots.Clear();
        foreach (var pair in refreshed)
            _getterSnapshots[pair.Key] = pair.Value;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            throw new InvalidOperationException("Configuration proxy received an unknown method.");

        if (targetMethod.Name == "add_Changed")
        {
            _changed += (EventHandler)args![0]!;
            return null;
        }

        if (targetMethod.Name == "remove_Changed")
        {
            _changed -= (EventHandler)args![0]!;
            return null;
        }

        if (_getterSnapshots.TryGetValue(targetMethod.Name, out var snapshot))
            return snapshot;

        if (targetMethod.Name == nameof(IGlobalConfigService.NotifyChanged)
            || targetMethod.Name == nameof(IMaterialCraftConfigService.NotifyChanged)
            || targetMethod.Name == nameof(IReturnPointService.NotifyChanged)
            || targetMethod.Name == nameof(IUtilityConfigService.NotifyChanged))
        {
            _changed?.Invoke(Proxy, EventArgs.Empty);
            return null;
        }

        if (targetMethod.Name == nameof(IGlobalConfigService.SchoolProvidesHeal))
            return targetMethod.Invoke(_source, args);

        if (targetMethod.DeclaringType == typeof(object))
            return targetMethod.Invoke(_source, args);

        throw new InvalidOperationException(
            $"Runtime configuration snapshot is read-only: {_interfaceType.Name}.{targetMethod.Name}.");
    }

    private static object? CloneValue(object? value, Type declaredType)
    {
        if (value is null)
            return null;

        var runtimeType = value.GetType();
        if (runtimeType.IsPrimitive
            || runtimeType.IsEnum
            || value is string
            || value is decimal
            || value is DateTime
            || value is DateTimeOffset
            || value is Guid
            || value is TimeSpan)
        {
            return value;
        }

        var cloneType = ResolveCloneType(declaredType, runtimeType);
        var json = JsonSerializer.Serialize(value, runtimeType, JsonOptions);
        return JsonSerializer.Deserialize(json, cloneType, JsonOptions)
            ?? throw new InvalidOperationException($"Unable to clone {runtimeType.Name} for a window session.");
    }

    private static Type ResolveCloneType(Type declaredType, Type runtimeType)
    {
        if (declaredType.IsArray)
            return declaredType;

        if (declaredType.IsGenericType)
        {
            var genericDefinition = declaredType.GetGenericTypeDefinition();
            if (genericDefinition == typeof(IReadOnlyList<>)
                || genericDefinition == typeof(IReadOnlyCollection<>)
                || genericDefinition == typeof(IEnumerable<>))
            {
                return typeof(List<>).MakeGenericType(declaredType.GetGenericArguments());
            }
        }

        return runtimeType;
    }
}
