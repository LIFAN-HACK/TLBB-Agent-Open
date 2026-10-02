namespace TlbbAgent.Contracts;

public static class ActivityFeatureKeys
{
    public const string Rebel = FeatureKeys.ActivityRebel;
    public const string SmallNpc = FeatureKeys.ActivityXiaoxiao;
    public const string Monk = FeatureKeys.ActivityMonk;
}

public enum ActivityPointSelectionMode
{
    Automatic = 0,
    ManualArrival = 1
}

public sealed class ActivityFeaturePreference
{
    public bool Enabled { get; set; }
    public ActivityPointSelectionMode PointSelectionMode { get; set; } = ActivityPointSelectionMode.Automatic;
}

public sealed class ActivityCenterConfig
{
    public ActivityFeaturePreference Rebel { get; set; } = new();
    public ActivityFeaturePreference SmallNpc { get; set; } = new();
    public ActivityFeaturePreference Monk { get; set; } = new();
}

public static class ActivityCenterConfigNormalizer
{
    public static ActivityCenterConfig Normalize(ActivityCenterConfig? config)
    {
        config ??= new ActivityCenterConfig();
        config.Rebel = NormalizeFeature(config.Rebel);
        config.SmallNpc = NormalizeFeature(config.SmallNpc);
        config.Monk = NormalizeFeature(config.Monk);
        return config;
    }

    private static ActivityFeaturePreference NormalizeFeature(ActivityFeaturePreference? feature)
    {
        feature ??= new ActivityFeaturePreference();
        if (!Enum.IsDefined(feature.PointSelectionMode))
            feature.PointSelectionMode = ActivityPointSelectionMode.Automatic;
        return feature;
    }
}
