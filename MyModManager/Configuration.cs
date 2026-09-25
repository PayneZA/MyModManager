using Dalamud.Configuration;
using Dalamud.Plugin;
using MyModManager.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MyModManager
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 1;

        public List<ManagedMod> ManagedMods { get; set; } = new();
        public Guid TargetCollectionId { get; set; } = Guid.Empty;
        public List<string> KnownTags { get; set; } = new();

        /// <summary>Redraw the character after changes so new files load.</summary>
        public bool RedrawAfterChange { get; set; } = true;

        /// <summary>Play emotes with "motion" so no emote text is posted to the log.</summary>
        public bool SilentEmotes { get; set; } = false;

        /// <summary>Select an entry's pose before playing it.</summary>
        public bool AutoPose { get; set; } = true;

        /// <summary>Playing an entry pauses other entries that replace the same emote and pose.</summary>
        public bool OneAnimationPerEmote { get; set; } = true;

        /// <summary>Kept-on entries paused by a temporary one; Turn off temporary restores them.</summary>
        public List<string> SuspendedIds { get; set; } = new();

        [NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        /// <summary>Bumped on every Save so UI grouping caches can rebuild only when config changes.</summary>
        [NonSerialized]
        public int Revision;

        [NonSerialized]
        private DateTime? saveDueAt;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;
            Normalize();
        }

        public void Normalize()
        {
            ManagedMods ??= new List<ManagedMod>();
            KnownTags ??= new List<string>();
            SuspendedIds ??= new List<string>();

            foreach (var m in ManagedMods)
            {
                if (string.IsNullOrEmpty(m.Id)) m.Id = Guid.NewGuid().ToString();
                m.ModName ??= string.Empty;
                m.DisplayName ??= string.Empty;
                m.ShortcutName ??= string.Empty;
                m.AnimationCommand ??= string.Empty;
                m.GroupName ??= string.Empty;
                m.OptionName ??= string.Empty;
                m.OffOption ??= string.Empty;
                if (m.Pose < 0) m.Pose = 0;
                if (string.IsNullOrEmpty(m.CategoryName)) m.CategoryName = "Default";
                m.Tags ??= new List<string>();
                m.Tags = m.Tags
                    .Select(t => t?.Trim() ?? string.Empty)
                    .Where(t => t.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            RebuildAssignedTags();
        }

        /// <summary>KnownTags is only names still used on at least one managed entry.</summary>
        public void RebuildAssignedTags()
        {
            KnownTags = ManagedMods
                .SelectMany(m => m.Tags ?? Enumerable.Empty<string>())
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void RememberTags(IEnumerable<string> tags)
        {
            foreach (var tag in tags)
            {
                var trimmed = tag.Trim();
                if (trimmed.Length == 0) continue;
                if (!KnownTags.Any(k => k.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
                    KnownTags.Add(trimmed);
            }
        }

        /// <summary>
        /// Records a change. The file write is deferred briefly so bursts of edits (e.g. starring
        /// several entries) produce one write; <see cref="Flush"/> runs on unload.
        /// </summary>
        public void Save()
        {
            RebuildAssignedTags();
            Revision++;
            saveDueAt ??= DateTime.UtcNow.AddMilliseconds(750);
        }

        public void FlushIfDue()
        {
            if (saveDueAt is { } due && DateTime.UtcNow >= due)
                Flush();
        }

        public void Flush()
        {
            if (saveDueAt == null) return;
            saveDueAt = null;
            pluginInterface?.SavePluginConfig(this);
        }
    }
}
