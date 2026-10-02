using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

internal static class TargetPolicyRuntime
{
    public static bool RequiresTargetName(TargetingPreset targeting)
    {
        if (!targeting.L2TargetNameEnabled)
            return false;

        if (targeting.SwitchMode == TargetSwitchMode.BossWhitelist
            || !string.IsNullOrWhiteSpace(targeting.TargetNameWhitelist)
            || !string.IsNullOrWhiteSpace(targeting.TargetNameBlacklist))
        {
            return true;
        }

        var policy = targeting.Policy;
        if (policy?.Enabled != true)
            return false;

        return policy.Categories
            .Where(category => category.Enabled)
            .Any(category =>
                !category.AllowUnlisted
                || category.Entries.Any(entry => entry.Enabled));
    }
}
