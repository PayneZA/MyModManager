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
    // the Penumbra IPC gate, so caching avoids that cost on every toggle.
    private readonly GetCollection _getCollection;
    private readonly GetCollections _getCollections;
    private readonly TrySetMod _trySetMod;
    private readonly TrySetModSetting _trySetModSetting;
    private readonly TrySetModSettings _trySetModSettings;
    private readonly GetCurrentModSettings _getCurrentModSettings;
    private readonly GetAvailableModSettings _getAvailableModSettings;
    private readonly RedrawObject _redrawObject;

    public PenumbraOptionSetter(IDalamudPluginInterface pi)
    {
        _getCollection = new GetCollection(pi);
        _getCollections = new GetCollections(pi);
        _trySetMod = new TrySetMod(pi);
        _trySetModSetting = new TrySetModSetting(pi);
        _trySetModSettings = new TrySetModSettings(pi);
        _getCurrentModSettings = new GetCurrentModSettings(pi);
        _getAvailableModSettings = new GetAvailableModSettings(pi);
        _redrawObject = new RedrawObject(pi);
    }

    /// <summary>
    /// Reads the live enabled state of a managed mod (or its specific option) from Penumbra.
    /// Returns null when Penumbra or the mod is unavailable so callers can fall back to stored state.
    /// </summary>
    public bool? GetManagedModState(ManagedMod mod, Guid collectionId = default)
    {
        try
        {
            if (collectionId == Guid.Empty)
            {
                (collectionId, _) = _getCollection.Invoke(ApiCollectionType.Current) ?? (Guid.Empty, string.Empty);
            }

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
            if (collectionId == Guid.Empty)
            {
                (collectionId, _) = _getCollection.Invoke(ApiCollectionType.Current) ?? (Guid.Empty, string.Empty);
            }

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

                    var enabledList = current.Item2.Value.Item3.TryGetValue(mod.GroupName, out var list) ? list : new List<string>();
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
                else
                {
                    if (enable)
                    {
                        return _trySetModSetting.Invoke(collectionId, mod.ModName, mod.GroupName, mod.OptionName, mod.ModName) == PenumbraApiEc.Success;
                    }
                    else
                    {
                        // Single select options cannot be "disabled".
                        // We return true here to allow the UI to untick, without dropping the mod's settings.
                        return true;
                    }
                }
            }
            else
            {
                return _trySetMod.Invoke(collectionId, mod.ModName, enable, mod.ModName) == PenumbraApiEc.Success;
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Failed to set mod state for {mod.DisplayName}");
            return false;
        }
    }

    /// <summary>
    /// Searches all groups in a mod and enables/disables the first option that matches the search term.
    /// </summary>
    public bool SetOptionByName(string modName, string searchTerm, bool enable, string? collectionName = null)
    {
        try
        {
            Guid id = Guid.Empty;

            if (string.IsNullOrEmpty(collectionName))
            {
                (id, _) = _getCollection.Invoke(ApiCollectionType.Current) ?? (Guid.Empty, string.Empty);
            }
            else
            {
                var collections = _getCollections.Invoke();
                foreach (var collection in collections)
                {
                    if (collection.Value.Equals(collectionName, StringComparison.OrdinalIgnoreCase))
                    {
                        id = collection.Key;
                        break;
                    }
                }
            }

            if (id == Guid.Empty) return false;

            var available = _getAvailableModSettings.Invoke(modName, modName);
            if (available == null) return false;

            foreach (var group in available)
            {
                var groupName = group.Key;
                var (options, groupType) = group.Value;

                var matchingOption = options.FirstOrDefault(o => o.StartsWith(searchTerm, StringComparison.OrdinalIgnoreCase));
                if (matchingOption == null) continue;

                bool result = enable
                    ? EnableOption(id, modName, groupName, matchingOption, groupType)
                    : DisableOption(id, modName, groupName, matchingOption, groupType);

                if (result)
                {
                    _redrawObject.Invoke(0, RedrawType.Redraw);
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Failed to set option '{searchTerm}' for {modName}");
            return false;
        }
    }

    private bool EnableOption(Guid collectionId, string modName, string groupName, string optionName, GroupType type)
    {
        // Ensure base mod is enabled
        _trySetMod.Invoke(collectionId, modName, true, modName);

        if (type == GroupType.Single)
        {
            return _trySetModSetting.Invoke(collectionId, modName, groupName, optionName, modName) == PenumbraApiEc.Success;
        }
        else
        {
            var current = _getCurrentModSettings.Invoke(collectionId, modName, modName, false);
            if (current.Item1 != PenumbraApiEc.Success || current.Item2 == null) return false;

            var enabledList = current.Item2.Value.Item3.TryGetValue(groupName, out var list) ? list : new List<string>();
            if (!enabledList.Contains(optionName))
            {
                enabledList.Add(optionName);
                return _trySetModSettings.Invoke(collectionId, modName, groupName, enabledList, modName) == PenumbraApiEc.Success;
            }
            return true;
        }
    }

    private bool DisableOption(Guid collectionId, string modName, string groupName, string optionName, GroupType type)
    {
        if (type == GroupType.Multi)
        {
            var current = _getCurrentModSettings.Invoke(collectionId, modName, modName, false);
            if (current.Item1 != PenumbraApiEc.Success || current.Item2 == null) return false;

            var enabledList = current.Item2.Value.Item3.TryGetValue(groupName, out var list) ? list : new List<string>();
            if (enabledList.Remove(optionName))
            {
                return _trySetModSettings.Invoke(collectionId, modName, groupName, enabledList, modName) == PenumbraApiEc.Success;
            }
            return true;
        }
        else
        {
            // Single select options cannot be "disabled".
            return true;
        }
    }
}
