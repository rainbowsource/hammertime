﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LogicAndTrick.Gimme.Providers;
using LogicAndTrick.Oy;
using Newtonsoft.Json;
using Sledge.Common.Logging;
using Sledge.Common.Shell.Hooks;
using Sledge.Common.Shell.Settings;

namespace Sledge.Shell.Settings
{
    /// <summary>
    /// Manages all settings
    /// </summary>
    [Export(typeof(IInitialiseHook))]
    [Export(typeof(IShutdownHook))]
    public class SettingsProvider : IInitialiseHook, IShutdownHook
    {
        [ImportMany] private IEnumerable<Lazy<ISettingsContainer>> _settingsContainers;

        public async Task OnInitialise()
        {
            // Register all settings containers
            foreach (var export in _settingsContainers)
            {
                Log.Debug("Settings", "Settings container: " + export.Value.GetType().FullName);
                Add(export.Value);
            }

            // Load the settings
            await LoadSettings(null);

            // Listen for setting events
            Oy.Subscribe<string>("Settings:Load", LoadSettings);
            Oy.Subscribe<string>("Settings:Save", SaveSettings);
        }
        
        public async Task OnShutdown()
        {
            // Save settings on close
            await SaveSettings(null);
        }

        private readonly Dictionary<string, JsonSettingsStore> _values;
        private readonly List<ISettingsContainer> _containers;

        public SettingsProvider()
        {
            _values = new Dictionary<string, JsonSettingsStore>();
            _containers = new List<ISettingsContainer>();
        }

        /// <summary>
        /// Register a settings container
        /// </summary>
        /// <param name="settingsContainer">The settings container</param>
        private void Add(ISettingsContainer settingsContainer)
        {
            _containers.Add(settingsContainer);
        }

        /// <summary>
        /// Load settings from disk
        /// </summary>
        /// <param name="name">The name of the settings module to load, or null to load all</param>
        /// <returns>A task that completes when the load is complete</returns>
        private async Task LoadSettings(string name)
        {
            if (name == null) _values.Clear();
            else if (_values.ContainsKey(name)) _values.Remove(name);

            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sledge", "Shell");
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path, "*.json"))
                {
                    var containerName = Path.GetFileNameWithoutExtension(file);

                    if (containerName == null) continue;
                    if (name != null && containerName != name) continue;

                    JsonSettingsStore store;
                    try
                    {
                        store = new JsonSettingsStore(File.ReadAllText(file));
                    }
                    catch
                    {
                        store = new JsonSettingsStore();
                    }

                    _values[containerName] = store;
                }
            }

            foreach (var container in _containers)
            {
                if (name != null && container.Name != name) continue;
                container.LoadValues(_values.ContainsKey(container.Name) ? _values[container.Name] : new JsonSettingsStore());
            }
            Log.Debug("Settings", "Settings loaded.");
        }

        /// <summary>
        /// Save settings to disk
        /// </summary>
        /// <param name="name">The name of the settings module to save, or null to save all</param>
        /// <returns>A task that complete when the operation is complete</returns>
        private async Task SaveSettings(string name)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sledge", "Shell");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);

            foreach (var container in _containers)
            {
                if (name != null && container.Name != name) continue;
                var store = _values.ContainsKey(container.Name) ? _values[container.Name] : new JsonSettingsStore();
                container.StoreValues(store);
                File.WriteAllText(Path.Combine(path, container.Name + ".json"), store.ToJson());
            }
            Log.Debug("Settings", "Settings saved.");
        }
    }
}
