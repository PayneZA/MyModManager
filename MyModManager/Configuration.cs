using Dalamud.Configuration;
using Dalamud.Plugin;
using MyModManager.Models;
using System;
using System.Collections.Generic;

namespace MyModManager
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 1;

        public List<ManagedMod> ManagedMods { get; set; } = new();
        public Guid TargetCollectionId { get; set; } = Guid.Empty;

        [NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;
        }

        public void Save()
        {
            pluginInterface?.SavePluginConfig(this);
        }
    }
}
