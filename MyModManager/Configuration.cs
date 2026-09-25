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

        [NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        /// <summary>Bumped on every Save so UI grouping caches can rebuild only when config changes.</summary>
        [NonSerialized]
        public int Revision;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;
            Normalize();
        }

        public void Normalize()
        {
            ManagedMods ??= new List<ManagedMod>();
            KnownTags ??= new List<string>();

            foreach (var m in ManagedMods)
            {
                if (string.IsNullOrEmpty(m.Id)) m.Id = Guid.NewGuid().ToString();
                m.ModName ??= string.Empty;
                m.DisplayName ??= string.Empty;
                m.ShortcutName ??= string.Empty;
                m.AnimationCommand ??= string.Empty;
                m.GroupName ??= string.Empty;
                m.OptionName ??= string.Empty;
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

        public void Save()
        {
            RebuildAssignedTags();
            Revision++;
            pluginInterface?.SavePluginConfig(this);
        }
    }
}
