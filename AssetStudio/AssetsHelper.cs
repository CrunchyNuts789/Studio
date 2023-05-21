﻿using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Globalization;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Text;
using MessagePack;

namespace AssetStudio
{
    public static class AssetsHelper
    {
        public const string MapName = "Maps";

        public static bool Minimal = true;
        public static CancellationTokenSource tokenSource = new CancellationTokenSource();

        private static string BaseFolder = "";
        private static Dictionary<string, Entry> CABMap = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, HashSet<long>> Offsets = new Dictionary<string, HashSet<long>>();
        private static AssetsManager assetsManager = new AssetsManager() { Silent = true, SkipProcess = true, ResolveDependencies = false };

        public record Entry
        {
            public string Path { get; set; }
            public long Offset { get; set; }
            public string[] Dependencies { get; set; }
        }

        public static string[] GetMaps()
        {
            Directory.CreateDirectory(MapName);
            var files = Directory.GetFiles(MapName, "*.bin", SearchOption.TopDirectoryOnly);
            return files.Select(Path.GetFileNameWithoutExtension).ToArray();
        }

        public static void Clear()
        {
            CABMap.Clear();
            Offsets.Clear();
            BaseFolder = string.Empty;

            tokenSource.Dispose();
            tokenSource = new CancellationTokenSource();

            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        public static void ClearOffsets() => Offsets.Clear();

        public static void Remove(string path) => Offsets.Remove(path);

        public static bool TryAdd(string name, out string path)
        {
            if (CABMap.TryGetValue(name, out var entry))
            {
                path = Path.Combine(BaseFolder, entry.Path);
                if (!Offsets.ContainsKey(path))
                {
                    Offsets.Add(path, new HashSet<long>());
                }
                Offsets[path].Add(entry.Offset);
                return true;
            }
            path = string.Empty;
            return false;
        }

        public static bool TryGet(string path, out long[] offsets)
        {
            if (Offsets.TryGetValue(path, out var list))
            {
                offsets = list.ToArray();
                return true;
            }
            offsets = Array.Empty<long>();
            return false;
        }

        public static void BuildCABMap(string[] files, string mapName, string baseFolder, Game game)
        {
            Logger.Info("Building CABMap...");
            try
            {
                CABMap.Clear();
                Progress.Reset();
                var collision = 0;
                BaseFolder = baseFolder;
                assetsManager.Game = game;
                foreach (var file in LoadFiles(files))
                {
                    BuildCABMap(file, ref collision);
                }

                DumpCABMap(mapName);

                Logger.Info($"CABMap build successfully !! {collision} collisions found");
            }
            catch (Exception e)
            {
                Logger.Warning($"CABMap was not build, {e}");
            }
        }

        private static IEnumerable<string> LoadFiles(string[] files)
        {
            for (int i = 0; i < files.Length; i++)
            {
                var file = files[i];
                assetsManager.LoadFiles(file);
                if (assetsManager.assetsFileList.Count > 0)
                {
                    yield return file;
                    Logger.Info($"[{i + 1}/{files.Length}] Processed {Path.GetFileName(file)}");
                    Progress.Report(i + 1, files.Length);
                }
                assetsManager.Clear();
            }
        }

        private static void BuildCABMap(string file, ref int collision)
        {
            var relativePath = Path.GetRelativePath(BaseFolder, file);
            foreach (var assetsFile in assetsManager.assetsFileList)
            {
                if (tokenSource.IsCancellationRequested)
                {
                    Logger.Info("Building CABMap has been cancelled !!");
                    return;
                }
                var entry = new Entry()
                {
                    Path = relativePath,
                    Offset = assetsFile.offset,
                    Dependencies = assetsFile.m_Externals.Select(x => x.fileName).ToArray()
                };

                if (CABMap.ContainsKey(assetsFile.fileName))
                {
                    collision++;
                    continue;
                }
                CABMap.Add(assetsFile.fileName, entry);
            }
        }

        private static void DumpCABMap(string mapName)
        {
            CABMap = CABMap.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var outputFile = Path.Combine(MapName, $"{mapName}.bin");

            Directory.CreateDirectory(Path.GetDirectoryName(outputFile));

            using (var binaryFile = File.OpenWrite(outputFile))
            using (var writer = new BinaryWriter(binaryFile))
            {
                writer.Write(BaseFolder);
                writer.Write(CABMap.Count);
                foreach (var kv in CABMap)
                {
                    writer.Write(kv.Key);
                    writer.Write(kv.Value.Path);
                    writer.Write(kv.Value.Offset);
                    writer.Write(kv.Value.Dependencies.Length);
                    foreach (var cab in kv.Value.Dependencies)
                    {
                        writer.Write(cab);
                    }
                }
            }
        }

        public static void LoadCABMap(string mapName)
        {
            Logger.Info($"Loading {mapName}");
            try
            {
                CABMap.Clear();
                using (var fs = File.OpenRead(Path.Combine(MapName, $"{mapName}.bin")))
                using (var reader = new BinaryReader(fs))
                {
                    BaseFolder = reader.ReadString();
                    var count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var cab = reader.ReadString();
                        var path = reader.ReadString();
                        var offset = reader.ReadInt64();
                        var depCount = reader.ReadInt32();
                        var dependencies = new string[depCount];
                        for (int j = 0; j < depCount; j++)
                        {
                            dependencies[j] = reader.ReadString();
                        }
                        var entry = new Entry()
                        {
                            Path = path,
                            Offset = offset,
                            Dependencies = dependencies
                        };
                        CABMap.Add(cab, entry);
                    }
                }
                Logger.Info($"Loaded {mapName} !!");
            }
            catch (Exception e)
            {
                Logger.Warning($"{mapName} was not loaded, {e}"); 
            }
        }

