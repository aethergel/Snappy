using System.Collections;
using System.Reflection;
using Dalamud.Plugin.Ipc;

namespace Snappy.Integrations;

public sealed partial class MareIpc
{
    public List<ICharacter> GetPairedPlayers()
    {
        if (!_isUiOpen || !IsReady()) return new List<ICharacter>();

        var pairedAddresses = GetCurrentPairedAddresses();

        // Convert to ICharacter objects
        var result = pairedAddresses
            .Select(addr => Svc.Objects.FirstOrDefault(obj => obj.Address == addr))
            .OfType<ICharacter>()
            .Where(c => c.IsValid())
            .ToList();

        return result;
    }

    public bool IsHandledAddress(nint address)
    {
        var pairedAddresses = GetCurrentPairedAddresses();
        return pairedAddresses.Contains(address);
    }

    private HashSet<nint> GetCurrentPairedAddresses()
    {
        var pairedAddresses = new HashSet<nint>();

        RefreshPluginAvailability();

        foreach (var addr in GetCurrentLightlessAddresses())
            pairedAddresses.Add(addr);

        if (_snowcloakSyncHandledAddresses?.HasFunction == true)
            try
            {
                var addresses = _snowcloakSyncHandledAddresses.InvokeFunc();
                foreach (var addr in addresses) pairedAddresses.Add(addr);
            }
            catch (Exception ex)
            {
                PluginLog.Debug($"Failed to get SnowcloakSync handled addresses: {ex.Message}");
            }

        if (_marePlugins.TryGetValue(MareSempiternePluginKey, out var playerSyncPlugin) && playerSyncPlugin.IsAvailable)
        {
            try
            {
                foreach (var addr in GetPlayerSyncAddressesViaPairs(playerSyncPlugin))
                    pairedAddresses.Add(addr);
            }
            catch (Exception ex)
            {
                PluginLog.Debug($"Failed to reflect PlayerSync pair addresses: {ex.Message}");
            }
        }

        return pairedAddresses;
    }

    public bool IsAddressHandledByLightless(nint address)
    {
        return GetCurrentLightlessAddresses().Contains(address);
    }

    public bool IsAddressHandledBySnowcloak(nint address)
    {
        if (!IsPluginActive(SnowcloakPluginKey)) return false;

        try
        {
            // Prefer explicit Snowcloak label first
            if (_snowcloakSyncHandledAddresses?.HasFunction == true)
            {
                var addresses = _snowcloakSyncHandledAddresses.InvokeFunc();
                if (addresses.Contains(address)) return true;
            }

            // Do not read Mare label to avoid ambiguity
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"Failed Snowcloak address check: {ex.Message}");
        }

        return false;
    }

    public bool IsAddressHandledByPlayerSync(nint address)
    {
        if (!IsPluginActive(MareSempiternePluginKey)) return false;

        try
        {
            var pluginInfo = _marePlugins[MareSempiternePluginKey];
            var viaPairs = GetPlayerSyncAddressesViaPairs(pluginInfo);
            if (viaPairs.Contains(address)) return true;

            // Do not read Mare label to avoid ambiguity
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"Failed Player Sync address check: {ex.Message}");
        }

        return false;
    }

    private HashSet<nint> GetCurrentLightlessAddresses()
    {
        var handledAddresses = GetHandledAddressesFromIpc(_lightlessSyncHandledAddresses, "LightlessSync");
        var visibleAddresses = _marePlugins.TryGetValue(LightlessSyncPluginKey, out var lightlessPlugin)
            ? GetVisiblePairAddressesViaPairs(lightlessPlugin)
            : null;

        if (visibleAddresses != null)
        {
            if (handledAddresses != null)
                visibleAddresses.IntersectWith(handledAddresses);

            return visibleAddresses;
        }

        return handledAddresses ?? [];
    }

    private static HashSet<nint>? GetHandledAddressesFromIpc(ICallGateSubscriber<List<nint>>? subscriber, string pluginName)
    {
        if (subscriber?.HasFunction != true)
            return null;

        try
        {
            return subscriber.InvokeFunc().Where(addr => addr != nint.Zero).ToHashSet();
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"Failed to get {pluginName} handled addresses: {ex.Message}");
            return null;
        }
    }

    private HashSet<nint>? GetVisiblePairAddressesViaPairs(MarePluginInfo pluginInfo)
    {
        try
        {
            if (!pluginInfo.IsAvailable)
                return [];

            if (pluginInfo.Plugin == null || pluginInfo.PairManager == null)
                InitializeAllPlugins();

            if (pluginInfo.PairManager == null)
                return null;

            var results = new HashSet<nint>();
            foreach (var pair in EnumeratePairsFromPlugin(pluginInfo))
            {
                var pairType = pair.GetType();
                var isVisibleObj = pairType.GetProperty("IsVisible", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(pair);
                if (isVisibleObj is not true)
                    continue;

                var addrObj = pairType.GetProperty("PlayerCharacter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(pair);
                if (addrObj is nint addr && addr != nint.Zero)
                    results.Add(addr);
            }

            return results;
        }
        catch (Exception e)
        {
            PluginLog.Debug($"[Mare IPC] Visible pair reflection failed for {pluginInfo.PluginName}: {e.Message}");
            return null;
        }
    }

    private HashSet<nint> GetPlayerSyncAddressesViaPairs(MarePluginInfo pluginInfo)
    {
        var results = new HashSet<nint>();
        try
        {
            if (!pluginInfo.IsAvailable)
                return results;

            if (pluginInfo.Plugin == null || pluginInfo.PairManager == null)
                InitializeAllPlugins();

            if (pluginInfo.PairManager == null)
                return results;

            var pairManager = pluginInfo.PairManager;
            var pmType = pairManager.GetType();

            var getOnlinePairs = pmType.GetMethod("GetOnlineUserPairs", BindingFlags.Instance | BindingFlags.Public);
            if (getOnlinePairs == null)
                return results;

            if (getOnlinePairs.Invoke(pairManager, null) is not IEnumerable onlinePairs)
                return results;

            foreach (var pair in onlinePairs)
            {
                var pairType = pair.GetType();
                var hasCachedObj =
                    pairType.GetProperty("HasCachedPlayer", BindingFlags.Instance | BindingFlags.Public)?.GetValue(pair);
                var isVisibleObj =
                    pairType.GetProperty("IsVisible", BindingFlags.Instance | BindingFlags.Public)?.GetValue(pair);
                bool hasCached = hasCachedObj is bool b1 && b1;
                bool isVisible = isVisibleObj is bool b2 && b2;
                if (!hasCached) continue;

                var cachedPlayer =
                    pairType.GetProperty("CachedPlayer", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.GetValue(pair);
                if (cachedPlayer == null) continue;

                var addrObj = cachedPlayer.GetType()
                    .GetProperty("PlayerCharacter", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(cachedPlayer);
                if (addrObj is nint np && np != nint.Zero)
                {
                    if (isVisible)
                        results.Add(np);
                }
            }
        }
        catch (Exception e)
        {
            PluginLog.Debug($"[Mare IPC] PairManager reflection failed for {pluginInfo.PluginName}: {e.Message}");
        }

        return results;
    }
}
