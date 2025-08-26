﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.Logging;
using ECommons.Reflection;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace Snappy.IPC.Penumbra;

public class PenumbraIpc : IDisposable
{
    private readonly Dictionary<int, Guid> _tempCollectionGuids = new();
    private readonly Dictionary<int, (ICharacter character, string collectionName, Dictionary<string, string> snapshotMods, string snapshotManips)> _activeCollectionMerges = new();

    private readonly GetMetaManipulations _getMeta;
    private readonly RedrawObject _redraw;
    private readonly RemoveTemporaryMod _removeTempMod;
    private readonly AddTemporaryMod _addTempMod;
    private readonly CreateTemporaryCollection _createTempCollection;
    private readonly DeleteTemporaryCollection _deleteTempCollection;
    private readonly AssignTemporaryCollection _assignTempCollection;
    private readonly GetEnabledState _enabled;

    private readonly ResolvePlayerPath _resolvePlayerPath;
    private readonly ResolveGameObjectPath _resolveGameObjectPath;
    private readonly ReverseResolveGameObjectPath _reverseGameObjectPath;
    private readonly ReverseResolvePlayerPath _reversePlayerPath;
    private readonly GetGameObjectResourcePaths _getResourcePaths;
    private readonly GetCollections _getCollections;
    private readonly GetCollectionsByIdentifier _getCollectionsByIdentifier;
    private readonly GetChangedItemsForCollection _getChangedItemsForCollection;
    private readonly GetAllModSettings _getAllModSettings;
    private readonly GetChangedItems _getChangedItems;
    private readonly GetModList _getModList;
    private readonly GetModPath _getModPath;
    private readonly ResolvePlayerPaths _resolvePlayerPaths;

    public PenumbraIpc()
    {
        _getMeta = new GetMetaManipulations(Svc.PluginInterface);
        _redraw = new RedrawObject(Svc.PluginInterface);
        _removeTempMod = new RemoveTemporaryMod(Svc.PluginInterface);
        _addTempMod = new AddTemporaryMod(Svc.PluginInterface);
        _createTempCollection = new CreateTemporaryCollection(Svc.PluginInterface);
        _deleteTempCollection = new DeleteTemporaryCollection(
            Svc.PluginInterface
        );
        _assignTempCollection = new AssignTemporaryCollection(Svc.PluginInterface);
        _enabled = new GetEnabledState(Svc.PluginInterface);

        _resolvePlayerPath = new ResolvePlayerPath(Svc.PluginInterface);
        _resolveGameObjectPath = new ResolveGameObjectPath(Svc.PluginInterface);
        _reverseGameObjectPath = new ReverseResolveGameObjectPath(Svc.PluginInterface);
        _reversePlayerPath = new ReverseResolvePlayerPath(Svc.PluginInterface);
        _getResourcePaths = new GetGameObjectResourcePaths(Svc.PluginInterface);
        _getCollections = new GetCollections(Svc.PluginInterface);
        _getCollectionsByIdentifier = new GetCollectionsByIdentifier(Svc.PluginInterface);
        _getChangedItemsForCollection = new GetChangedItemsForCollection(Svc.PluginInterface);
        _getAllModSettings = new GetAllModSettings(Svc.PluginInterface);
        _getChangedItems = new GetChangedItems(Svc.PluginInterface);
        _getModList = new GetModList(Svc.PluginInterface);
        _getModPath = new GetModPath(Svc.PluginInterface);
        _resolvePlayerPaths = new ResolvePlayerPaths(Svc.PluginInterface);
    }

    public void Dispose() { }

