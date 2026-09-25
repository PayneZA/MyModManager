using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace MyModManager.Services;

/// <summary>Live settings of one mod in the active collection.</summary>
public sealed record ModState(bool Enabled, int Priority, IReadOnlyDictionary<string, List<string>> Settings);

public sealed record OptionGroup(string Name, string[] Options, GroupType Type);

/// <summary>
/// Thin, event-driven layer over Penumbra's IPC. It tracks only the mods My Mod Manager
/// manages, refreshes a mod when Penumbra reports a change to it, and resolves the
/// collection that actually applies to the player's character.
/// </summary>
public sealed class PenumbraService : IDisposable
{
    private const int RequiredBreakingVersion = 5;

    private readonly Configuration config;

    private readonly ApiVersion apiVersion;
    private readonly GetCollection getCollection;
    private readonly GetCollections getCollections;
    private readonly GetCollectionForObject getCollectionForObject;
    private readonly TrySetMod trySetMod;
    private readonly TrySetModSetting trySetModSetting;
    private readonly TrySetModSettings trySetModSettings;
    private readonly GetCurrentModSettings getCurrentModSettings;
    private readonly GetAvailableModSettings getAvailableModSettings;
    private readonly GetModList getModList;
    private readonly GetChangedItemAdapterList getChangedItemAdapterList;
    private readonly GetChangedItems getChangedItems;
    private readonly GetModPath getModPath;
    private readonly GetModDirectory getModDirectory;
    private readonly RedrawObject redrawObject;
    private readonly OpenMainWindow openMainWindow;

    private readonly IDisposable initializedEvent;
    private readonly IDisposable disposedEvent;
    private readonly IDisposable settingChangedEvent;
    private readonly IDisposable redrawnEvent;
    private readonly IDisposable modAddedEvent;
    private readonly IDisposable modDeletedEvent;
    private readonly IDisposable modMovedEvent;

    private readonly Dictionary<string, ModState?> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<OptionGroup>?> optionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> dirtyMods = new();
    private readonly ConcurrentQueue<(string From, string To)> movedMods = new();
    private HashSet<string> tracked = new(StringComparer.OrdinalIgnoreCase);
    private int trackedRevision = -1;

    private volatile bool allDirty = true;
    private volatile bool modStructureChanged = true;
    private volatile bool playerRedrawn;
    private volatile bool availabilityChanged = true;

    private DateTime nextCollectionCheck = DateTime.MinValue;
    private DateTime nextSafetyRefresh = DateTime.MinValue;
    private DateTime nextCollectionListRefresh = DateTime.MinValue;

    private Dictionary<string, string> modList = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<Guid, string> collections = new();

    public PenumbraService(IDalamudPluginInterface pi, Configuration config)
    {
        this.config = config;

        apiVersion = new ApiVersion(pi);
        getCollection = new GetCollection(pi);
        getCollections = new GetCollections(pi);
        getCollectionForObject = new GetCollectionForObject(pi);
        trySetMod = new TrySetMod(pi);
        trySetModSetting = new TrySetModSetting(pi);
        trySetModSettings = new TrySetModSettings(pi);
        getCurrentModSettings = new GetCurrentModSettings(pi);
        getAvailableModSettings = new GetAvailableModSettings(pi);
        getModList = new GetModList(pi);
        getChangedItemAdapterList = new GetChangedItemAdapterList(pi);
        getChangedItems = new GetChangedItems(pi);
        getModPath = new GetModPath(pi);
        getModDirectory = new GetModDirectory(pi);
        redrawObject = new RedrawObject(pi);
        openMainWindow = new OpenMainWindow(pi);

        initializedEvent = Initialized.Subscriber(pi, OnInitialized);
        disposedEvent = Disposed.Subscriber(pi, OnDisposed);
        settingChangedEvent = ModSettingChanged.Subscriber(pi, OnSettingChanged);
        redrawnEvent = GameObjectRedrawn.Subscriber(pi, OnRedrawn);
        modAddedEvent = ModAdded.Subscriber(pi, OnModStructureChanged);
        modDeletedEvent = ModDeleted.Subscriber(pi, OnModStructureChanged);
        modMovedEvent = ModMoved.Subscriber(pi, OnModMoved);

        CheckAvailability();
    }