        public static void BuildAssetMap(string[] files, string mapName, Game game, string savePath, ExportListType exportListType, ManualResetEvent resetEvent = null, ClassIDType[] typeFilters = null, Regex[] nameFilters = null, Regex[] containerFilters = null)
        {
            Logger.Info("Building AssetMap...");
            try
            {
                Progress.Reset();
                assetsManager.Game = game;
                var assets = new List<AssetEntry>();
                foreach (var file in LoadFiles(files))
                {
                    BuildAssetMap(file, assets, typeFilters, nameFilters, containerFilters);
                }

                UpdateContainers(assets, game);

                ExportAssetsMap(assets.ToArray(), game, mapName, savePath, exportListType, resetEvent);
            }
            catch(Exception e)
            {
                Logger.Warning($"AssetMap was not build, {e}");
            }
            
        }

        private static void BuildAssetMap(string file, List<AssetEntry> assets, ClassIDType[] typeFilters = null, Regex[] nameFilters = null, Regex[] containerFilters = null)
        {
            var containers = new List<(PPtr<Object>, string)>();
            var mihoyoBinDataNames = new List<(PPtr<Object>, string)>();
            var objectAssetItemDic = new Dictionary<Object, AssetEntry>();
            var animators = new List<(PPtr<Object>, AssetEntry)>();
            foreach (var assetsFile in assetsManager.assetsFileList)
            {
                foreach (var objInfo in assetsFile.m_Objects)
                {
                    if (tokenSource.IsCancellationRequested)
                    {
                        Logger.Info("Building AssetMap has been cancelled !!");
                        return;
                    }
                    var objectReader = new ObjectReader(assetsFile.reader, assetsFile, objInfo, assetsManager.Game);
                    var obj = new Object(objectReader);
                    var asset = new AssetEntry()
                    {
                        Source = file,
                        PathID = objectReader.m_PathID,
                        Type = objectReader.type,
                        Container = ""
                    };

                    var exportable = true;
                    try
                    {
                        switch (objectReader.type)
                        {
                            case ClassIDType.AssetBundle:
                                var assetBundle = new AssetBundle(objectReader);
                                foreach (var m_Container in assetBundle.m_Container)
                                {
                                    var preloadIndex = m_Container.Value.preloadIndex;
                                    var preloadSize = m_Container.Value.preloadSize;
                                    var preloadEnd = preloadIndex + preloadSize;
                                    for (int k = preloadIndex; k < preloadEnd; k++)
                                    {
                                        containers.Add((assetBundle.m_PreloadTable[k], m_Container.Key));
                                    }
                                }
                                obj = null;
                                asset.Name = assetBundle.m_Name;
                                exportable = !Minimal;
                                break;
                            case ClassIDType.GameObject:
                                var gameObject = new GameObject(objectReader);
                                obj = gameObject;
                                asset.Name = gameObject.m_Name;
                                exportable = !Minimal;
                                break;
                            case ClassIDType.Shader when Shader.Parsable:
                                asset.Name = objectReader.ReadAlignedString();
                                if (string.IsNullOrEmpty(asset.Name))
                                {
                                    var m_parsedForm = new SerializedShader(objectReader);
                                    asset.Name = m_parsedForm.m_Name;
                                }
                                break;
                            case ClassIDType.Animator:
                                var component = new PPtr<Object>(objectReader);
                                animators.Add((component, asset));
                                break;
                            case ClassIDType.MiHoYoBinData:
                                var MiHoYoBinData = new MiHoYoBinData(objectReader);
                                obj = MiHoYoBinData;
                                break;
                            case ClassIDType.IndexObject:
                                var indexObject = new IndexObject(objectReader);
                                obj = null;
                                foreach (var index in indexObject.AssetMap)
                                {
                                    mihoyoBinDataNames.Add((index.Value.Object, index.Key));
                                }
                                asset.Name = "IndexObject";
                                exportable = !Minimal;
                                break;
                            case ClassIDType.Font:
                            case ClassIDType.Material:
                            case ClassIDType.Texture:
                            case ClassIDType.Mesh:
                            case ClassIDType.Sprite:
                            case ClassIDType.TextAsset:
                            case ClassIDType.Texture2D:
                            case ClassIDType.VideoClip:
                            case ClassIDType.AudioClip:
                            case ClassIDType.AnimationClip:
                                asset.Name = objectReader.ReadAlignedString();
                                break;
                            default:
                                asset.Name = objectReader.type.ToString();
                                exportable = !Minimal;
                                break;
                        }
                    }
                    catch (Exception e)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("Unable to load object")
                            .AppendLine($"Assets {assetsFile.fileName}")
                            .AppendLine($"Path {assetsFile.originalPath}")
                            .AppendLine($"Type {objectReader.type}")
                            .AppendLine($"PathID {objectReader.m_PathID}")
                            .Append(e);
                        Logger.Error(sb.ToString());
                    }
                    if (obj != null)
                    {
                        objectAssetItemDic.Add(obj, asset);
                        assetsFile.AddObject(obj);
                    }
                    var isMatchRegex = nameFilters.IsNullOrEmpty() || nameFilters.Any(x => x.IsMatch(asset.Name) || asset.Type == ClassIDType.Animator);
                    var isFilteredType = typeFilters.IsNullOrEmpty() || typeFilters.Contains(asset.Type) || asset.Type == ClassIDType.Animator;
                    if (isMatchRegex && isFilteredType && exportable)
                    {
                        assets.Add(asset);
                    }
                }
            }
            foreach ((var pptr, var asset) in animators)
            {
                if (pptr.TryGet<GameObject>(out var gameObject) && (nameFilters.IsNullOrEmpty() || nameFilters.Any(x => x.IsMatch(gameObject.m_Name))) && (typeFilters.IsNullOrEmpty() || typeFilters.Contains(asset.Type)))
                {
                    asset.Name = gameObject.m_Name;
                }
                else
                {
                    assets.Remove(asset);
                }

            }
            foreach ((var pptr, var name) in mihoyoBinDataNames)
            {
                if (pptr.TryGet<MiHoYoBinData>(out var miHoYoBinData))
                {
                    var asset = objectAssetItemDic[miHoYoBinData];
                    if (int.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash))
                    {
                        asset.Name = name;
                        asset.Container = hash.ToString();
                    }
                    else asset.Name = $"BinFile #{asset.PathID}";
                }
            }
            foreach ((var pptr, var container) in containers)
            {
                if (pptr.TryGet(out var obj))
                {
                    var item = objectAssetItemDic[obj];
                    if (containerFilters.IsNullOrEmpty() || containerFilters.Any(x => x.IsMatch(container)))
                    {
                        item.Container = container;
                    }
                    else
                    {
                        assets.Remove(item);
                    }
                }
            }
        }

        private static void UpdateContainers(List<AssetEntry> assets, Game game)
        {
            if (game.Type.IsGISubGroup() && assets.Count > 0)
            {
                Logger.Info("Updating Containers...");
                foreach (var asset in assets)
                {
                    if (int.TryParse(asset.Container, out var value))
                    {
                        var last = unchecked((uint)value);
                        var name = Path.GetFileNameWithoutExtension(asset.Source);
                        if (uint.TryParse(name, out var id))
                        {
                            var path = ResourceIndex.GetContainer(id, last);
                            if (!string.IsNullOrEmpty(path))
                            {
                                asset.Container = path;
                                if (asset.Type == ClassIDType.MiHoYoBinData)
                                {
                                    asset.Name = Path.GetFileNameWithoutExtension(path);
                                }
                            }
                        }
                    }
                }
                Logger.Info("Updated !!");
            }
        }

        private static void ExportAssetsMap(AssetEntry[] toExportAssets, Game game, string name, string savePath, ExportListType exportListType, ManualResetEvent resetEvent = null)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");

                Progress.Reset();

                string filename = Path.Combine(savePath, $"{name}{exportListType.GetExtension()}");
                switch (exportListType)
                {
                    case ExportListType.XML:
                        var xmlSettings = new XmlWriterSettings() { Indent = true };
                        using (XmlWriter writer = XmlWriter.Create(filename, xmlSettings))
                        {
                            writer.WriteStartDocument();
                            writer.WriteStartElement("Assets");
                            writer.WriteAttributeString("filename", filename);
                            writer.WriteAttributeString("createdAt", DateTime.UtcNow.ToString("s"));
                            foreach(var asset in toExportAssets)
                            {
                                writer.WriteStartElement("Asset");
                                writer.WriteElementString("Name", asset.Name);
                                writer.WriteElementString("Container", asset.Container);
                                writer.WriteStartElement("Type");
                                writer.WriteAttributeString("id", ((int)asset.Type).ToString());
                                writer.WriteValue(asset.Type.ToString());
                                writer.WriteEndElement();
                                writer.WriteElementString("PathID", asset.PathID.ToString());
                                writer.WriteElementString("Source", asset.Source);
                                writer.WriteEndElement();
                            }
                            writer.WriteEndElement();
                            writer.WriteEndDocument();
                        }
                        break;
                    case ExportListType.JSON:
                        using (StreamWriter file = File.CreateText(filename))
                        {
                            var serializer = new JsonSerializer() { Formatting = Newtonsoft.Json.Formatting.Indented };
                            serializer.Converters.Add(new StringEnumConverter());
                            serializer.Serialize(file, toExportAssets);
                        }
                        break;
                    case ExportListType.MessagePack:
                        using (var file = File.Create(filename))
                        {
                            var assetMap = new AssetMap
                            {
                                GameType = game.Type,
                                AssetEntries = toExportAssets
                            };
                            MessagePackSerializer.Serialize(file, assetMap, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
                        }
                        break;
                }

                Logger.Info($"Finished buidling AssetMap with {toExportAssets.Length} assets.");

                resetEvent?.Set();
            });
        }
        public static void BuildBoth(string[] files, string mapName, string baseFolder, Game game, string savePath, ExportListType exportListType, ManualResetEvent resetEvent = null, ClassIDType[] typeFilters = null, Regex[] nameFilters = null, Regex[] containerFilters = null)
        {
            Logger.Info($"Building Both...");
            CABMap.Clear();
            Progress.Reset();
            var collision = 0;
            BaseFolder = baseFolder;
            assetsManager.Game = game;
            var assets = new List<AssetEntry>();
            foreach(var file in LoadFiles(files))
            {
                BuildCABMap(file, ref collision);
                BuildAssetMap(file, assets, typeFilters, nameFilters, containerFilters);
            }

            UpdateContainers(assets, game);
            DumpCABMap(mapName);

            Logger.Info($"Map build successfully !! {collision} collisions found");
            ExportAssetsMap(assets.ToArray(), game, mapName, savePath, exportListType, resetEvent);
        }
    }
}
