using Android.Content;
using Android.Net;
using AndroidX.DocumentFile.Provider;
using AssetStudio;
using Fmod5Sharp;
using NVorbis;
using System.Collections;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Object = AssetStudio.Object;
using Uri = Android.Net.Uri;

namespace AssetStudio.AndroidApp.Processing;

public sealed class AssetStudioProcessor
{
    private readonly Context _context;
    private static int s_fmodAvailability = -1;
    private static string s_fmodStatus = "not checked";
    private static int s_fbxAvailability = -1;
    private static string s_fbxStatus = "not checked";

    public AssetStudioProcessor(Context context)
    {
        _context = context;
    }

    public Task<ExportSummary> ProcessTreeAsync(
        Uri treeUri,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        return ProcessTreeAsync(treeUri, new ExportOptions(), log, cancellationToken);
    }

    public Task<ExportSummary> ProcessTreeAsync(
        Uri treeUri,
        ExportOptions options,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        options ??= new ExportOptions();
        return Task.Run(() => ProcessTree(treeUri, options, log, cancellationToken), cancellationToken);
    }

    private ExportSummary ProcessTree(Uri treeUri, ExportOptions options, Action<string> uiLog, CancellationToken cancellationToken)
    {
        var sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var workingInputDir = Path.Combine(_context.CacheDir!.AbsolutePath!, "assetstudio_input", sessionId);
        var outputDir = CreateOutputDirectory(sessionId);
        Directory.CreateDirectory(workingInputDir);
        Directory.CreateDirectory(outputDir);

        var summary = new ExportSummary
        {
            OutputDirectory = outputDir,
            WorkingInputDirectory = workingInputDir,
            LogFilePath = Path.Combine(outputDir, $"export_log_{sessionId}.txt")
        };

        using var logWriter = new StreamWriter(summary.LogFilePath, false, new UTF8Encoding(false));
        var traceLinesSinceFlush = 0;
        var lastTraceFlushUtc = DateTime.UtcNow;
        void Log(string message, bool showInUi = true)
        {
            if (showInUi)
            {
                uiLog(message);
            }
            logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

            if (showInUi)
            {
                logWriter.Flush();
                return;
            }

            traceLinesSinceFlush++;
            var now = DateTime.UtcNow;
            if (traceLinesSinceFlush >= 200 || (now - lastTraceFlushUtc).TotalSeconds >= 2)
            {
                logWriter.Flush();
                traceLinesSinceFlush = 0;
                lastTraceFlushUtc = now;
            }
        }

        bool IsVerboseTraceLine(string message)
        {
            return message.StartsWith("TraceRead:", StringComparison.OrdinalIgnoreCase);
        }

        void CoreLog(string message)
        {
            Log(message, !IsVerboseTraceLine(message));
        }

        Log($"Session: {sessionId}");
        Log($"Working input: {workingInputDir}");
        Log($"Output: {outputDir}");
        Log($"Log file: {summary.LogFilePath}");
        Log($"Export options: type={(options.OnlyType?.ToString() ?? "All")}, image={options.ImageFormat}, audioToWav={options.ConvertAudioToWav}");

        var root = DocumentFile.FromTreeUri(_context, treeUri)
            ?? throw new IOException("Unable to open selected folder.");

        summary.CopiedFiles = CopyTreeToLocal(root, workingInputDir, CoreLog, cancellationToken);
        Log($"Files copied from selected folder: {summary.CopiedFiles}");

        ExportWithAssetStudio(workingInputDir, summary, options, CoreLog, cancellationToken);
        Log(
            "Finished. exported=" + summary.ExportedObjects +
            ", skipped=" + summary.SkippedObjects +
            ", failed=" + summary.FailedObjects);

        return summary;
    }

    private int CopyTreeToLocal(
        DocumentFile root,
        string localRoot,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var copied = 0;
        var queue = new Queue<(DocumentFile Dir, string RelativePath)>();
        queue.Enqueue((root, string.Empty));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, relative) = queue.Dequeue();

            var children = dir.ListFiles() ?? Array.Empty<DocumentFile>();
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var childName = SanitizeFileName(child.Name ?? "unnamed");
                var childRelative = string.IsNullOrEmpty(relative)
                    ? childName
                    : Path.Combine(relative, childName);

                if (child.IsDirectory)
                {
                    Directory.CreateDirectory(Path.Combine(localRoot, childRelative));
                    queue.Enqueue((child, childRelative));
                    continue;
                }

                if (!child.IsFile)
                {
                    continue;
                }