    public void Dispose()
    {
        initializedEvent.Dispose();
        disposedEvent.Dispose();
        settingChangedEvent.Dispose();
        redrawnEvent.Dispose();
        modAddedEvent.Dispose();
        modDeletedEvent.Dispose();
        modMovedEvent.Dispose();
    }

    /// <summary>True when a compatible Penumbra is loaded and answering IPC.</summary>
    public bool Available { get; private set; }

    public Guid CollectionId { get; private set; }
    public string CollectionName { get; private set; } = string.Empty;

    /// <summary>True when the collection follows the player's own assignment rather than a fixed pick.</summary>
    public bool FollowsPlayer => config.TargetCollectionId == Guid.Empty || TargetCollectionMissing;

    /// <summary>True when the configured collection no longer exists and the player's is used instead.</summary>
    public bool TargetCollectionMissing { get; private set; }

    /// <summary>Bumps whenever cached mod state, the mod list or the collection changes.</summary>
    public int Version { get; private set; }

    /// <summary>Raised on the framework thread after Penumbra finishes redrawing the local player.</summary>
    public event Action? PlayerRedrawn;

    public IReadOnlyDictionary<string, string> ModList => modList;
    public IReadOnlyDictionary<Guid, string> Collections => collections;

    // ---------------------------------------------------------------- events (any thread)

    private void OnInitialized()
    {
        availabilityChanged = true;
        allDirty = true;
        modStructureChanged = true;
    }

    private void OnDisposed() => availabilityChanged = true;

    private void OnSettingChanged(ModSettingChange change, Guid collectionId, string modDirectory, bool inherited)
    {
        if (change == ModSettingChange.Edited)
            modStructureChanged = true;
        dirtyMods.Enqueue(modDirectory);
    }

    private void OnRedrawn(nint address, int objectIndex)
    {
        if (objectIndex == 0)
            playerRedrawn = true;
    }

    private void OnModMoved(string from, string to)
    {
        movedMods.Enqueue((from, to));
        OnModStructureChanged(to);
    }

    private void OnModStructureChanged(string modDirectory)
    {
        modStructureChanged = true;
        dirtyMods.Enqueue(modDirectory);
    }

    // ---------------------------------------------------------------- per-frame update

    /// <summary>Applies queued Penumbra changes. Call once per framework tick; cheap when idle.</summary>
    public void Update()
    {
        if (availabilityChanged)
        {
            availabilityChanged = false;
            CheckAvailability();
        }

        if (playerRedrawn)
        {
            playerRedrawn = false;
            PlayerRedrawn?.Invoke();
        }

        if (!Available)
            return;

        FollowMovedMods();

        var now = DateTime.UtcNow;
        if (trackedRevision != config.Revision)
        {
            trackedRevision = config.Revision;
            var next = config.ManagedMods.Select(m => m.ModName).Where(d => d.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in next.Where(d => !tracked.Contains(d)))
                dirtyMods.Enqueue(dir);
            tracked = next;
        }

        if (now >= nextCollectionCheck)
        {
            nextCollectionCheck = now.AddSeconds(2);
            ResolveCollection();
        }

        // Safety net for changes Penumbra has no event for (e.g. collection inheritance edits).
        if (now >= nextSafetyRefresh)
        {
            nextSafetyRefresh = now.AddSeconds(30);
            allDirty = true;
        }

        if (modStructureChanged)
        {
            modStructureChanged = false;
            optionCache.Clear();
            RefreshModList();
        }

        var changed = false;
        if (allDirty)
        {
            allDirty = false;
            dirtyMods.Clear();
            foreach (var dir in tracked)
                changed |= RefreshState(dir);
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (dirtyMods.TryDequeue(out var dir))
            {
                if (tracked.Contains(dir) && seen.Add(dir))
                    changed |= RefreshState(dir);
            }
        }

        if (changed)
            Version++;
    }

    /// <summary>Renaming a mod's folder in Penumbra changes its directory; keep entries pointing at it.</summary>
    private void FollowMovedMods()
    {
        var changed = false;
        while (movedMods.TryDequeue(out var move))
        {
            foreach (var entry in config.ManagedMods)
            {
                if (!entry.ModName.Equals(move.From, StringComparison.OrdinalIgnoreCase))
                    continue;
                entry.ModName = move.To;
                changed = true;
            }
        }

        if (changed)
            config.Save();
    }

