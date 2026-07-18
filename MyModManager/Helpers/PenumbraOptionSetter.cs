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
    private readonly IDalamudPluginInterface _pi;

    public PenumbraOptionSetter(IDalamudPluginInterface pi)
    {
        _pi = pi;
    }

    public bool SetManagedModState(ManagedMod mod, bool enable, Guid collectionId = default)
    {
        if (collectionId == Guid.Empty)
        {
            (collectionId, _) = new GetCollection(_pi).Invoke(ApiCollectionType.Current) ?? (Guid.Empty, string.Empty);
        }

        if (collectionId == Guid.Empty) return false;

        try
        {
            if (!string.IsNullOrEmpty(mod.OptionName))
            {
                if (enable)
                {
                    // Ensure the base mod is enabled when we enable a specific option
                    new TrySetMod(_pi).Invoke(collectionId, mod.ModName, true, mod.ModName);
                }

                if (mod.GroupType == GroupType.Multi)
                {
                    var current = new GetCurrentModSettings(_pi).Invoke(collectionId, mod.ModName, mod.ModName, false);
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
                    return new TrySetModSettings(_pi).Invoke(collectionId, mod.ModName, mod.GroupName, enabledList, mod.ModName) == PenumbraApiEc.Success;
                }
                else
                {
                    if (enable)
                    {
                        return new TrySetModSetting(_pi).Invoke(collectionId, mod.ModName, mod.GroupName, mod.OptionName, mod.ModName) == PenumbraApiEc.Success;
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
                return new TrySetMod(_pi).Invoke(collectionId, mod.ModName, enable, mod.ModName) == PenumbraApiEc.Success;
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
        Guid id = Guid.Empty;

        if (string.IsNullOrEmpty(collectionName))
        {
            (id, _) = new GetCollection(_pi).Invoke(ApiCollectionType.Current) ?? (Guid.Empty, string.Empty);
        }
        else
        {
            var collections = new GetCollections(_pi).Invoke();
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

        var available = new GetAvailableModSettings(_pi).Invoke(modName, modName);
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
                new RedrawObject(_pi).Invoke(0, RedrawType.Redraw);
                return true;
            }
        }

        return false;
    }

    private bool EnableOption(Guid collectionId, string modName, string groupName, string optionName, GroupType type)
    {
        // Ensure base mod is enabled
        new TrySetMod(_pi).Invoke(collectionId, modName, true, modName);

        if (type == GroupType.Single)
        {
            return new TrySetModSetting(_pi).Invoke(collectionId, modName, groupName, optionName, modName) == PenumbraApiEc.Success;
        }
        else
        {
            var current = new GetCurrentModSettings(_pi).Invoke(collectionId, modName, modName, false);
            if (current.Item1 != PenumbraApiEc.Success || current.Item2 == null) return false;

            var enabledList = current.Item2.Value.Item3.TryGetValue(groupName, out var list) ? list : new List<string>();
            if (!enabledList.Contains(optionName))
            {
                enabledList.Add(optionName);
                return new TrySetModSettings(_pi).Invoke(collectionId, modName, groupName, enabledList, modName) == PenumbraApiEc.Success;
            }
            return true;
        }
    }

    private bool DisableOption(Guid collectionId, string modName, string groupName, string optionName, GroupType type)
    {
        if (type == GroupType.Multi)
        {
            var current = new GetCurrentModSettings(_pi).Invoke(collectionId, modName, modName, false);
            if (current.Item1 != PenumbraApiEc.Success || current.Item2 == null) return false;

            var enabledList = current.Item2.Value.Item3.TryGetValue(groupName, out var list) ? list : new List<string>();
            if (enabledList.Remove(optionName))
            {
                return new TrySetModSettings(_pi).Invoke(collectionId, modName, groupName, enabledList, modName) == PenumbraApiEc.Success;
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