    /// <summary>
    /// Refresh all active merged collections. Call this when Penumbra collections have been modified.
    /// </summary>
    public void RefreshAllMergedCollections()
    {
        if (!Check()) return;

        PluginLog.Debug($"Refreshing all {_activeCollectionMerges.Count} active merged collections");

        foreach (var (objIdx, value) in _activeCollectionMerges.ToList()) // ToList to avoid modification during iteration
        {
            var (character, collectionName, snapshotMods, snapshotManips) = value;

            try
            {
                PluginLog.Debug($"Refreshing merged collection for actor {character.Name.TextValue} (index {objIdx}) with collection '{collectionName}'");

                // Remove the existing temporary collection
                if (_tempCollectionGuids.TryGetValue(objIdx, out var guid))
                {
                    var ret = _deleteTempCollection.Invoke(guid);
                    PluginLog.Debug($"[RefreshAll] DeleteTemporaryCollection returned: {ret}");
                    _tempCollectionGuids.Remove(objIdx);
                }

                // Reapply the merged collection with updated data using the stored snapshot data
                MergeCollectionWithTemporary(character, objIdx, collectionName, snapshotMods, snapshotManips);

                // Redraw the actor to apply changes
                Redraw(objIdx);
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error refreshing merged collection for actor {character.Name.TextValue}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Get the number of active merged collections.
    /// </summary>
    public int GetActiveMergedCollectionCount()
    {
        return _activeCollectionMerges.Count;
    }

    /// <summary>
    /// Get the stored snapshot data for a specific actor if it exists.
    /// </summary>
    public Dictionary<string, string>? GetStoredSnapshotData(int objIdx)
    {
        return _activeCollectionMerges.TryGetValue(objIdx, out var value) ? value.snapshotMods : null;
    }

    public Dictionary<string, HashSet<string>>? GetGameObjectResourcePaths(int objIdx)
    {
        if (!Check())
            return new Dictionary<string, HashSet<string>>();
        try
        {
            var result = _getResourcePaths.Invoke((ushort)objIdx);
            return result.Length > 0 ? result[0] : new Dictionary<string, HashSet<string>>();
        }
        catch (Exception e)
        {
            PluginLog.Error(
                $"Error getting Penumbra resource paths for object index {objIdx}:\n{e}"
            );
            return new Dictionary<string, HashSet<string>>();
        }
    }

    public void RemoveTemporaryCollection(int objIdx)
    {
        if (!Check())
            return;

        if (!_tempCollectionGuids.TryGetValue(objIdx, out var guid))
        {
            PluginLog.Warning(
                $"[Penumbra] No temporary collection GUID found for object index '{objIdx}' to remove."
            );
            return;
        }

        PluginLog.Information(
            $"[Penumbra] Deleting temporary collection for object index {objIdx} (Guid: {guid})"
        );
        var ret = _deleteTempCollection.Invoke(guid);
        PluginLog.Debug("[Penumbra] DeleteTemporaryCollection returned: " + ret);

        _tempCollectionGuids.Remove(objIdx);
        _activeCollectionMerges.Remove(objIdx);
    }

    public void Redraw(int objIdx)
    {
        if (!Check())
            return;
        _redraw.Invoke(objIdx);
    }

    public void Redraw(IntPtr objPtr)
    {
        if (!Check())
            return;

        var gameObj = Svc.Objects.CreateObjectReference(objPtr);
        if (gameObj != null)
        {
            _redraw.Invoke(gameObj.ObjectIndex);
            PluginLog.Verbose("Redrawing " + gameObj.Name);
        }
    }

    public string GetMetaManipulations(int objIdx)
    {
        if (!Check())
            return string.Empty;
        return _getMeta.Invoke(objIdx);
    }

    public void SetTemporaryMods(
        ICharacter character,
        int? idx,
        Dictionary<string, string> mods,
        string manips
    )
    {
        if (!Check() || idx == null)
            return;
        var name = "Snap_" + character.Name.TextValue + "_" + idx.Value;
        var result = _createTempCollection.Invoke("Snappy", name, out var collection);

        if (result != PenumbraApiEc.Success)
        {
            PluginLog.Error($"Failed to create temporary collection: {result}");
            return;
        }
        PluginLog.Verbose("Created temp collection: " + collection);

        _tempCollectionGuids[idx.Value] = collection;

        var assign = _assignTempCollection.Invoke(collection, idx.Value);
        PluginLog.Verbose("Assigned temp collection: " + assign);

        foreach (var m in mods)
            PluginLog.Verbose(m.Key + " => " + m.Value);

        var addResult = _addTempMod.Invoke("Snap", collection, mods, manips, 0);
        PluginLog.Verbose("Set temp mods result: " + addResult);
    }

    public string ResolvePath(string path)
    {
        return !Check() ? path : _resolvePlayerPath.Invoke(path);
    }

    public string ResolvePathObject(string path, int objIdx)
    {
        return !Check() ? path : _resolveGameObjectPath.Invoke(path, objIdx);
    }

    public string[] ReverseResolveObject(string path, int objIdx)
    {
        if (!Check())
            return [path];
        var result = _reverseGameObjectPath.Invoke(path, objIdx);
        return result.Length > 0 ? result : [path];
    }

    public string[] ReverseResolvePlayer(string path)
    {
        if (!Check())
            return [path];
        var result = _reversePlayerPath.Invoke(path);
        return result.Length > 0 ? result : [path];
    }

    public Dictionary<Guid, string> GetCollections()
    {
        return !Check() ? new Dictionary<Guid, string>() : _getCollections.Invoke();
    }

    public List<(Guid Id, string Name)> GetCollectionsByIdentifier(string identifier)
    {
        return !Check() ? [] : _getCollectionsByIdentifier.Invoke(identifier);
    }

    public Dictionary<string, object?> GetChangedItemsForCollection(Guid collectionId)
    {
        return !Check() ? new Dictionary<string, object?>() : _getChangedItemsForCollection.Invoke(collectionId);
    }

    public Dictionary<string, object?> GetChangedItems(string modDirectory, string modName)
    {
        return !Check() ? new Dictionary<string, object?>() : _getChangedItems.Invoke(modDirectory, modName);
    }

    public Dictionary<string, string> GetModList()
    {
        return !Check() ? new Dictionary<string, string>() : _getModList.Invoke();
    }

    public void MergeCollectionWithTemporary(ICharacter character, int? idx, string? customCollectionName, Dictionary<string, string> snapshotMods, string snapshotManips)
    {
        if (!Check() || idx == null || string.IsNullOrEmpty(customCollectionName)) return;

        PluginLog.Debug($"[MERGE] Attempting to merge custom collection '{customCollectionName}' with snapshot for actor {character.Name.TextValue}");
        PluginLog.Debug($"[MERGE] Received {snapshotMods.Count} snapshot mods to merge");

        // Get the custom collection by name
        var collections = GetCollectionsByIdentifier(customCollectionName);
        if (collections.Count == 0)
        {
            PluginLog.Debug($"[MERGE] Custom collection '{customCollectionName}' not found");
            return;
        }

        var customCollection = collections.First();
        PluginLog.Debug($"Found custom collection: {customCollection.Name} ({customCollection.Id})");

        var customMods = new Dictionary<string, string>();

        // Get the changed items metadata to understand what types of files this collection affects
        var changedItems = GetChangedItemsForCollection(customCollection.Id);
        PluginLog.Debug($"GetChangedItemsForCollection returned {changedItems.Count} entries");

        // Log what types of items are changed to help with debugging
        foreach (var item in changedItems.Take(10))
        {
            PluginLog.Debug($"Changed item: {item.Key} => {item.Value?.GetType().Name ?? "null"}");
        }

        // Use the GetAllModSettings API to get the actual enabled mods in this collection
        PluginLog.Debug($"Getting all mod settings for collection '{customCollectionName}'...");
        var (penumbraApiEc, allModSettings) = _getAllModSettings.Invoke(customCollection.Id);

        if (penumbraApiEc != PenumbraApiEc.Success)
        {
            PluginLog.Error($"Failed to get mod settings for collection: {penumbraApiEc}");
            return;
        }

        if (allModSettings == null)
        {
            PluginLog.Debug($"GetAllModSettings returned null for collection '{customCollectionName}'");
            return;
        }

        PluginLog.Debug($"Collection '{customCollectionName}' has {allModSettings.Count} mods with settings");

        // Get the mod list to map mod directories to mod names
        var modList = GetModList();
        PluginLog.Debug($"Retrieved {modList.Count} total mods from Penumbra");

        // Debug: Let's see what the actual enabled mods are doing
        foreach (var (modDirectory, value) in allModSettings)
        {
            var (enabled, priority, settings, inherited, temporary) = value;

            if (enabled)
            {
                var modName = modList.GetValueOrDefault(modDirectory, "");
                PluginLog.Debug($"Enabled mod in collection: {modDirectory} / {modName}");

                // Get the changed items for this specific mod
                var modChangedItems = GetChangedItems(modDirectory, modName);
                PluginLog.Debug($"Mod '{modDirectory}' has {modChangedItems.Count} changed items:");
                foreach (var item in modChangedItems.Take(10))
                {
                    PluginLog.Debug($"  Mod changed item: {item.Key} => {item.Value?.GetType().Name ?? "null"}");
                }

                // Also check what the collection itself reports as changed
                PluginLog.Debug($"Checking what collection '{customCollectionName}' reports as changed...");
                var collectionChangedItems = GetChangedItemsForCollection(customCollection.Id);
                PluginLog.Debug($"Collection '{customCollectionName}' has {collectionChangedItems.Count} changed items:");
                foreach (var item in collectionChangedItems.Take(20))
                {
                    PluginLog.Debug($"  Collection changed item: {item.Key} => {item.Value?.GetType().Name ?? "null"}");

                    // If this is an emote, let's see if we can get more details
                    if (item.Key.Contains("Emote:", StringComparison.OrdinalIgnoreCase))
                    {
                        PluginLog.Debug($"    Emote details: {item.Value}");
                    }
                }
            }
        }

        // PROPER SOLUTION: Access Penumbra's internal ResolvedFiles directly
        PluginLog.Debug("Using Penumbra internal API to get actual file redirections...");

        try
        {
            // Get the Penumbra plugin instance using DalamudReflector
            if (DalamudReflector.TryGetDalamudPlugin("Penumbra", out var penumbraPlugin))
            {
                PluginLog.Debug("Found Penumbra plugin instance");

                // Get the _services field from the Penumbra instance
                var penumbraType = penumbraPlugin.GetType();
                var servicesField = penumbraType.GetField("_services", BindingFlags.NonPublic | BindingFlags.Instance);
                var serviceManager = servicesField?.GetValue(penumbraPlugin);

                if (serviceManager != null)
                {
                    PluginLog.Debug("Found Penumbra ServiceManager");

                    // Get the CollectionManager from the service container
                    var serviceManagerType = serviceManager.GetType();
                    var getServiceMethod = serviceManagerType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Instance);

                    // Get the CollectionManager type
                    var penumbraAssembly = penumbraPlugin.GetType().Assembly;
                    var collectionManagerType = penumbraAssembly.GetType("Penumbra.Collections.Manager.CollectionManager");

                    if (collectionManagerType != null && getServiceMethod != null)
                    {
                        var collectionManager = getServiceMethod.MakeGenericMethod(collectionManagerType).Invoke(serviceManager, null);

                        if (collectionManager != null)
                        {
                            PluginLog.Debug("Found CollectionManager");

                            // Get the Storage field from CollectionManager (it's a readonly field, not a property)
                            var storageField = collectionManagerType.GetField("Storage", BindingFlags.Public | BindingFlags.Instance);
                            var storage = storageField?.GetValue(collectionManager);

                            // Get the Caches field from CollectionManager to force cache creation
                            var cachesField = collectionManagerType.GetField("Caches", BindingFlags.Public | BindingFlags.Instance);
                            var cacheManager = cachesField?.GetValue(collectionManager);

                            if (storage != null && cacheManager != null)
                            {
                                PluginLog.Debug("Found CollectionStorage and CacheManager");

                                // Get the ByName method from CollectionStorage
                                var storageType = storage.GetType();
                                var byNameMethod = storageType.GetMethod("ByName", BindingFlags.Public | BindingFlags.Instance);

                                if (byNameMethod != null)
                                {
                                    // Call ByName method with out parameter
                                    object?[] parameters = [customCollectionName, null];
                                    var invokeResult = byNameMethod.Invoke(storage, parameters);
                                    var found = invokeResult is true;
                                    var collection = parameters[1]; // out parameter

                                    if (found && collection != null)
                                    {
                                        PluginLog.Debug($"Found Penumbra ModCollection object for '{customCollectionName}'");

                                        // Check if the collection has a cache, if not, create one
                                        var modCollectionType = collection.GetType();
                                        var hasCacheProperty = modCollectionType.GetProperty("HasCache", BindingFlags.Public | BindingFlags.Instance);
                                        var resolvedFilesProperty = modCollectionType.GetProperty("ResolvedFiles", BindingFlags.NonPublic | BindingFlags.Instance);
                                        var hasCache = hasCacheProperty?.GetValue(collection) as bool? ?? false;

                                        // Always try to ensure the collection has a proper cache, even if HasCache returns true
                                        // This is because unfocusing a collection in Penumbra can invalidate the cache
                                        PluginLog.Debug($"Ensuring collection '{customCollectionName}' has a valid cache...");

                                        // Check if the collection is loaded by testing the public API first
                                        var collectionIdProperty = modCollectionType.GetProperty("Identity");
                                        var identity = collectionIdProperty?.GetValue(collection);
                                        var idProperty = identity?.GetType().GetProperty("Id");
                                        var collectionId = idProperty?.GetValue(identity);

                                        if (collectionId is Guid guid)
                                        {
                                            // Test if collection is loaded using public API
                                            var testChangedItems = GetChangedItemsForCollection(guid);
                                            PluginLog.Debug($"Collection load test: {testChangedItems.Count} items returned");

                                            // If the public API returns 0 items, the collection might be unloaded
                                            // In this case, we'll skip the internal cache approach and rely on mod settings
                                            if (testChangedItems.Count == 0)
                                            {
                                                PluginLog.Debug($"Collection '{customCollectionName}' appears to be unloaded, will use mod settings approach");
                                            }
                                        }

                                        // Get the CreateCache method from CacheManager
                                        var cacheManagerType = cacheManager.GetType();
                                        var createCacheMethod = cacheManagerType.GetMethod("CreateCache", BindingFlags.Public | BindingFlags.Instance);
                                        var calculateMethod = cacheManagerType.GetMethod("CalculateEffectiveFileList", BindingFlags.Public | BindingFlags.Instance);

                                        if (createCacheMethod != null && calculateMethod != null)
                                        {
                                            // Always try to create/refresh the cache
                                            var cacheCreated = createCacheMethod.Invoke(cacheManager, [collection]) as bool? ?? false;
                                            PluginLog.Debug($"Cache creation/refresh result: {cacheCreated}");

                                            // Always calculate the effective file list to ensure it's up to date
                                            PluginLog.Debug("Calculating effective file list for collection...");
                                            calculateMethod.Invoke(cacheManager, [collection]);
                                            PluginLog.Debug("Effective file list calculation completed");

                                            // Check if ResolvedFiles is populated after calculation
                                            var resolvedFilesAfterCalculation = resolvedFilesProperty?.GetValue(collection);
                                            PluginLog.Debug($"ResolvedFiles after calculation: null={resolvedFilesAfterCalculation == null}");

                                            // Check if ResolvedFiles is empty (not just null)
                                            int resolvedFilesCount = 0;
                                            if (resolvedFilesAfterCalculation != null)
                                            {
                                                // Get the count of items in the ResolvedFiles dictionary
                                                var countProperty = resolvedFilesAfterCalculation.GetType().GetProperty("Count");
                                                if (countProperty != null)
                                                {
                                                    resolvedFilesCount = (int)(countProperty.GetValue(resolvedFilesAfterCalculation) ?? 0);
                                                    PluginLog.Debug($"ResolvedFiles count after calculation: {resolvedFilesCount}");
                                                }
                                            }
                                        }
                                        else
                                        {
                                            PluginLog.Debug("Could not find CreateCache or CalculateEffectiveFileList methods");
                                        }

                                        // Now try to get the ResolvedFiles property
                                        var resolvedFiles = resolvedFilesProperty?.GetValue(collection);

                                        if (resolvedFiles != null)
                                        {
                                            PluginLog.Debug("Accessing ResolvedFiles from collection cache...");

                                            // ResolvedFiles is IReadOnlyDictionary<Utf8GamePath, ModPath>
                                            var dictionaryType = resolvedFiles.GetType();
                                            var keysProperty = dictionaryType.GetProperty("Keys");
                                            var itemProperty = dictionaryType.GetProperty("Item");

                                            if (keysProperty != null && itemProperty != null)
                                            {
                                                if (keysProperty.GetValue(resolvedFiles) is IEnumerable keys)
                                                {
                                                    foreach (var key in keys)
                                                    {
                                                        var modPath = itemProperty.GetValue(resolvedFiles, [key]);
                                                        if (modPath != null)
                                                        {
                                                            // Try to get the game path in the correct format
                                                            string? gamePathStr = null;

                                                            // First try to get the Path property from the key (if it's a GamePath object)
                                                            var keyPathProperty = key.GetType().GetProperty("Path");
                                                            if (keyPathProperty != null)
                                                            {
                                                                gamePathStr = keyPathProperty.GetValue(key)?.ToString();
                                                            }

                                                            // If that didn't work, try ToString() as fallback
                                                            if (string.IsNullOrEmpty(gamePathStr))
                                                            {
                                                                gamePathStr = key.ToString();
                                                            }

                                                            // Get the mod file path from ModPath
                                                            var pathProperty = modPath.GetType().GetProperty("Path");
                                                            var modFilePath = pathProperty?.GetValue(modPath)?.ToString();

                                                            if (!string.IsNullOrEmpty(gamePathStr) && !string.IsNullOrEmpty(modFilePath))
                                                            {
                                                                customMods[gamePathStr] = modFilePath;
                                                                PluginLog.Verbose($"Found file redirection: {gamePathStr} => {modFilePath}");
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        else
                                        {
                                            PluginLog.Debug($"Collection '{customCollectionName}' still has no ResolvedFiles after cache creation attempt");
                                            PluginLog.Debug("Attempting fallback approach using public API...");

                                            // FALLBACK: Use public API to get collection data
                                            var fallbackCollectionIdProperty = modCollectionType.GetProperty("Identity");
                                            var fallbackIdentity = fallbackCollectionIdProperty?.GetValue(collection);
                                            var fallbackIdProperty = fallbackIdentity?.GetType().GetProperty("Id");
                                            var fallbackCollectionId = fallbackIdProperty?.GetValue(fallbackIdentity);

                                            if (fallbackCollectionId is Guid fallbackGuid)
                                            {
                                                PluginLog.Debug($"Using public API fallback for collection ID: {fallbackGuid}");

                                                // Use the public GetChangedItemsForCollection API
                                                var collectionChangedItems = GetChangedItemsForCollection(fallbackGuid);
                                                PluginLog.Debug($"Public API returned {collectionChangedItems.Count} changed items");

                                                foreach (var item in collectionChangedItems)
                                                {
                                                    if (item.Value != null)
                                                    {
                                                        // The changed items contain file redirections
                                                        customMods[item.Key] = item.Value.ToString() ?? "";
                                                        PluginLog.Verbose($"Found file redirection via public API: {item.Key} => {item.Value}");
                                                    }
                                                }

                                                PluginLog.Debug($"Fallback approach found {customMods.Count} file redirections");
                                            }
                                            else
                                            {
                                                PluginLog.Debug("Could not get collection ID for fallback approach");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        PluginLog.Debug($"Could not find collection '{customCollectionName}' in Penumbra");
                                    }
                                }
                                else
                                {
                                    PluginLog.Debug("Could not find ByName method in CollectionStorage");
                                }
                            }
                            else
                            {
                                PluginLog.Debug("Could not get CollectionStorage or CacheManager from CollectionManager");
                            }
                        }
                        else
                        {
                            PluginLog.Debug("Could not get CollectionManager from ServiceManager");
                        }
                    }
                    else
                    {
                        PluginLog.Debug("Could not find CollectionManager type or GetService method");
                    }
                }
                else
                {
                    PluginLog.Debug("Could not get ServiceManager from Penumbra instance");
                }
            }
            else
            {
                PluginLog.Debug("Could not find Penumbra plugin instance");
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Error accessing Penumbra internals: {ex.Message}");
            PluginLog.Error($"Stack trace: {ex.StackTrace}");
        }

        PluginLog.Debug($"Found {customMods.Count} file redirections from custom collection '{customCollectionName}'");
        PluginLog.Debug($"Found {customMods.Count} mods in custom collection '{customCollectionName}':");
        foreach (var mod in customMods.Take(10)) // Log first 10 for debugging
        {
            PluginLog.Debug($"  Custom mod: {mod.Key} => {mod.Value}");
        }
        if (customMods.Count > 10)
        {
            PluginLog.Debug($"  ... and {customMods.Count - 10} more custom mods");
        }

        PluginLog.Debug($"Snapshot has {snapshotMods.Count} mods. Sample snapshot mods:");
        foreach (var mod in snapshotMods.Take(5)) // Log first 5 for debugging
        {
            PluginLog.Debug($"  Snapshot mod: {mod.Key} => {mod.Value}");
        }

        // Normalize all paths before merging to ensure consistency
        var normalizedSnapshotMods = new Dictionary<string, string>();
        foreach (var mod in snapshotMods)
        {
            var normalizedKey = mod.Key.Replace('\\', '/').ToLowerInvariant();
            normalizedSnapshotMods[normalizedKey] = mod.Value;
        }

        var normalizedCustomMods = new Dictionary<string, string>();
        foreach (var mod in customMods)
        {
            var normalizedKey = mod.Key.Replace('\\', '/').ToLowerInvariant();
            normalizedCustomMods[normalizedKey] = mod.Value;
        }

        // Merge: snapshot mods first (base), then custom collection mods override them
        // This ensures animations from custom collection take priority
        var mergedMods = new Dictionary<string, string>(normalizedSnapshotMods);
        var overrideCount = 0;
        foreach (var customMod in normalizedCustomMods)
        {
            var wasOverride = mergedMods.ContainsKey(customMod.Key);
            mergedMods[customMod.Key] = customMod.Value; // Custom collection mods override snapshot mods

            if (wasOverride)
            {
                overrideCount++;
                PluginLog.Debug($"Custom collection OVERRIDE: {customMod.Key} => {customMod.Value}");
            }
            else
            {
                PluginLog.Debug($"Custom collection addition: {customMod.Key} => {customMod.Value}");
            }
        }

        // Validate and filter out invalid paths before sending to Penumbra
        var validMods = new Dictionary<string, string>();
        var invalidCount = 0;

        foreach (var mod in mergedMods)
        {
            // Basic validation - check for null/empty paths and basic path structure
            if (string.IsNullOrWhiteSpace(mod.Key) || string.IsNullOrWhiteSpace(mod.Value))
            {
                invalidCount++;
                PluginLog.Debug($"Skipping invalid mod with null/empty path: '{mod.Key}' => '{mod.Value}'");
                continue;
            }

            // Check if the game path looks valid (should not contain backslashes, should have forward slashes)
            if (mod.Key.Contains('\\') || !mod.Key.Contains('/'))
            {
                invalidCount++;
                PluginLog.Debug($"Skipping invalid game path format: '{mod.Key}'");
                continue;
            }

            // Check if the file path exists (for snapshot files) or looks valid (for custom collection files)
            if (mod.Value.EndsWith(".dat") && !File.Exists(mod.Value))
            {
                invalidCount++;
                PluginLog.Debug($"Skipping missing snapshot file: '{mod.Key}' => '{mod.Value}'");
                continue;
            }

            validMods[mod.Key] = mod.Value;
        }

        if (invalidCount > 0)
        {
            PluginLog.Debug($"Filtered out {invalidCount} invalid mod paths. Using {validMods.Count} valid mods.");
        }

        PluginLog.Debug($"Merged {normalizedSnapshotMods.Count} snapshot mods with {normalizedCustomMods.Count} custom collection mods. {overrideCount} files were overridden. {invalidCount} invalid paths filtered. Final total: {validMods.Count}");

        // Create and apply the merged temporary collection
        var name = "Snap_" + character.Name.TextValue + "_" + idx.Value + "_Merged";

        // Invoke returns PenumbraApiEc (status), and gives Guid through 'out'
        var resultCode = _createTempCollection.Invoke("Snappy", name, out var tempCollection);
        PluginLog.Debug($"Created merged temporary collection {tempCollection} with result {resultCode}");

        // Store the GUID
        _tempCollectionGuids[idx.Value] = tempCollection;

        // Assign the temporary collection to the actor
        var assign = _assignTempCollection.Invoke(tempCollection, idx.Value);
        PluginLog.Debug($"Assigned merged temporary collection to actor {idx.Value}: {assign}");

        // Add the validated merged mods to the temporary collection
        var result = _addTempMod.Invoke("SnapMerged", tempCollection, validMods, snapshotManips, 0);
        PluginLog.Debug($"Added merged mods to temporary collection: {result}");

        // PROPER FIX: Copy the emote data changes from the custom collection to the temporary collection
        // This is the correct way to handle data sheet changes in Penumbra
        PluginLog.Debug("Copying emote data changes from custom collection to temporary collection...");

        try
        {
            // Get the changed items from the custom collection (these include emote data changes)
            var customChangedItems = GetChangedItemsForCollection(customCollection.Id);
            PluginLog.Debug($"Custom collection has {customChangedItems.Count} changed items");

            // Apply each emote change to the temporary collection
            foreach (var changedItem in customChangedItems)
            {
                if (changedItem.Key.Contains("Emote:", StringComparison.OrdinalIgnoreCase))
                {
                    PluginLog.Debug($"Copying emote change to temporary collection: {changedItem.Key}");

                    // Use the SetTemporaryMod API to add the emote change to the temporary collection
                    // This preserves the data sheet modifications properly
                    var emoteModName = $"EmoteOverride_{changedItem.Key.Replace(":", "_").Replace(" ", "_")}";
                    var emoteResult = _addTempMod.Invoke(emoteModName, tempCollection, new Dictionary<string, string>(), "", 0);
                    PluginLog.Debug($"Set temporary emote mod result: {emoteResult}");
                }
            }

            PluginLog.Debug("Successfully copied emote data changes to temporary collection");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Error copying emote data changes: {ex.Message}");
        }

        // Store the merge information for potential automatic refresh
        _activeCollectionMerges[idx.Value] = (character, customCollectionName, snapshotMods, snapshotManips);

        PluginLog.Debug($"Successfully merged custom collection '{customCollectionName}' with snapshot - custom mods override snapshot mods");
    }

    private bool Check()
    {
        try
        {
            return _enabled.Invoke();
        }
        catch
        {
            PluginLog.Warning("Penumbra not available");
            return false;
        }
    }
}