                var outputPath = Path.Combine(localRoot, childRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using var input = _context.ContentResolver!.OpenInputStream(child.Uri)
                    ?? throw new IOException($"Unable to read {child.Uri}");
                using var output = File.Create(outputPath);
                input.CopyTo(output);
                copied++;
            }
        }

        log("Source copy completed.");
        return copied;
    }

    private void ExportWithAssetStudio(
        string localInputDir,
        ExportSummary summary,
        ExportOptions options,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var previousLogger = Logger.Default;
        Logger.Default = new CallbackLogger(log, summary);
        var manager = new AssetsManager();
        var assemblyLoader = new AssemblyLoader();

        try
        {
            log("Loading files with AssetStudio core...");
            LoadFolderWithHeartbeat(manager, localInputDir, log, cancellationToken);
            summary.AssetsFilesLoaded = manager.assetsFileList.Count;
            log($"Assets files loaded: {summary.AssetsFilesLoaded}");
            if (options.ConvertAudioToWav)
            {
                TryLoadFmodFromFolder(localInputDir);
                var fmodAvailable = IsFmodRuntimeAvailable();
                if (fmodAvailable)
                {
                    log("FMOD runtime: available, AudioClip conversion to WAV is enabled for supported formats.");
                }
                else
                {
                    log($"FMOD runtime: unavailable ({s_fmodStatus}). AudioClip fallback: FSB5->WAV (Vorbis) without FMOD; unsupported codecs will stay in source container.");
                }
            }
            else
            {
                log("AudioClip conversion to WAV is disabled by export options.");
            }

            if ((options.OnlyType == null || options.OnlyType == ClassIDType.Animator) && !IsFbxRuntimeAvailable())
            {
                log($"FBX runtime: unavailable ({s_fbxStatus}). Animator export to FBX is not available on this Android build.");
            }
            LoadAssembliesIfPresent(localInputDir, assemblyLoader, log);

            var exportItems = BuildExportItems(manager, options);
            summary.ObjectsScanned = exportItems.Count;
            log($"Objects prepared for export: {summary.ObjectsScanned}");

            for (var i = 0; i < exportItems.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = exportItems[i];
                if (i == 0 || i % 200 == 0)
                {
                    log($"[{i + 1}/{exportItems.Count}] {item.TypeString}: {item.Name}");
                }

                try
                {
                    if (ExportConvertFile(item, summary, assemblyLoader, options))
                    {
                        summary.ExportedObjects++;
                    }
                    else
                    {
                        summary.SkippedObjects++;
                    }
                }
                catch (Exception ex)
                {
                    summary.FailedObjects++;
                    log($"Export failed: type={item.TypeString}, name={item.Name}, pathId={item.Asset.m_PathID}, error={ex.Message}");
                }
            }

            if (summary.SuppressedLoadObjectErrors > 0)
            {
                log($"Suppressed non-critical load errors: {summary.SuppressedLoadObjectErrors}");
            }

            if (summary.SuppressedReadTypeInfo > 0)
            {
                log($"Suppressed typetree read warnings: {summary.SuppressedReadTypeInfo}");
            }

            if (summary.SuppressedLoadingInfo > 0)
            {
                log($"Suppressed verbose loading lines: {summary.SuppressedLoadingInfo}");
            }
        }
        finally
        {
            assemblyLoader.Clear();
            manager.Clear();
            Logger.Default = previousLogger;
        }
    }

    private static void LoadFolderWithHeartbeat(
        AssetsManager manager,
        string localInputDir,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var progressLock = new object();
        var currentProgress = 0;
        var lastReportedProgress = -1;
        var lastProgressChangeUtc = DateTime.UtcNow;
        var previousProgressReporter = AssetStudio.Progress.Default;
        AssetStudio.Progress.Default = new System.Progress<int>(value =>
        {
            lock (progressLock)
            {
                if (value != currentProgress)
                {
                    currentProgress = value;
                    lastProgressChangeUtc = DateTime.UtcNow;
                }
            }
        });

        try
        {
            var task = Task.Run(() => manager.LoadFolder(localInputDir), cancellationToken);

            while (!task.Wait(TimeSpan.FromSeconds(2)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var elapsed = DateTime.UtcNow - startedAt;
                int progressSnapshot;
                DateTime progressChangedAt;
                lock (progressLock)
                {
                    progressSnapshot = currentProgress;
                    progressChangedAt = lastProgressChangeUtc;
                }

                if (progressSnapshot >= lastReportedProgress + 5 || progressSnapshot == 100)
                {
                    lastReportedProgress = progressSnapshot;
                    log($"Core progress: {progressSnapshot}%");
                }

                var noProgressFor = DateTime.UtcNow - progressChangedAt;
                if (noProgressFor.TotalSeconds >= 30)
                {
                    log(
                        $"Reading assets... {elapsed:mm\\:ss} elapsed, " +
                        $"progress {progressSnapshot}%, no progress for {noProgressFor:mm\\:ss}");
                }
                else
                {
                    log($"Reading assets... {elapsed:mm\\:ss} elapsed, progress {progressSnapshot}%");
                }
            }

            // Propagate the original exception (if any).
            task.GetAwaiter().GetResult();
        }
        finally
        {
            AssetStudio.Progress.Default = previousProgressReporter;
        }
    }

    private static void LoadAssembliesIfPresent(string localInputDir, AssemblyLoader assemblyLoader, Action<string> log)
    {
        var dllDirectories = Directory.EnumerateFiles(localInputDir, "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (dllDirectories.Count == 0)
        {
            log("Managed assemblies not found in selected folder. MonoBehaviour export will use typetree fallback.");
            return;
        }

        foreach (var dllDirectory in dllDirectories)
        {
            try
            {
                assemblyLoader.Load(dllDirectory!);
            }
            catch
            {
                // ignore a single bad assembly directory
            }
        }

        log($"Assembly folders scanned: {dllDirectories.Count}");
    }

    private List<ExportItem> BuildExportItems(AssetsManager manager, ExportOptions options)
    {
        var estimatedCount = manager.assetsFileList.Sum(x => x.Objects.Count);
        var objectToItem = new Dictionary<Object, ExportItem>(estimatedCount);
        var containers = new List<(PPtr<Object> ObjectRef, string ContainerPath)>();
        var index = 0;

        foreach (var assetsFile in manager.assetsFileList)
        {
            foreach (var asset in assetsFile.Objects)
            {
                var item = new ExportItem(asset)
                {
                    UniqueId = $" #{index}",
                    Name = GetDisplayName(asset, index)
                };
                objectToItem[asset] = item;
                index++;

                switch (asset)
                {
                    case AssetBundle assetBundle:
                        foreach (var container in assetBundle.m_Container)
                        {
                            var preloadIndex = container.Value.preloadIndex;
                            var preloadEnd = preloadIndex + container.Value.preloadSize;
                            for (var k = preloadIndex; k < preloadEnd; k++)
                            {
                                containers.Add((assetBundle.m_PreloadTable[k], container.Key));
                            }
                        }
                        break;
                    case ResourceManager resourceManager:
                        foreach (var container in resourceManager.m_Container)
                        {
                            containers.Add((container.Value, container.Key));
                        }
                        break;
                }
            }
        }

        foreach (var (objectRef, containerPath) in containers)
        {
            if (objectRef.TryGet(out var obj) && objectToItem.TryGetValue(obj, out var item))
            {
                item.Container = containerPath;
            }
        }

        return objectToItem.Values
            .Where(item => IsExportable(item.Asset, options))
            .ToList();
    }

    private static bool IsExportable(Object asset, ExportOptions options)
    {
        if (!IsSupportedType(asset))
        {
            return false;
        }

        if (options.OnlyType == null)
        {
            if (asset is Animator && !IsFbxRuntimeAvailable())
            {
                return false;
            }
            return true;
        }

        var classId = GetClassIdType(asset);
        if (classId == null || classId != options.OnlyType)
        {
            return false;
        }

        if (classId == ClassIDType.Animator && !IsFbxRuntimeAvailable())
        {
            return false;
        }

        return true;
    }

    private static bool IsSupportedType(Object asset)
    {
        return asset switch
        {
            Texture2D => true,
            AudioClip => true,
            VideoClip => true,
            Shader => true,
            Mesh => true,
            TextAsset => true,
            MonoBehaviour => true,
            Font => true,
            MovieTexture => true,
            Sprite => true,
            Animator => true,
            _ => false
        };
    }

    private static ClassIDType? GetClassIdType(Object asset)
    {
        return asset switch
        {
            Texture2D => ClassIDType.Texture2D,
            AudioClip => ClassIDType.AudioClip,
            VideoClip => ClassIDType.VideoClip,
            Shader => ClassIDType.Shader,
            Mesh => ClassIDType.Mesh,
            TextAsset => ClassIDType.TextAsset,
            MonoBehaviour => ClassIDType.MonoBehaviour,
            Font => ClassIDType.Font,
            MovieTexture => ClassIDType.MovieTexture,
            Sprite => ClassIDType.Sprite,
            Animator => ClassIDType.Animator,
            _ => null
        };
    }

    private static string GetDisplayName(Object asset, int index)
    {
        string? name = null;
        switch (asset)
        {
            case Texture2D texture2D:
                name = texture2D.m_Name;
                break;
            case AudioClip audioClip:
                name = audioClip.m_Name;
                break;
            case VideoClip videoClip:
                name = videoClip.m_Name;
                break;
            case Shader shader:
                name = shader.m_ParsedForm?.m_Name ?? shader.m_Name;
                break;
            case Mesh mesh:
                name = mesh.m_Name;
                break;
            case TextAsset textAsset:
                name = textAsset.m_Name;
                break;
            case AnimationClip animationClip:
                name = animationClip.m_Name;
                break;
            case Font font:
                name = font.m_Name;
                break;
            case MovieTexture movieTexture:
                name = movieTexture.m_Name;
                break;
            case Sprite sprite:
                name = sprite.m_Name;
                break;
            case Animator animator:
                if (animator.m_GameObject.TryGet(out var go))
                {
                    name = go.m_Name;
                }
                break;
            case MonoBehaviour monoBehaviour:
                if (string.IsNullOrWhiteSpace(monoBehaviour.m_Name) && monoBehaviour.m_Script.TryGet(out var script))
                {
                    name = script.m_ClassName;
                }
                else
                {
                    name = monoBehaviour.m_Name;
                }
                break;
            case NamedObject namedObject:
                name = namedObject.m_Name;
                break;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"{asset.type}_{index}";
        }

        return SanitizeFileName(name);
    }

    private bool ExportConvertFile(ExportItem item, ExportSummary summary, AssemblyLoader assemblyLoader, ExportOptions options)
    {
        var exportPath = Path.Combine(summary.OutputDirectory, item.TypeString);
        return item.Asset switch
        {
            Texture2D texture2D => ExportTexture2D(item, texture2D, exportPath, summary, options),
            AudioClip audioClip => ExportAudioClip(item, audioClip, exportPath, summary, options),
            Shader shader => ExportShader(item, shader, exportPath, summary),
            TextAsset textAsset => ExportTextAsset(item, textAsset, exportPath, summary),
            MonoBehaviour monoBehaviour => ExportMonoBehaviour(item, monoBehaviour, exportPath, assemblyLoader, summary),
            Font font => ExportFont(item, font, exportPath, summary),
            Mesh mesh => ExportMesh(item, mesh, exportPath, summary),
            VideoClip videoClip => ExportVideoClip(item, videoClip, exportPath, summary),
            MovieTexture movieTexture => ExportMovieTexture(item, movieTexture, exportPath, summary),
            Sprite sprite => ExportSprite(item, sprite, exportPath, summary, options),
            Animator animator => ExportAnimator(item, animator, exportPath, summary, options),
            AnimationClip => false,
            _ => ExportRawFile(item, exportPath, summary)
        };
    }

    private static bool ExportRawFile(ExportItem item, string exportPath, ExportSummary summary)
    {
        if (!TryExportFile(exportPath, item, ".dat", out var fullPath))
        {
            return false;
        }

        var raw = item.Asset.GetRawData();
        if (raw == null || raw.Length == 0)
        {
            return false;
        }

        EnsureParentDirectory(fullPath);
        File.WriteAllBytes(fullPath, raw);
        summary.ExportedRawObjects++;
        return true;
    }

    private static bool ExportTexture2D(ExportItem item, Texture2D texture2D, string exportPath, ExportSummary summary, ExportOptions options)
    {
        var imageExtension = GetImageFileExtension(options.ImageFormat);
        if (TryExportFile(exportPath, item, imageExtension, out var imagePath))
        {
            try
            {
                using var stream = texture2D.ConvertToStream(options.ImageFormat, true);
                if (stream != null)
                {
                    stream.Position = 0;
                    EnsureParentDirectory(imagePath);
                    using var file = File.Create(imagePath);
                    stream.CopyTo(file);
                    summary.ExportedTextures++;
                    return true;
                }
            }
            catch
            {
                // fallback below
            }
        }

        var raw = texture2D.image_data.GetData();
        if (raw == null || raw.Length == 0)
        {
            return false;
        }

        summary.TextureDecodeFallbacks++;
        var fallbackExt = GuessTextureExtension(item, texture2D, raw);
        var outputData = raw;
        if (TryWrapTextureData(texture2D, raw, out var wrappedExt, out var wrappedData))
        {
            fallbackExt = wrappedExt;
            outputData = wrappedData;
            summary.TextureContainerWraps++;
        }
        else if (NeedsContainerHeader(fallbackExt) && string.IsNullOrEmpty(DetectExtensionFromMagic(raw)))
        {
            // Raw block data without container header should not pretend to be DDS/KTX/PVR/ASTC.
            fallbackExt = ".tex";
        }

        if (!TryExportFile(exportPath, item, fallbackExt, out var texPath))
        {
            return false;
        }

        EnsureParentDirectory(texPath);
        File.WriteAllBytes(texPath, outputData);
        summary.ExportedRawObjects++;
        return true;
    }

    private static bool ExportAudioClip(ExportItem item, AudioClip audioClip, string exportPath, ExportSummary summary, ExportOptions options)
    {
        var data = audioClip.m_AudioData.GetData();
        if (data == null || data.Length == 0)
        {
            return false;
        }

        AudioClipConverter? converter = null;
        try
        {
            converter = new AudioClipConverter(audioClip);
        }
        catch
        {
            // fall back to static extension mapping
        }

        if (options.ConvertAudioToWav && IsFmodRuntimeAvailable())
        {
            try
            {
                if (converter?.IsSupport == true && TryExportFile(exportPath, item, ".wav", out var wavPath))
                {
                    var wavData = converter.ConvertToWav();
                    if (wavData != null && wavData.Length > 0)
                    {
                        EnsureParentDirectory(wavPath);
                        File.WriteAllBytes(wavPath, NormalizeWavForAndroid(wavData));
                        summary.ExportedAudioClips++;
                        return true;
                    }
                }
            }
            catch
            {
                summary.AudioClipFallbacks++;
            }
        }

        if (options.ConvertAudioToWav && TryExportPcmAsWav(item, audioClip, data, exportPath, summary))
        {
            summary.AudioClipFallbacks++;
            return true;
        }

        if (options.ConvertAudioToWav && TryExportFsbAsWav(item, data, exportPath, summary))
        {
            summary.AudioClipFallbacks++;
            return true;
        }

        var extension = GuessAudioExtension(audioClip, data, converter);
        if (!TryExportFile(exportPath, item, extension, out var rawPath))
        {
            return false;
        }

        EnsureParentDirectory(rawPath);
        File.WriteAllBytes(rawPath, data);
        summary.ExportedAudioClips++;
        return true;
    }

    private static bool IsFmodRuntimeAvailable()
    {
        if (s_fmodAvailability == 1)
        {
            return true;
        }
        if (s_fmodAvailability == 0)
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsAndroid())
            {
                // If app bundles libfmod.so, this helps register it before FMOD wrapper initialization.
                TryLoadFmodByName("fmod");
                TryLoadFmodByName("fmodL");
            }

            var result = FMOD.Factory.System_Create(out var system);
            if (result == FMOD.RESULT.OK)
            {
                try
                {
                    system.release();
                }
                catch
                {
                    // ignore
                }
                s_fmodAvailability = 1;
                s_fmodStatus = "system_create=OK";
                return true;
            }

            s_fmodStatus = $"system_create={result}";
        }
        catch (Exception ex)
        {
            s_fmodStatus = ex.GetType().Name;
        }

        s_fmodAvailability = 0;
        return false;
    }

    private static void TryLoadFmodByName(string libraryName)
    {
        try
        {
            NativeLibrary.Load(libraryName);
        }
        catch
        {
            // ignore, we'll continue with regular DllImport probing
        }
    }

    private static void TryLoadFmodFromFolder(string rootFolder)
    {
        if (!OperatingSystem.IsAndroid())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
        {
            return;
        }

        try
        {
            var candidates = Directory.EnumerateFiles(rootFolder, "libfmod*.so", SearchOption.AllDirectories)
                .Take(8)
                .ToArray();

            foreach (var candidate in candidates)
            {
                try
                {
                    NativeLibrary.Load(candidate);
                    s_fmodStatus = $"loaded_from={Path.GetFileName(candidate)}";
                }
                catch
                {
                    // try next candidate
                }
            }
        }
        catch
        {
            // ignore probing failures
        }
    }

    private static bool ExportShader(ExportItem item, Shader shader, string exportPath, ExportSummary summary)
    {
        if (!TryExportFile(exportPath, item, ".shader", out var fullPath))
        {
            return false;
        }

        EnsureParentDirectory(fullPath);
        File.WriteAllText(fullPath, shader.Convert() ?? string.Empty);
        summary.ExportedShaders++;
        return true;
    }

    private static bool ExportTextAsset(ExportItem item, TextAsset textAsset, string exportPath, ExportSummary summary)
    {
        var extension = ".txt";
        if (!string.IsNullOrWhiteSpace(item.Container))
        {
            var containerExt = Path.GetExtension(item.Container);
            if (!string.IsNullOrWhiteSpace(containerExt))
            {
                extension = containerExt;
            }
        }

        if (!TryExportFile(exportPath, item, extension, out var fullPath))
        {
            return false;
        }

        EnsureParentDirectory(fullPath);
        File.WriteAllBytes(fullPath, textAsset.m_Script ?? Array.Empty<byte>());
        summary.ExportedTextAssets++;
        return true;
    }

    private static bool ExportMonoBehaviour(
        ExportItem item,
        MonoBehaviour monoBehaviour,
        string exportPath,
        AssemblyLoader assemblyLoader,
        ExportSummary summary)
    {
        object? monoObject = null;
        try
        {
            monoObject = monoBehaviour.ToType();
        }
        catch
        {
            // fallback below
        }

        if (monoObject == null)
        {
            try
            {
                var typeTree = monoBehaviour.ConvertToTypeTree(assemblyLoader);
                monoObject = monoBehaviour.ToType(typeTree);
                if (monoObject == null)
                {
                    var typedDump = monoBehaviour.Dump(typeTree);
                    if (!string.IsNullOrWhiteSpace(typedDump))
                    {
                        if (!TryExportFile(exportPath, item, ".txt", out var typedDumpPath))
                        {
                            return false;
                        }
                        EnsureParentDirectory(typedDumpPath);
                        File.WriteAllText(typedDumpPath, typedDump);
                        summary.ExportedMonoBehaviours++;
                        return true;
                    }
                }
            }
            catch
            {
                // fallback below
            }
        }

        if (monoObject != null)
        {
            if (!TryExportFile(exportPath, item, ".json", out var jsonPath))
            {
                return false;
            }
            var normalized = NormalizeMonoValue(monoObject);
            var json = JsonSerializer.Serialize(
                normalized,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            EnsureParentDirectory(jsonPath);
            File.WriteAllText(jsonPath, json);
            summary.ExportedMonoBehaviours++;
            return true;
        }

        var dump = monoBehaviour.Dump();
        if (string.IsNullOrWhiteSpace(dump))
        {
            return false;
        }

        if (!TryExportFile(exportPath, item, ".txt", out var fullPath))
        {
            return false;
        }

        EnsureParentDirectory(fullPath);
        File.WriteAllText(fullPath, dump);
        summary.ExportedMonoBehaviours++;
        return true;
    }

    private static object? NormalizeMonoValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is OrderedDictionary orderedDictionary)
        {
            var dict = new Dictionary<string, object?>(orderedDictionary.Count);
            foreach (DictionaryEntry entry in orderedDictionary)
            {
                var key = entry.Key?.ToString() ?? string.Empty;
                dict[key] = NormalizeMonoValue(entry.Value);
            }
            return dict;
        }

        if (value is IDictionary dictionary)
        {
            var dict = new Dictionary<string, object?>(dictionary.Count);
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString() ?? string.Empty;
                dict[key] = NormalizeMonoValue(entry.Value);
            }
            return dict;
        }

        if (value is IEnumerable enumerable && value is not string && value is not byte[])
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(NormalizeMonoValue(item));
            }
            return list;
        }

        return value;
    }

    private static bool ExportFont(ExportItem item, Font font, string exportPath, ExportSummary summary)
    {
        if (font.m_FontData == null || font.m_FontData.Length == 0)
        {
            return false;
        }

        var extension = ".ttf";
        if (font.m_FontData.Length >= 4 &&
            font.m_FontData[0] == 79 &&
            font.m_FontData[1] == 84 &&
            font.m_FontData[2] == 84 &&
            font.m_FontData[3] == 79)
        {
            extension = ".otf";
        }

        if (!TryExportFile(exportPath, item, extension, out var fullPath))
        {
            return false;
        }

        EnsureParentDirectory(fullPath);
        File.WriteAllBytes(fullPath, font.m_FontData);
        summary.ExportedFonts++;
        return true;
    }

    private static bool ExportMesh(ExportItem item, Mesh mesh, string exportPath, ExportSummary summary)
    {
        if (mesh.m_VertexCount <= 0 || mesh.m_Vertices == null || mesh.m_Vertices.Length == 0)
        {
            return false;
        }
        if (mesh.m_Indices == null || mesh.m_SubMeshes == null || mesh.m_SubMeshes.Length == 0)
        {
            return false;
        }
        if (!TryExportFile(exportPath, item, ".obj", out var fullPath))
        {
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("g " + mesh.m_Name);

        var c = mesh.m_Vertices.Length == mesh.m_VertexCount * 4 ? 4 : 3;
        for (var v = 0; v < mesh.m_VertexCount; v++)
        {
            sb.AppendFormat("v {0} {1} {2}\r\n", -mesh.m_Vertices[v * c], mesh.m_Vertices[v * c + 1], mesh.m_Vertices[v * c + 2]);
        }

        if (mesh.m_UV0?.Length > 0)
        {
            c = 4;
            if (mesh.m_UV0.Length == mesh.m_VertexCount * 2)
            {
                c = 2;
            }
            else if (mesh.m_UV0.Length == mesh.m_VertexCount * 3)
            {
                c = 3;
            }
            for (var v = 0; v < mesh.m_VertexCount; v++)
            {
                sb.AppendFormat("vt {0} {1}\r\n", mesh.m_UV0[v * c], mesh.m_UV0[v * c + 1]);
            }
        }

        if (mesh.m_Normals?.Length > 0)
        {
            c = mesh.m_Normals.Length == mesh.m_VertexCount * 4 ? 4 : 3;
            for (var v = 0; v < mesh.m_VertexCount; v++)
            {
                sb.AppendFormat("vn {0} {1} {2}\r\n", -mesh.m_Normals[v * c], mesh.m_Normals[v * c + 1], mesh.m_Normals[v * c + 2]);
            }
        }

        var sum = 0;
        for (var i = 0; i < mesh.m_SubMeshes.Length; i++)
        {
            sb.AppendLine($"g {mesh.m_Name}_{i}");
            var indexCount = (int)mesh.m_SubMeshes[i].indexCount;
            var end = sum + indexCount / 3;
            for (var f = sum; f < end; f++)
            {
                sb.AppendFormat(
                    "f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\r\n",
                    mesh.m_Indices[f * 3 + 2] + 1,
                    mesh.m_Indices[f * 3 + 1] + 1,
                    mesh.m_Indices[f * 3] + 1);
            }
            sum = end;
        }

        sb.Replace("NaN", "0");
        EnsureParentDirectory(fullPath);
        File.WriteAllText(fullPath, sb.ToString());
        summary.ExportedMeshes++;
        return true;
    }

    private static bool ExportVideoClip(ExportItem item, VideoClip videoClip, string exportPath, ExportSummary summary)
    {
        if (videoClip.m_ExternalResources.m_Size <= 0)
        {
            return false;
        }

        var extension = Path.GetExtension(videoClip.m_OriginalPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }

        if (!TryExportFile(exportPath, item, extension, out var fullPath))
        {
            return false;
        }

        EnsureParentDirectory(fullPath);
        videoClip.m_VideoData.WriteData(fullPath);
        summary.ExportedVideos++;
        return true;
    }

    private static bool ExportMovieTexture(ExportItem item, MovieTexture movieTexture, string exportPath, ExportSummary summary)
    {
        if (!TryExportFile(exportPath, item, ".ogv", out var fullPath))
        {
            return false;
        }

        if (movieTexture.m_MovieData == null || movieTexture.m_MovieData.Length == 0)
        {
            return false;
        }

        EnsureParentDirectory(fullPath);
        File.WriteAllBytes(fullPath, movieTexture.m_MovieData);
        summary.ExportedMovieTextures++;
        return true;
    }

    private static bool ExportSprite(ExportItem item, Sprite sprite, string exportPath, ExportSummary summary, ExportOptions options)
    {
        var imageExtension = GetImageFileExtension(options.ImageFormat);
        if (TryExportFile(exportPath, item, imageExtension, out var fullPath))
        {
            try
            {
                var image = sprite.GetImage();
                if (image != null)
                {
                    using (image)
                    {
                        EnsureParentDirectory(fullPath);
                        using var file = File.OpenWrite(fullPath);
                        image.WriteToStream(file, options.ImageFormat);
                    }
                    summary.ExportedSprites++;
                    return true;
                }
            }
            catch
            {
                // fallback below when decoder is unavailable on some Android devices
            }
        }

        if (sprite.m_RD != null &&
            sprite.m_RD.texture != null &&
            sprite.m_RD.texture.TryGet(out var texture2D) &&
            texture2D?.image_data != null)
        {
            var raw = texture2D.image_data.GetData();
            if (raw != null && raw.Length > 0)
            {
                var fallbackExt = GuessTextureExtension(item, texture2D, raw);
                var outputData = raw;
                if (TryWrapTextureData(texture2D, raw, out var wrappedExt, out var wrappedData))
                {
                    fallbackExt = wrappedExt;
                    outputData = wrappedData;
                    summary.TextureContainerWraps++;
                }
                else if (NeedsContainerHeader(fallbackExt) && string.IsNullOrEmpty(DetectExtensionFromMagic(raw)))
                {
                    fallbackExt = ".tex";
                }

                if (!TryExportFile(exportPath, item, fallbackExt, out var texPath))
                {
                    return false;
                }
                EnsureParentDirectory(texPath);
                File.WriteAllBytes(texPath, outputData);
                summary.ExportedRawObjects++;
                return true;
            }
        }

        return false;
    }

    private static bool ExportAnimator(ExportItem item, Animator animator, string exportPath, ExportSummary summary, ExportOptions options)
    {
        if (!IsFbxRuntimeAvailable())
        {
            return false;
        }

        if (!TryExportFile(exportPath, item, ".fbx", out var fullPath))
        {
            return false;
        }

        EnsureParentDirectory(fullPath);
        var converter = new ModelConverter(animator, options.ImageFormat);
        ModelExporter.ExportFbx(
            fullPath,
            converter,
            eulerFilter: true,
            filterPrecision: 0.25f,
            allNodes: true,
            skins: true,
            animation: true,
            blendShape: true,
            castToBone: false,
            boneSize: 10f,
            exportAllUvsAsDiffuseMaps: true,
            scaleFactor: 1f,
            versionIndex: 3,
            isAscii: false);
        summary.ExportedAnimators++;
        return true;
    }

    private static bool IsFbxRuntimeAvailable()
    {
        if (s_fbxAvailability == 1)
        {
            return true;
        }
        if (s_fbxAvailability == 0)
        {
            return false;
        }

        try
        {
            NativeLibrary.Load("AssetStudioFBXNative");
            s_fbxAvailability = 1;
            s_fbxStatus = "native_loaded";
            return true;
        }
        catch (Exception ex)
        {
            s_fbxAvailability = 0;
            s_fbxStatus = ex.GetType().Name;
            return false;
        }
    }

    private static bool TryExportFile(string dir, ExportItem item, string extension, out string fullPath)
    {
        var fileName = SanitizeFileName(item.Name);
        fullPath = Path.Combine(dir, fileName + extension);
        if (!File.Exists(fullPath))
        {
            return true;
        }

        var uniqueId = SanitizeFileName(item.UniqueId);
        fullPath = Path.Combine(dir, fileName + uniqueId + extension);
        if (!File.Exists(fullPath))
        {
            return true;
        }

        return false;
    }

    private static void EnsureParentDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static string GetImageFileExtension(ImageFormat format)
    {
        return format switch
        {
            ImageFormat.Jpeg => ".jpeg",
            ImageFormat.Bmp => ".bmp",
            ImageFormat.Tga => ".tga",
            _ => ".png"
        };
    }

    private static string GuessTextureExtension(ExportItem item, Texture2D texture, byte[] data)
    {
        var fromMagic = DetectExtensionFromMagic(data);
        if (!string.IsNullOrEmpty(fromMagic))
        {
            return fromMagic;
        }

        var fromContainer = GuessTextureExtensionFromContainer(item.Container);
        if (!string.IsNullOrEmpty(fromContainer))
        {
            return fromContainer;
        }

        return TextureFormatToExtension(texture.m_TextureFormat);
    }

    private static string GuessTextureExtensionFromContainer(string? containerPath)
    {
        if (string.IsNullOrWhiteSpace(containerPath))
        {
            return string.Empty;
        }

        var extension = Path.GetExtension(containerPath).ToLowerInvariant();
        return extension switch
        {
            ".png" => ".png",
            ".jpg" => ".jpg",
            ".jpeg" => ".jpeg",
            ".webp" => ".webp",
            ".tga" => ".tga",
            ".bmp" => ".bmp",
            ".gif" => ".gif",
            ".dds" => ".dds",
            ".ktx" => ".ktx",
            ".ktx2" => ".ktx2",
            ".pvr" => ".pvr",
            ".astc" => ".astc",
            ".pkm" => ".pkm",
            ".crn" => ".crn",
            _ => string.Empty
        };
    }

    private static string DetectExtensionFromMagic(byte[] data)
    {
        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return ".png";
        }

        if (data.Length >= 3 &&
            data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ".jpg";
        }

        if (data.Length >= 4 &&
            data[0] == 0x44 && data[1] == 0x44 && data[2] == 0x53 && data[3] == 0x20)
        {
            return ".dds";
        }

        if (data.Length >= 4 &&
            data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x4D && data[3] == 0x20)
        {
            return ".pkm";
        }

        if (data.Length >= 4 &&
            data[0] == 0x13 && data[1] == 0xAB && data[2] == 0xA1 && data[3] == 0x5C)
        {
            return ".astc";
        }

        if (data.Length >= 12 &&
            data[0] == 0xAB && data[1] == 0x4B && data[2] == 0x54 && data[3] == 0x58 &&
            data[4] == 0x20 && data[5] == 0x31 && data[6] == 0x31 && data[7] == 0xBB &&
            data[8] == 0x0D && data[9] == 0x0A && data[10] == 0x1A && data[11] == 0x0A)
        {
            return ".ktx";
        }

        if (data.Length >= 12 &&
            data[0] == 0xAB && data[1] == 0x4B && data[2] == 0x54 && data[3] == 0x58 &&
            data[4] == 0x20 && data[5] == 0x32 && data[6] == 0x30 && data[7] == 0xBB &&
            data[8] == 0x0D && data[9] == 0x0A && data[10] == 0x1A && data[11] == 0x0A)
        {
            return ".ktx2";
        }

        if (data.Length >= 4 &&
            data[0] == 0x50 && data[1] == 0x56 && data[2] == 0x52 && data[3] == 0x03)
        {
            return ".pvr";
        }

        if (data.Length >= 4 &&
            data[0] == 0x50 && data[1] == 0x56 && data[2] == 0x52 && data[3] == 0x21)
        {
            return ".pvr";
        }

        if (data.Length >= 12 &&
            data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
            data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return ".webp";
        }

        if (data.Length >= 4 &&
            data[0] == 0x4F && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53)
        {
            return ".ogg";
        }

        if (data.Length >= 12 &&
            data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
            data[8] == 0x57 && data[9] == 0x41 && data[10] == 0x56 && data[11] == 0x45)
        {
            return ".wav";
        }

        return string.Empty;
    }

    private static bool NeedsContainerHeader(string extension)
    {
        return extension is ".dds" or ".ktx" or ".ktx2" or ".pvr" or ".astc" or ".pkm";
    }

    private static bool TryWrapTextureData(Texture2D texture, byte[] raw, out string extension, out byte[] wrappedData)
    {
        extension = string.Empty;
        wrappedData = raw;

        if (raw == null || raw.Length == 0)
        {
            return false;
        }

        var existing = DetectExtensionFromMagic(raw);
        if (!string.IsNullOrEmpty(existing))
        {
            // Already wrapped in a known container.
            return false;
        }

        var (width, height) = GetEffectiveTextureSize(texture);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        if (TryBuildDds(texture.m_TextureFormat, width, height, raw, out var dds))
        {
            extension = ".dds";
            wrappedData = dds;
            return true;
        }

        if (TryBuildAstc(texture.m_TextureFormat, width, height, raw, out var astc))
        {
            extension = ".astc";
            wrappedData = astc;
            return true;
        }

        return false;
    }

    private static (int Width, int Height) GetEffectiveTextureSize(Texture2D texture)
    {
        try
        {
            var converter = new Texture2DConverter(texture);
            if (converter.Width > 0 && converter.Height > 0)
            {
                return (converter.Width, converter.Height);
            }
        }
        catch
        {
            // fallback below
        }

        return (Math.Max(1, texture.m_Width), Math.Max(1, texture.m_Height));
    }

    private static bool TryBuildAstc(TextureFormat format, int width, int height, byte[] raw, out byte[] astc)
    {
        astc = raw;

        var blockSize = format switch
        {
            TextureFormat.ASTC_RGB_4x4 or TextureFormat.ASTC_RGBA_4x4 or TextureFormat.ASTC_HDR_4x4 => (4, 4),
            TextureFormat.ASTC_RGB_5x5 or TextureFormat.ASTC_RGBA_5x5 or TextureFormat.ASTC_HDR_5x5 => (5, 5),
            TextureFormat.ASTC_RGB_6x6 or TextureFormat.ASTC_RGBA_6x6 or TextureFormat.ASTC_HDR_6x6 => (6, 6),
            TextureFormat.ASTC_RGB_8x8 or TextureFormat.ASTC_RGBA_8x8 or TextureFormat.ASTC_HDR_8x8 => (8, 8),
            TextureFormat.ASTC_RGB_10x10 or TextureFormat.ASTC_RGBA_10x10 or TextureFormat.ASTC_HDR_10x10 => (10, 10),
            TextureFormat.ASTC_RGB_12x12 or TextureFormat.ASTC_RGBA_12x12 or TextureFormat.ASTC_HDR_12x12 => (12, 12),
            _ => (0, 0)
        };

        if (blockSize == (0, 0))
        {
            return false;
        }

        // ASTC file header is 16 bytes.
        var output = new byte[16 + raw.Length];
        output[0] = 0x13;
        output[1] = 0xAB;
        output[2] = 0xA1;
        output[3] = 0x5C;
        output[4] = (byte)blockSize.Item1;
        output[5] = (byte)blockSize.Item2;
        output[6] = 1; // blockdim z

        Write24(output, 7, width);
        Write24(output, 10, height);
        Write24(output, 13, 1); // zsize

        Buffer.BlockCopy(raw, 0, output, 16, raw.Length);
        astc = output;
        return true;
    }

    private static void Write24(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value & 0xFF);
        target[offset + 1] = (byte)((value >> 8) & 0xFF);
        target[offset + 2] = (byte)((value >> 16) & 0xFF);
    }

    private static bool TryBuildDds(TextureFormat format, int width, int height, byte[] raw, out byte[] dds)
    {
        dds = raw;
        var hasDx10Header = false;
        var fourCC = 0u;
        var dxgiFormat = 0u;

        switch (format)
        {
            case TextureFormat.DXT1:
                fourCC = FourCC("DXT1");
                break;
            case TextureFormat.DXT5:
                fourCC = FourCC("DXT5");
                break;
            case TextureFormat.BC4:
                fourCC = FourCC("ATI1");
                break;
            case TextureFormat.BC5:
                fourCC = FourCC("ATI2");
                break;
            case TextureFormat.BC6H:
                fourCC = FourCC("DX10");
                dxgiFormat = 95; // DXGI_FORMAT_BC6H_UF16
                hasDx10Header = true;
                break;
            case TextureFormat.BC7:
                fourCC = FourCC("DX10");
                dxgiFormat = 98; // DXGI_FORMAT_BC7_UNORM
                hasDx10Header = true;
                break;
            default:
                return false;
        }

        using var ms = new MemoryStream(raw.Length + (hasDx10Header ? 148 : 128));
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);

        // Magic: "DDS "
        bw.Write(0x20534444u);

        // DDS_HEADER
        bw.Write(124u); // size
        bw.Write(0x00081007u); // flags: caps|height|width|pixelformat|linearsize
        bw.Write((uint)height);
        bw.Write((uint)width);
        bw.Write((uint)raw.Length); // pitch or linear size
        bw.Write(0u); // depth
        bw.Write(0u); // mipmap count (single level)

        for (var i = 0; i < 11; i++)
        {
            bw.Write(0u); // reserved1
        }

        // DDS_PIXELFORMAT
        bw.Write(32u); // size
        bw.Write(0x00000004u); // flags: fourCC
        bw.Write(fourCC);
        bw.Write(0u); // RGBBitCount
        bw.Write(0u); // RBitMask
        bw.Write(0u); // GBitMask
        bw.Write(0u); // BBitMask
        bw.Write(0u); // ABitMask

        bw.Write(0x00001000u); // caps: texture
        bw.Write(0u); // caps2
        bw.Write(0u); // caps3
        bw.Write(0u); // caps4
        bw.Write(0u); // reserved2

        if (hasDx10Header)
        {
            // DDS_HEADER_DXT10
            bw.Write(dxgiFormat);
            bw.Write(3u); // D3D10_RESOURCE_DIMENSION_TEXTURE2D
            bw.Write(0u); // miscFlag
            bw.Write(1u); // arraySize
            bw.Write(0u); // miscFlags2
        }

        bw.Write(raw);
        bw.Flush();
        dds = ms.ToArray();
        return true;
    }

    private static uint FourCC(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 4)
        {
            return 0u;
        }

        return (uint)(byte)value[0]
            | ((uint)(byte)value[1] << 8)
            | ((uint)(byte)value[2] << 16)
            | ((uint)(byte)value[3] << 24);
    }

    private static bool TryExportFsbAsWav(
        ExportItem item,
        byte[] data,
        string exportPath,
        ExportSummary summary)
    {
        if (!LooksLikeFsb(data))
        {
            return false;
        }

        try
        {
            var soundBank = FsbLoader.LoadFsbFromByteArray(data);
            if (soundBank.Samples.Count <= 0)
            {
                return false;
            }

            var sample = soundBank.Samples[0];
            if (!sample.RebuildAsStandardFileFormat(out var rebuilt, out var extension) ||
                rebuilt == null ||
                rebuilt.Length == 0)
            {
                return false;
            }

            var normalizedExtension = NormalizeExtension(extension);
            byte[] wavData;

            if (normalizedExtension == ".wav")
            {
                wavData = NormalizeWavForAndroid(rebuilt);
            }
            else if (normalizedExtension == ".ogg")
            {
                if (!TryConvertOggToWav(rebuilt, out wavData))
                {
                    return false;
                }
                wavData = NormalizeWavForAndroid(wavData);
            }
            else
            {
                return false;
            }

            if (!TryExportFile(exportPath, item, ".wav", out var wavPath))
            {
                return false;
            }

            EnsureParentDirectory(wavPath);
            File.WriteAllBytes(wavPath, wavData);
            summary.AudioFsbWavConversions++;
            summary.ExportedAudioClips++;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeFsb(byte[] data)
    {
        return data.Length >= 4 &&
               data[0] == (byte)'F' &&
               data[1] == (byte)'S' &&
               data[2] == (byte)'B' &&
               (data[3] == (byte)'3' || data[3] == (byte)'4' || data[3] == (byte)'5');
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var trimmed = extension.Trim();
        if (trimmed[0] != '.')
        {
            trimmed = "." + trimmed;
        }
        return trimmed.ToLowerInvariant();
    }

    private static bool TryConvertOggToWav(byte[] oggData, out byte[] wavData)
    {
        wavData = Array.Empty<byte>();
        if (oggData.Length == 0)
        {
            return false;
        }

        try
        {
            using var oggStream = new MemoryStream(oggData, writable: false);
            using var vorbis = new VorbisReader(oggStream, closeOnDispose: false);

            if (vorbis.Channels <= 0 || vorbis.SampleRate <= 0)
            {
                return false;
            }

            using var outStream = new MemoryStream(oggData.Length * 3);
            using var writer = new BinaryWriter(outStream, Encoding.ASCII, leaveOpen: true);

            // Placeholder RIFF/WAV header; sizes are patched after writing all PCM data.
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(0);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)vorbis.Channels);
            writer.Write(vorbis.SampleRate);
            const short bitsPerSample = 16;
            var blockAlign = (short)(vorbis.Channels * bitsPerSample / 8);
            writer.Write(vorbis.SampleRate * blockAlign);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(0);

            var dataStart = outStream.Position;
            var sampleBuffer = new float[8192 * vorbis.Channels];
            var pcmBuffer = new byte[sampleBuffer.Length * 2];

            while (true)
            {
                var samplesRead = vorbis.ReadSamples(sampleBuffer, 0, sampleBuffer.Length);
                if (samplesRead <= 0)
                {
                    break;
                }

                for (var i = 0; i < samplesRead; i++)
                {
                    var clamped = Math.Clamp(sampleBuffer[i], -1f, 1f);
                    var pcm = (short)Math.Round(clamped * short.MaxValue);
                    var offset = i * 2;
                    pcmBuffer[offset] = (byte)(pcm & 0xFF);
                    pcmBuffer[offset + 1] = (byte)((pcm >> 8) & 0xFF);
                }

                outStream.Write(pcmBuffer, 0, samplesRead * 2);
            }

            var dataSize = (int)(outStream.Length - dataStart);
            outStream.Position = 4;
            writer.Write((int)(outStream.Length - 8));
            outStream.Position = 40;
            writer.Write(dataSize);
            writer.Flush();

            wavData = outStream.ToArray();
            return wavData.Length > 44;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] NormalizeWavForAndroid(byte[] wavData)
    {
        if (!LooksLikeWav(wavData))
        {
            return wavData;
        }

        try
        {
            var normalized = wavData.ToArray();
            if (normalized.Length < 44)
            {
                return wavData;
            }

            var riffSize = ReadUInt32LE(normalized, 4);
            var position = 12;
            var dataSizeFieldOffset = -1;
            var dataStartOffset = -1;
            uint dataSize = 0;

            while (position + 8 <= normalized.Length)
            {
                var chunkId = Encoding.ASCII.GetString(normalized, position, 4);
                var chunkSize = ReadUInt32LE(normalized, position + 4);
                var chunkDataStart = position + 8;

                if (chunkId == "data")
                {
                    dataSizeFieldOffset = position + 4;
                    dataStartOffset = chunkDataStart;
                    dataSize = chunkSize;
                    break;
                }

                var next = (long)chunkDataStart + chunkSize + ((chunkSize & 1u) == 1u ? 1 : 0);
                if (next <= position || next > normalized.Length)
                {
                    break;
                }

                position = (int)next;
            }

            if (dataSizeFieldOffset < 0 || dataStartOffset < 0 || dataStartOffset > normalized.Length)
            {
                return wavData;
            }

            var remainingData = normalized.Length - dataStartOffset;
            if (remainingData < 0)
            {
                return wavData;
            }

            var expectedRiffSize = (uint)Math.Max(0, normalized.Length - 8);
            var invalidRiffSize = riffSize == 0 || riffSize > expectedRiffSize;
            var invalidDataSize = dataSize == 0 || dataSize > (uint)remainingData;

            if (!invalidRiffSize && !invalidDataSize)
            {
                return wavData;
            }

            WriteUInt32LE(normalized, 4, expectedRiffSize);
            WriteUInt32LE(normalized, dataSizeFieldOffset, (uint)remainingData);
            return normalized;
        }
        catch
        {
            return wavData;
        }
    }

    private static bool LooksLikeWav(byte[] data)
    {
        return data.Length >= 12 &&
               data[0] == (byte)'R' &&
               data[1] == (byte)'I' &&
               data[2] == (byte)'F' &&
               data[3] == (byte)'F' &&
               data[8] == (byte)'W' &&
               data[9] == (byte)'A' &&
               data[10] == (byte)'V' &&
               data[11] == (byte)'E';
    }

    private static uint ReadUInt32LE(byte[] data, int offset)
    {
        return (uint)(data[offset]
                      | (data[offset + 1] << 8)
                      | (data[offset + 2] << 16)
                      | (data[offset + 3] << 24));
    }

    private static void WriteUInt32LE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
        data[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static bool TryExportPcmAsWav(
        ExportItem item,
        AudioClip audioClip,
        byte[] data,
        string exportPath,
        ExportSummary summary)
    {
        if (audioClip.version[0] < 5 || audioClip.m_CompressionFormat != AudioCompressionFormat.PCM)
        {
            return false;
        }

        if (data.Length < 4)
        {
            return false;
        }

        var detected = DetectAudioExtensionFromMagic(data);
        if (!string.IsNullOrEmpty(detected))
        {
            // Already wrapped in known audio container.
            return false;
        }

        var channels = audioClip.m_Channels;
        var sampleRate = audioClip.m_Frequency;
        var bitsPerSample = audioClip.m_BitsPerSample;
        if (channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0 || bitsPerSample % 8 != 0)
        {
            return false;
        }

        if (bitsPerSample != 8 && bitsPerSample != 16 && bitsPerSample != 24)
        {
            return false;
        }

        var blockAlign = channels * bitsPerSample / 8;
        if (blockAlign <= 0)
        {
            return false;
        }

        var alignedLength = data.Length - (data.Length % blockAlign);
        if (alignedLength < blockAlign * 16)
        {
            return false;
        }

        var pcm = data;
        if (alignedLength != data.Length)
        {
            pcm = new byte[alignedLength];
            Buffer.BlockCopy(data, 0, pcm, 0, alignedLength);
        }

        if (audioClip.m_Length > 0)
        {
            var expectedBytes = (long)Math.Round(audioClip.m_Length * sampleRate * blockAlign);
            if (expectedBytes > 0)
            {
                var delta = Math.Abs(expectedBytes - pcm.LongLength);
                var tolerance = Math.Max(blockAlign * 16L, (long)(expectedBytes * 0.02f));
                if (delta > tolerance)
                {
                    return false;
                }
            }
        }

        if (!TryExportFile(exportPath, item, ".wav", out var wavPath))
        {
            return false;
        }

        EnsureParentDirectory(wavPath);
        var wav = NormalizeWavForAndroid(BuildWavFromPcmData(pcm, channels, sampleRate, bitsPerSample));
        File.WriteAllBytes(wavPath, wav);
        summary.AudioPcmWavConversions++;
        summary.ExportedAudioClips++;
        return true;
    }

    private static byte[] BuildWavFromPcmData(byte[] pcmData, int channels, int sampleRate, int bitsPerSample)
    {
        using var ms = new MemoryStream(pcmData.Length + 44);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, true);

        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var subchunk2Size = pcmData.Length;
        var chunkSize = 36 + subchunk2Size;

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(chunkSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // PCM header size
        bw.Write((short)1); // PCM format
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(subchunk2Size);
        bw.Write(pcmData);
        bw.Flush();

        return ms.ToArray();
    }

    private static string GuessAudioExtension(AudioClip audioClip, byte[] data, AudioClipConverter? converter)
    {
        var fromMagic = DetectAudioExtensionFromMagic(data);
        if (!string.IsNullOrEmpty(fromMagic))
        {
            return fromMagic;
        }

        if (converter != null)
        {
            try
            {
                var ext = converter.GetExtensionName();
                if (!string.IsNullOrWhiteSpace(ext))
                {
                    return ext;
                }
            }
            catch
            {
                // ignored
            }
        }

        if (audioClip.version[0] < 5)
        {
            return audioClip.m_Type switch
            {
                FMODSoundType.ACC => ".m4a",
                FMODSoundType.AIFF => ".aif",
                FMODSoundType.FLAC => ".flac",
                FMODSoundType.IT => ".it",
                FMODSoundType.MOD => ".mod",
                FMODSoundType.MPEG => ".mp3",
                FMODSoundType.OGGVORBIS => ".ogg",
                FMODSoundType.S3M => ".s3m",
                FMODSoundType.WAV => ".wav",
                FMODSoundType.XM => ".xm",
                FMODSoundType.XMA => ".wav",
                FMODSoundType.VAG => ".vag",
                FMODSoundType.AUDIOQUEUE or FMODSoundType.FSB => ".fsb",
                FMODSoundType.VORBIS => ".ogg",
                FMODSoundType.AT9 => ".at9",
                _ => ".fsb"
            };
        }

        return audioClip.m_CompressionFormat switch
        {
            AudioCompressionFormat.AAC => ".m4a",
            AudioCompressionFormat.PCM => ".fsb",
            AudioCompressionFormat.Vorbis => ".fsb",
            AudioCompressionFormat.ADPCM => ".fsb",
            AudioCompressionFormat.MP3 => ".fsb",
            AudioCompressionFormat.PSMVAG => ".fsb",
            AudioCompressionFormat.HEVAG => ".fsb",
            AudioCompressionFormat.XMA => ".fsb",
            AudioCompressionFormat.GCADPCM => ".fsb",
            AudioCompressionFormat.ATRAC9 => ".fsb",
            _ => ".AudioClip"
        };
    }

    private static string DetectAudioExtensionFromMagic(byte[] data)
    {
        if (data.Length >= 4 &&
            data[0] == 0x4F && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53)
        {
            return ".ogg";
        }

        if (data.Length >= 4 &&
            data[0] == 0x66 && data[1] == 0x4C && data[2] == 0x61 && data[3] == 0x43)
        {
            return ".flac";
        }

        if (data.Length >= 12 &&
            data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
            data[8] == 0x57 && data[9] == 0x41 && data[10] == 0x56 && data[11] == 0x45)
        {
            return ".wav";
        }

        if (data.Length >= 3 &&
            data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33)
        {
            return ".mp3";
        }

        if (data.Length >= 2 &&
            data[0] == 0xFF &&
            (data[1] & 0xE0) == 0xE0)
        {
            if ((data[1] & 0x16) == 0x10)
            {
                return ".m4a";
            }
            return ".mp3";
        }

        if (data.Length >= 8 &&
            data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70)
        {
            return ".m4a";
        }

        if (data.Length >= 4 &&
            data[0] == 0x46 && data[1] == 0x53 && data[2] == 0x42 &&
            (data[3] == 0x34 || data[3] == 0x35))
        {
            return ".fsb";
        }

        return string.Empty;
    }

    private static string TextureFormatToExtension(TextureFormat format)
    {
        return format switch
        {
            TextureFormat.DXT1 or TextureFormat.DXT3 or TextureFormat.DXT5 or
            TextureFormat.BC4 or TextureFormat.BC5 or TextureFormat.BC6H or TextureFormat.BC7 => ".dds",
            TextureFormat.DXT1Crunched or TextureFormat.DXT5Crunched or
            TextureFormat.ETC_RGB4Crunched or TextureFormat.ETC2_RGBA8Crunched => ".crn",
            TextureFormat.PVRTC_RGB2 or TextureFormat.PVRTC_RGBA2 or
            TextureFormat.PVRTC_RGB4 or TextureFormat.PVRTC_RGBA4 => ".pvr",
            TextureFormat.ASTC_RGB_4x4 or TextureFormat.ASTC_RGB_5x5 or TextureFormat.ASTC_RGB_6x6 or
            TextureFormat.ASTC_RGB_8x8 or TextureFormat.ASTC_RGB_10x10 or TextureFormat.ASTC_RGB_12x12 or
            TextureFormat.ASTC_RGBA_4x4 or TextureFormat.ASTC_RGBA_5x5 or TextureFormat.ASTC_RGBA_6x6 or
            TextureFormat.ASTC_RGBA_8x8 or TextureFormat.ASTC_RGBA_10x10 or TextureFormat.ASTC_RGBA_12x12 or
            TextureFormat.ASTC_HDR_4x4 or TextureFormat.ASTC_HDR_5x5 or TextureFormat.ASTC_HDR_6x6 or
            TextureFormat.ASTC_HDR_8x8 or TextureFormat.ASTC_HDR_10x10 or TextureFormat.ASTC_HDR_12x12 => ".astc",
            TextureFormat.ETC_RGB4 or TextureFormat.ETC2_RGB or TextureFormat.ETC2_RGBA1 or TextureFormat.ETC2_RGBA8 or
            TextureFormat.EAC_R or TextureFormat.EAC_R_SIGNED or TextureFormat.EAC_RG or TextureFormat.EAC_RG_SIGNED => ".ktx",
            _ => ".tex"
        };
    }

    private string CreateOutputDirectory(string sessionId)
    {
        var root = Path.Combine("/storage/emulated/0/Android/media", _context.PackageName!, "decoded");
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
        }

        if (!Directory.Exists(root))
        {
            root = Path.Combine(_context.FilesDir!.AbsolutePath!, "decoded");
            Directory.CreateDirectory(root);
        }

        var output = Path.Combine(root, sessionId);
        Directory.CreateDirectory(output);
        return output;
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unnamed";
        }
        if (value.Length >= 240)
        {
            return Path.GetRandomFileName();
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }
        return builder.ToString();
    }
}

