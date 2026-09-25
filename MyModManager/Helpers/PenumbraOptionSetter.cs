using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using ECommons.DalamudServices;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using MyModManager.Models;

namespace MyModManager.Helpers;

public class PenumbraOptionSetter
{
    // IPC subscribers are constructed once and reused; each construction re-resolves
    // the Penumbra IPC gate, so caching avoids that cost on every toggle/poll.
    private readonly GetCollection _getCollection;
    private readonly GetCollections _getCollections;
    private readonly TrySetMod _trySetMod;
    private readonly TrySetModSetting _trySetModSetting;
    private readonly TrySetModSettings _trySetModSettings;
    private readonly GetCurrentModSettings _getCurrentModSettings;
    private readonly GetAvailableModSettings _getAvailableModSettings;
    private readonly GetAllModSettings _getAllModSettings;
    private readonly GetModList _getModList;
    private readonly GetChangedItemAdapterList _getChangedItemAdapterList;
    private readonly GetModPath _getModPath;
    private readonly RedrawObject _redrawObject;

    private DateTime _lastModStateRefresh = DateTime.MinValue;
    private Dictionary<string, (bool Enabled, Dictionary<string, List<string>> Settings)> _modStates = new();

    public PenumbraOptionSetter(IDalamudPluginInterface pi)
    {
        _getCollection = new GetCollection(pi);
        _getCollections = new GetCollections(pi);
        _trySetMod = new TrySetMod(pi);
        _trySetModSetting = new TrySetModSetting(pi);
        _trySetModSettings = new TrySetModSettings(pi);
        _getCurrentModSettings = new GetCurrentModSettings(pi);
        _getAvailableModSettings = new GetAvailableModSettings(pi);
        _getAllModSettings = new GetAllModSettings(pi);
        _getModList = new GetModList(pi);
        _getChangedItemAdapterList = new GetChangedItemAdapterList(pi);
        _getModPath = new GetModPath(pi);
        _redrawObject = new RedrawObject(pi);
    }

    public IReadOnlyDictionary<string, (bool Enabled, Dictionary<string, List<string>> Settings)> ModStates => _modStates;

    /// <summary>Forces the next <see cref="RefreshModStatesIfDue"/> call to re-poll Penumbra.</summary>
    public void ForceModStateRefresh() => _lastModStateRefresh = DateTime.MinValue;

    public Guid ResolveCollectionId(Guid collectionId)
    {
        if (collectionId != Guid.Empty) return collectionId;
        try
        {
            (collectionId, _) = _getCollection.Invoke(ApiCollectionType.Current) ?? (Guid.Empty, string.Empty);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to resolve current Penumbra collection.");
            return Guid.Empty;
        }

        return collectionId;
    }

