using Dalamud.Configuration;
using Dalamud.Plugin;
using MyModManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MyModManager
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public const int CurrentVersion = 2;

        public int Version { get; set; } = CurrentVersion;

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

        /// <summary>Library tabs, in display order.</summary>
        public List<ModType> ModTypes { get; set; } = new();

        /// <summary>Body positions offered for animation entries.</summary>
        public List<string> Positions { get; set; } = new();

        /// <summary>Background opacity of My Mod Manager's windows (1 = solid).</summary>
        public float WindowOpacity { get; set; } = 1f;

        /// <summary>Hide everything not rated SFW, everywhere.</summary>
        public bool SafeView { get; set; } = false;

        /// <summary>Library "Group" choice: folder, emote, category, position or none.</summary>
        public string LibraryGroupBy { get; set; } = "folder";

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
            if (Version < CurrentVersion)
                BackUpBeforeMigration(pluginInterface);
            if (Normalize())
                Save();
        }

        /// <summary>Copies the config file aside once, before a version migration rewrites it.</summary>
        private void BackUpBeforeMigration(IDalamudPluginInterface pi)
        {
            try
            {
                var file = pi.ConfigFile;
                if (!file.Exists) return;
                var backup = Path.Combine(file.DirectoryName!, $"{Path.GetFileNameWithoutExtension(file.Name)}.v{Version}-backup.json");
                if (!File.Exists(backup))
                    file.CopyTo(backup);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Couldn't back up the config before migrating it.");
            }
        }

        /// <summary>Fills gaps and applies data migrations. Returns true when anything was migrated.</summary>
        public bool Normalize()
        {
            var migrated = false;
            ManagedMods ??= new List<ManagedMod>();
            KnownTags ??= new List<string>();
            SuspendedIds ??= new List<string>();
            ModTypes ??= new List<ModType>();
            Positions ??= new List<string>();
            WindowOpacity = Math.Clamp(WindowOpacity, 0.2f, 1f);
            if (Version < 2)
            {
                MigrateToV2();
                migrated = true;
            }
            EnsureModTypes();

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
                if (m.Pose > 0)
                {
                    m.PoseNumber ??= m.Pose - 1;
                    m.Pose = 0;
                    migrated = true;
                }
                if (m.PoseNumber < 0) m.PoseNumber = null;
                m.CategoryName ??= string.Empty;
                m.Category ??= string.Empty;
                m.Position ??= string.Empty;
                if (ModTypes.All(t => t.Id != m.ModTypeId)) m.ModTypeId = ModTypes[0].Id;
                m.Tags ??= new List<string>();
                m.Tags = m.Tags
                    .Select(t => t?.Trim() ?? string.Empty)
                    .Where(t => t.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            RebuildAssignedTags();
            return migrated;
        }

        public const string AnimationsTypeId = "animations";
        public const string BodyTypeId = "body";
        public const string ExtrasTypeId = "extras";

        private static readonly string[] DefaultAnimationCategories = ["Dance", "Idle", "Walk", "Pose", "Emote"];
        private static readonly string[] DefaultPositions = ["Standing", "Sitting", "Ground sitting", "Kneeling", "Leaning", "Lying"];

        // V1 folders that only meant "not sorted yet".
        private static readonly string[] PlaceholderFolders = ["Default", "Unassigned", "To Assign"];

        /// <summary>A fresh install gets three tabs; they can be renamed, restyled or deleted.</summary>
        private void EnsureModTypes()
        {
            if (ModTypes.Count > 0)
                return;

            ModTypes.Add(new ModType { Id = AnimationsTypeId, Name = "Animations", Style = ModTypeStyle.Animation, Categories = DefaultAnimationCategories.ToList() });
            ModTypes.Add(new ModType { Id = BodyTypeId, Name = "Body", Style = ModTypeStyle.Pick, Categories = ["Physics", "Scale", "Skin"] });
            ModTypes.Add(new ModType { Id = ExtrasTypeId, Name = "Extras", Style = ModTypeStyle.Toggle, Categories = ["Fix", "VFX", "Shader"] });
            if (Positions.Count == 0)
                Positions.AddRange(DefaultPositions);
        }

        /// <summary>
        /// V1 to V2: tags written as "Type - Position" (e.g. "Sex - Riding") become Category and
        /// Position; a lone tag such as "Dance" becomes the Category; the "Imported" marker is
        /// dropped (Unrated entries are the unsorted ones now). Non-animation entries move to
        /// the Body (single-choice options) or Extras tab.
        /// </summary>
        private void MigrateToV2()
        {
            EnsureModTypes();
            var typePosition = new Regex(@"^\s*(.+?)\s+-\s+(.+?)\s*$");

            foreach (var m in ManagedMods)
            {
                string? category = null, position = null;
                var leftover = new List<string>();
                foreach (var tag in m.Tags ?? new List<string>())
                {
                    var t = tag?.Trim() ?? string.Empty;
                    if (t.Length == 0 || t.Equals("Imported", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var match = typePosition.Match(t);
                    if (match.Success && category == null)
                    {
                        category = match.Groups[1].Value;
                        position = match.Groups[2].Value;
                    }
                    else
                    {
                        leftover.Add(t);
                    }
                }

                if (category == null && leftover.Count > 0)
                {
                    category = leftover[0];
                    leftover.RemoveAt(0);
                }

                m.Tags = leftover;
                m.Category = category ?? string.Empty;
                m.Position = position ?? string.Empty;

                if (PlaceholderFolders.Any(f => f.Equals(m.CategoryName?.Trim(), StringComparison.OrdinalIgnoreCase)))
                    m.CategoryName = string.Empty;

                m.ModTypeId = m.IsAnimation
                    ? AnimationsTypeId
                    : !string.IsNullOrEmpty(m.OptionName) && m.GroupType == Penumbra.Api.Enums.GroupType.Single
                        ? BodyTypeId
                        : ExtrasTypeId;
            }

            foreach (var type in ModTypes)
            {
                var used = ManagedMods.Where(m => m.ModTypeId == type.Id && m.Category.Length > 0).Select(m => m.Category);
                type.Categories = type.Categories.Concat(used).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            }

            Positions = Positions.Concat(ManagedMods.Select(m => m.Position).Where(p => p.Length > 0))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            Version = 2;
        }

        public ModType ModTypeOf(ManagedMod mod) => ModTypes.FirstOrDefault(t => t.Id == mod.ModTypeId) ?? ModTypes[0];

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