internal sealed class ExportItem
{
    public ExportItem(Object asset)
    {
        Asset = asset;
    }

    public Object Asset { get; }
    public string Name { get; set; } = string.Empty;
    public string UniqueId { get; set; } = string.Empty;
    public string? Container { get; set; }
    public string TypeString => Asset.type.ToString();
}

public sealed class ExportOptions
{
    // null means export all supported types
    public ClassIDType? OnlyType { get; set; }
    public ImageFormat ImageFormat { get; set; } = ImageFormat.Png;
    public bool ConvertAudioToWav { get; set; } = true;
}

public sealed class ExportSummary
{
    public int CopiedFiles { get; set; }
    public int AssetsFilesLoaded { get; set; }
    public int ObjectsScanned { get; set; }
    public int ExportedObjects { get; set; }
    public int SkippedObjects { get; set; }
    public int ExportedTextures { get; set; }
    public int ExportedTextAssets { get; set; }
    public int ExportedAudioClips { get; set; }
    public int ExportedShaders { get; set; }
    public int ExportedFonts { get; set; }
    public int ExportedMeshes { get; set; }
    public int ExportedVideos { get; set; }
    public int ExportedMovieTextures { get; set; }
    public int ExportedSprites { get; set; }
    public int ExportedAnimators { get; set; }
    public int ExportedMonoBehaviours { get; set; }
    public int ExportedRawObjects { get; set; }
    public int TextureDecodeFallbacks { get; set; }
    public int TextureContainerWraps { get; set; }
    public int AudioClipFallbacks { get; set; }
    public int AudioPcmWavConversions { get; set; }
    public int AudioFsbWavConversions { get; set; }
    public int FailedObjects { get; set; }
    public int SuppressedLoadObjectErrors { get; set; }
    public int SuppressedReadTypeInfo { get; set; }
    public int SuppressedLoadingInfo { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
    public string WorkingInputDirectory { get; set; } = string.Empty;
    public string LogFilePath { get; set; } = string.Empty;
}

internal sealed class CallbackLogger : ILogger
{
    private readonly Action<string> _log;
    private readonly ExportSummary _summary;
    private int _suppressedLoadingInfo;
    private DateTime _lastLoadingInfoFlushUtc = DateTime.MinValue;