    public void RefreshModStatesIfDue(Guid targetCollectionId)
    {
        if ((DateTime.Now - _lastModStateRefresh).TotalSeconds < 2) return;
        // Arm the throttle before attempting, so failures also wait out the interval
        // instead of retrying (and logging) every frame while Penumbra is unavailable.
        _lastModStateRefresh = DateTime.Now;

        try
        {
            var collectionId = ResolveCollectionId(targetCollectionId);
            if (collectionId == Guid.Empty) return;

            var (ec, settings) = _getAllModSettings.Invoke(collectionId, false, false, 0);
            if (ec == PenumbraApiEc.Success && settings != null)
            {
                _modStates = settings.ToDictionary(k => k.Key, v => (v.Value.Item1, v.Value.Item3));
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to refresh Penumbra mod states.");
        }
    }

    public Dictionary<Guid, string>? GetCollections()
    {
        try
        {
            return _getCollections.Invoke();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to refresh Penumbra collections.");
            return null;
        }
    }

    public Dictionary<string, string>? GetModList()
    {
        try
        {
            var mods = _getModList.Invoke();
            return mods?.ToDictionary(k => k.Key, v => v.Value);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to refresh Penumbra mod list.");
            return null;
        }
    }

    /// <summary>
    /// Snapshot of each mod's Changed Items. Copied off Penumbra's adapter so callers
    /// do not keep a live view that throws after a Penumbra reload.
    /// </summary>
    public List<(string ModDirectory, Dictionary<string, object?> ChangedItems)>? GetChangedItemsSnapshot()
    {
        try
        {
            var adapter = _getChangedItemAdapterList.Invoke();
            if (adapter == null)
                return null;

            var copy = new List<(string ModDirectory, Dictionary<string, object?> ChangedItems)>(adapter.Count);
            foreach (var entry in adapter)
            {
                var items = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (entry.ChangedItems != null)
                {
                    foreach (var kv in entry.ChangedItems)
                        items[kv.Key] = kv.Value;
                }

                copy.Add((entry.ModDirectory, items));
            }

            return copy;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to read Penumbra changed items.");
            return null;
        }
    }

    public string? GetModFilesystemPath(string modDirectory)
    {
        try
        {
            var (ec, fullPath, _, _) = _getModPath.Invoke(modDirectory);
            return ec == PenumbraApiEc.Success ? fullPath : null;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Failed to read Penumbra path for {modDirectory}");
            return null;
        }
    }

    public Dictionary<string, (string[] Options, GroupType Type)>? GetAvailableSettings(string modDirectory)
    {
        try
        {
            var available = _getAvailableModSettings.Invoke(modDirectory, modDirectory);
            return available?.ToDictionary(k => k.Key, v => (v.Value.Item1, v.Value.Item2));
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Failed to read available settings for {modDirectory}");
            return null;
        }
    }

    /// <summary>
    /// Reads the live enabled state of a managed mod (or its specific option) from Penumbra.
    /// Returns null when Penumbra or the mod is unavailable so callers can fall back to stored state.
    /// </summary>
    public bool? GetManagedModState(ManagedMod mod, Guid collectionId = default)
    {
        try
        {
            collectionId = ResolveCollectionId(collectionId);
            if (collectionId == Guid.Empty) return null;

            var current = _getCurrentModSettings.Invoke(collectionId, mod.ModName, mod.ModName, false);
            if (current.Item1 != PenumbraApiEc.Success || current.Item2 == null) return null;

            if (!string.IsNullOrEmpty(mod.OptionName))
            {
                return current.Item2.Value.Item3.TryGetValue(mod.GroupName, out var list) && list.Contains(mod.OptionName);
            }

            return current.Item2.Value.Item1;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Failed to read state for {mod.ModName}");
            return null;
        }
    }

    /// <summary>Redraws the local player. Safe to call when Penumbra is unavailable.</summary>
    public void RedrawPlayer()
    {
        try
        {
            _redrawObject.Invoke(0, RedrawType.Redraw);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to redraw player.");
        }
    }

    public bool SetManagedModState(ManagedMod mod, bool enable, Guid collectionId = default)
    {
        try
        {
            collectionId = ResolveCollectionId(collectionId);
            if (collectionId == Guid.Empty) return false;

            if (!string.IsNullOrEmpty(mod.OptionName))
            {
                if (enable)
                {
                    // Ensure the base mod is enabled when we enable a specific option
                    _trySetMod.Invoke(collectionId, mod.ModName, true, mod.ModName);
                }

                if (mod.GroupType == GroupType.Multi)
                {
                    var current = _getCurrentModSettings.Invoke(collectionId, mod.ModName, mod.ModName, false);
                    if (current.Item1 != PenumbraApiEc.Success || current.Item2 == null) return false;

                    var enabledList = CopyOptionList(current.Item2.Value.Item3, mod.GroupName);
                    if (enable)
                    {
                        if (!enabledList.Contains(mod.OptionName)) enabledList.Add(mod.OptionName);
                    }
                    else
                    {
                        enabledList.Remove(mod.OptionName);
                    }
                    return _trySetModSettings.Invoke(collectionId, mod.ModName, mod.GroupName, enabledList, mod.ModName) == PenumbraApiEc.Success;
                }

                if (enable)
                {
                    return _trySetModSetting.Invoke(collectionId, mod.ModName, mod.GroupName, mod.OptionName, mod.ModName) == PenumbraApiEc.Success;
                }

                // Single-select options cannot be disabled; the caller must enable a different option.
                return false;
            }

            return _trySetMod.Invoke(collectionId, mod.ModName, enable, mod.ModName) == PenumbraApiEc.Success;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Failed to set mod state for {mod.DisplayName}");
            return false;
        }
    }

    private static List<string> CopyOptionList(IReadOnlyDictionary<string, List<string>> settings, string groupName)
    {
        return settings.TryGetValue(groupName, out var list) && list != null
            ? new List<string>(list)
            : new List<string>();
    }
}