    /// <summary>Marks every tracked mod for re-reading on the next update.</summary>
    public void Invalidate() => allDirty = true;

    private void CheckAvailability()
    {
        var wasAvailable = Available;
        try
        {
            var (breaking, _) = apiVersion.Invoke();
            Available = breaking == RequiredBreakingVersion;
            if (!Available)
                Svc.Log.Warning($"Penumbra API {breaking} is not supported (need {RequiredBreakingVersion}).");
        }
        catch
        {
            Available = false;
        }

        if (!Available)
        {
            states.Clear();
            optionCache.Clear();
        }
        else if (!wasAvailable)
        {
            allDirty = true;
            modStructureChanged = true;
            nextCollectionCheck = DateTime.MinValue;
        }

        if (wasAvailable != Available)
            Version++;
    }

    private void ResolveCollection()
    {
        var previous = CollectionId;
        try
        {
            if (DateTime.UtcNow >= nextCollectionListRefresh)
            {
                nextCollectionListRefresh = DateTime.UtcNow.AddSeconds(10);
                collections = getCollections.Invoke() ?? new Dictionary<Guid, string>();
            }

            var target = config.TargetCollectionId;
            TargetCollectionMissing = target != Guid.Empty && collections.Count > 0 && !collections.ContainsKey(target);

            if (target != Guid.Empty && !TargetCollectionMissing)
            {
                CollectionId = target;
                CollectionName = collections.GetValueOrDefault(target, "Selected collection");
            }
            else
            {
                (CollectionId, CollectionName) = ResolvePlayerCollection();
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to resolve the Penumbra collection.");
        }

        if (previous != CollectionId)
        {
            allDirty = true;
            Version++;
        }
    }

    private (Guid, string) ResolvePlayerCollection()
    {
        var (objectValid, _, effective) = getCollectionForObject.Invoke(0);
        if (objectValid && effective.Id != Guid.Empty)
            return (effective.Id, effective.Name);

        // Not logged in yet: use the "Your Character" assignment, then the base collection.
        var yourself = getCollection.Invoke(ApiCollectionType.Yourself);
        if (yourself is { } y)
            return (y.Id, y.Name);

        var fallback = getCollection.Invoke(ApiCollectionType.Default);
        return fallback is { } d ? (d.Id, d.Name) : (Guid.Empty, string.Empty);
    }

    private void RefreshModList()
    {
        try
        {
            modList = new Dictionary<string, string>(getModList.Invoke(), StringComparer.OrdinalIgnoreCase);
            Version++;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to read the Penumbra mod list.");
        }
    }

    private bool RefreshState(string modDirectory)
    {
        if (CollectionId == Guid.Empty)
            return false;

        try
        {
            var (ec, settings) = getCurrentModSettings.Invoke(CollectionId, modDirectory, string.Empty, false);
            ModState? next = ec == PenumbraApiEc.Success && settings is { } s
                ? new ModState(s.Item1, s.Item2, s.Item3)
                : null;

            // ModMissing means uninstalled; any other failure keeps the last known state.
            if (next == null && ec != PenumbraApiEc.ModMissing)
                return false;

            var had = states.TryGetValue(modDirectory, out var old);
            states[modDirectory] = next;
            return !had || !SameState(old, next);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Failed to read settings for {modDirectory}.");
            return false;
        }
    }

    private static bool SameState(ModState? a, ModState? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a.Enabled != b.Enabled || a.Priority != b.Priority || a.Settings.Count != b.Settings.Count)
            return false;
        foreach (var (group, options) in a.Settings)
        {
            if (!b.Settings.TryGetValue(group, out var other) || !options.SequenceEqual(other))
                return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- queries

    /// <summary>
    /// Live state of a mod. Returns false when state is unknown (not yet read or Penumbra unavailable).
    /// A known-but-null state means the mod is not installed.
    /// </summary>
    public bool TryGetState(string modDirectory, out ModState? state)
    {
        state = null;
        return Available && states.TryGetValue(modDirectory, out state);
    }

    public bool IsInstalled(string modDirectory) => modList.Count == 0 || modList.ContainsKey(modDirectory);

    public IReadOnlyList<OptionGroup>? GetOptionGroups(string modDirectory)
    {
        if (optionCache.TryGetValue(modDirectory, out var cached))
            return cached;

        IReadOnlyList<OptionGroup>? groups = null;
        try
        {
            groups = getAvailableModSettings.Invoke(modDirectory, string.Empty)?
                .Select(kv => new OptionGroup(kv.Key, kv.Value.Item1, kv.Value.Item2))
                .ToList();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Failed to read options for {modDirectory}.");
        }

        if (Available)
            optionCache[modDirectory] = groups;
        return groups;
    }

    public OptionGroup? GetOptionGroup(string modDirectory, string groupName) =>
        GetOptionGroups(modDirectory)?.FirstOrDefault(g => g.Name == groupName);

    /// <summary>Snapshot of every mod's Changed Items, copied so it stays valid after a Penumbra reload.</summary>
    public List<(string ModDirectory, Dictionary<string, object?> ChangedItems)>? GetChangedItemsSnapshot()
    {
        try
        {
            var adapter = getChangedItemAdapterList.Invoke();
            var copy = new List<(string, Dictionary<string, object?>)>(adapter.Count);
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

    /// <summary>The mod's folder path inside Penumbra's mod selector, e.g. "Animations/NSFW/Nightlife".</summary>
    public string? GetSelectorPath(string modDirectory)
    {
        try
        {
            var (ec, fullPath, _, _) = getModPath.Invoke(modDirectory, string.Empty);
            return ec == PenumbraApiEc.Success ? fullPath : null;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Failed to read the Penumbra path for {modDirectory}.");
            return null;
        }
    }

    /// <summary>Names of the items one mod changes, e.g. "Emote: Sit on Ground".</summary>
    public IReadOnlyCollection<string> GetChangedItemNames(string modDirectory)
    {
        try
        {
            return getChangedItems.Invoke(modDirectory, string.Empty)?.Keys.ToList() ?? new List<string>();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Failed to read changed items for {modDirectory}.");
            return new List<string>();
        }
    }

    /// <summary>The mod's folder on disk, or null when Penumbra doesn't say where mods live.</summary>
    public string? GetModFolder(string modDirectory)
    {
        var root = GetModRoot();
        return string.IsNullOrEmpty(root) ? null : System.IO.Path.Combine(root, modDirectory);
    }

    /// <summary>Penumbra's root mod folder on disk.</summary>
    public string? GetModRoot()
    {
        try
        {
            return getModDirectory.Invoke();
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- mutations

    public bool SetModEnabled(string modDirectory, bool enabled) =>
        Call(modDirectory, () => trySetMod.Invoke(CollectionId, modDirectory, enabled, string.Empty));

    public bool SetSingleOption(string modDirectory, string group, string option) =>
        Call(modDirectory, () => trySetModSetting.Invoke(CollectionId, modDirectory, group, option, string.Empty));

    public bool SetMultiOptions(string modDirectory, string group, IReadOnlyList<string> options) =>
        Call(modDirectory, () => trySetModSettings.Invoke(CollectionId, modDirectory, group, options, string.Empty));

    private bool Call(string modDirectory, Func<PenumbraApiEc> action)
    {
        if (!Available || CollectionId == Guid.Empty)
            return false;

        try
        {
            var ec = action();
            if (ec is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)
            {
                dirtyMods.Enqueue(modDirectory);
                return true;
            }

            Svc.Log.Warning($"Penumbra refused a change to {modDirectory}: {ec}.");
            return false;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Failed to change settings for {modDirectory}.");
            return false;
        }
    }

    /// <summary>Redraws the local player so changed files load. Completion raises <see cref="PlayerRedrawn"/>.</summary>
    public void RedrawPlayer()
    {
        try
        {
            redrawObject.Invoke(0, RedrawType.Redraw);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to redraw the player.");
        }
    }

    /// <summary>Opens Penumbra's Mods tab with the given mod selected.</summary>
    public void OpenInPenumbra(string modDirectory)
    {
        try
        {
            openMainWindow.Invoke(TabType.Mods, modDirectory, string.Empty);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to open Penumbra.");
        }
    }
}