    public CallbackLogger(Action<string> log, ExportSummary summary)
    {
        _log = log;
        _summary = summary;
    }

    public void Log(LoggerEvent loggerEvent, string message)
    {
        if (loggerEvent == LoggerEvent.Info &&
            message.StartsWith("Loading ", StringComparison.OrdinalIgnoreCase))
        {
            _suppressedLoadingInfo++;
            _summary.SuppressedLoadingInfo++;

            // Emit a compact heartbeat instead of spamming one line per file.
            var now = DateTime.UtcNow;
            if (_suppressedLoadingInfo % 250 == 0 ||
                (now - _lastLoadingInfoFlushUtc).TotalSeconds >= 5)
            {
                _lastLoadingInfoFlushUtc = now;
                _log($"Info: Loading assets files... {_suppressedLoadingInfo} files processed");
            }
            return;
        }

        if (loggerEvent == LoggerEvent.Error &&
            message.Contains("Unable to load object", StringComparison.OrdinalIgnoreCase))
        {
            _summary.SuppressedLoadObjectErrors++;
            return;
        }

        if (loggerEvent == LoggerEvent.Info &&
            message.Contains("Error while read type", StringComparison.OrdinalIgnoreCase))
        {
            _summary.SuppressedReadTypeInfo++;
            return;
        }

        _log($"{loggerEvent}: {message}");
    }
}
