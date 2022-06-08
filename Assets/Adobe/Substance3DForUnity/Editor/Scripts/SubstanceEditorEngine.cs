using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEditor.PackageManager;

using Adobe.Substance.Input;
using Adobe.Substance.Editor.Importer;
using Adobe.Substance.Editor.ProjectSettings;

namespace Adobe.Substance.Editor
{
    /// <summary>
    /// Editor Singleton to manage interactions with the Substance engine.
    /// </summary>
    internal sealed class SubstanceEditorEngine : ScriptableSingleton<SubstanceEditorEngine>
    {
        /// <summary>
        /// Substance files currently loaded in the engine.
        /// </summary>
        private readonly Dictionary<string, SubstanceNativeHandler> _activeSubstanceDictionary = new Dictionary<string, SubstanceNativeHandler>();

        /// <summary>
        /// Currently active instances.
        /// </summary>
        private readonly List<SubstanceGraphSO> _managedInstances = new List<SubstanceGraphSO>();

        private readonly Queue<string> _delayiedInitilization = new Queue<string>();

        /// <summary>
        /// Render results generated by the substance engine in a background thread.
        /// </summary>
        private readonly ConcurrentQueue<RenderResult> _renderResultsQueue = new ConcurrentQueue<RenderResult>();

        private readonly List<SubstanceGraphSO> _playmodeObjects = new List<SubstanceGraphSO>();

        /// <summary>
        /// Initializer to ensure SubstanceEditorEngine is
        /// started consistently on editor load and assembly reload.
        ///</summary>
        [InitializeOnLoad]
        private sealed class SubstanceEditorEngineInitializer
        {
            static SubstanceEditorEngineInitializer()
            {
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            }

            private static void OnBeforeAssemblyReload()
            {
                SubstanceEditorEngine.instance.TearDown();
            }

            private static void OnAfterAssemblyReload()
            {
                SubstanceEditorEngine.instance.Setup();
            }
        }

        private bool _isLoaded;

        /// <summary>
        /// Initalize substance engine.
        /// </summary>
        private void Setup()
        {
            PluginPipelines.GetCurrentPipelineInUse();
            var enginePath = PlatformUtils.GetEnginePath();
            var pluginPath = PlatformUtils.GetPluginPath();
            Engine.Initialize(pluginPath, enginePath);
            EditorApplication.update += Update;
            _isLoaded = false;
        }

        /// <summary>
        /// Shutdown substance engine.
        /// </summary>
        private void TearDown()
        {
            EditorApplication.update -= Update;
            Engine.Shutdown();
        }

        #region Update

        /// <summary>
        /// Editor update.
        /// </summary>
        private void Update()
        {
            if (!_isLoaded)
            {
                LoadAllSbsarFiles();
                _isLoaded = true;
            }

            _managedInstances.RemoveAll(item => item == null);

            HandleDelayedInitialization();
            HandlePlaymode();
            CheckUIUpdate();
            CheckRenderResultsUpdates();
        }

        private void HandleDelayedInitialization()
        {
            while (_delayiedInitilization.Count != 0)
            {
                var instancePath = _delayiedInitilization.Dequeue();
                var materialInstance = AssetDatabase.LoadAssetAtPath<SubstanceGraphSO>(instancePath);
                _managedInstances.Add(materialInstance);
            }
        }

        private bool _onPlaymodeEnterHandled = false;

        private void HandlePlaymode()
        {
            if (EditorApplication.isPlaying && !_onPlaymodeEnterHandled)
            {
                var runtiemMaterials = GameObject.FindObjectsOfType<Substance.Runtime.SubstanceRuntimeGraph>();

                foreach (var materialInstance in _managedInstances)
                {
                    bool isAssigned = runtiemMaterials.FirstOrDefault(a => a.GraphSO == materialInstance) != null;

                    if (Application.IsPlaying(materialInstance) && (isAssigned || materialInstance.Graph.IsRuntimeOnly))
                    {
                        if (!_playmodeObjects.Contains(materialInstance))
                            _playmodeObjects.Add(materialInstance);
                    }
                }

                _onPlaymodeEnterHandled = true;
            }

            if (!EditorApplication.isPlaying && _onPlaymodeEnterHandled)
            {
                var objectsToRemove = new List<SubstanceGraphSO>();

                foreach (var playmodeObject in _playmodeObjects)
                {
                    if (!Application.IsPlaying(playmodeObject))
                    {
                        playmodeObject.Graph.RenderTextures = true;
                        objectsToRemove.Add(playmodeObject);
                    }
                }

                foreach (var item in objectsToRemove)
                    _playmodeObjects.Remove(item);

                _onPlaymodeEnterHandled = false;
            }
        }

        /// <summary>
        /// Updated the state of the SubstanceFileHandlers based on changes made in the graph objects.
        /// </summary>
        private void CheckUIUpdate()
        {
            foreach (var substanceInstance in _managedInstances)
            {
                if (substanceInstance == null)
                    continue;

                if (substanceInstance.RawData == null)
                {
                    var assets = AssetDatabase.LoadAllAssetsAtPath(substanceInstance.AssetPath);

                    if (assets == null)
                        continue;

                    var dataObject = assets.FirstOrDefault(a => a is SubstanceFileRawData) as SubstanceFileRawData;

                    substanceInstance.RawData = dataObject;
                    EditorUtility.SetDirty(substanceInstance);
                    AssetDatabase.Refresh();
                }

                if (!TryGetHandlerFromInstance(substanceInstance, out SubstanceNativeHandler substanceHandler))
                    continue;

                if (substanceHandler.InRenderWork)
                    continue;

                if (substanceInstance.Graph == null)
                    continue;

                var graph = substanceInstance.Graph;

                if (graph.IsRuntimeOnly && graph.OutputMaterial != null)
                    if (graph.OutputMaterial.GetTexture("_MainTex") == null)
                        MaterialUtils.AssignOutputTexturesToMaterial(graph);

                if (HasMaterialShaderChanged(graph))
                {
                    SubmitAsyncRenderWork(substanceHandler, substanceInstance, true);
                    graph.RenderTextures = true;
                    continue;
                }

                if (graph.OutputRemaped)
                {
                    graph.OutputRemaped = false;

                    if (graph.IsRuntimeOnly)
                    {
                        DeleteGeneratedTextures(graph);
                    }

                    RenderingUtils.UpdateAlphaChannelsAssignment(substanceHandler, graph);
                    SubmitAsyncRenderWork(substanceHandler, substanceInstance, true);
                    graph.RenderTextures = true;
                    continue;
                }

                if (graph.RenderTextures)
                {
                    graph.RenderTextures = false;

                    foreach (var input in graph.Input)
                        input.UpdateNativeHandle(substanceHandler);

                    SubmitAsyncRenderWork(substanceHandler, substanceInstance);
                    graph.RefreshInputVisibility(substanceHandler);

                    EditorUtility.SetDirty(substanceInstance);
                }
            }
        }

        /// <summary>
        /// Updated the render results that are finished by the substance engine
        /// </summary>
        private void CheckRenderResultsUpdates()
        {
            if (_renderResultsQueue.TryDequeue(out RenderResult renderResult))
            {
                SubstanceGraphSO substanceInstance = _managedInstances.FirstOrDefault(a => a.GUID == renderResult.GUID && a.Graph.Index == renderResult.GraphID);

                if (substanceInstance == null)
                    return;

                if (!TryGetHandlerFromInstance(substanceInstance, out SubstanceNativeHandler handler))
                    return;

                var graph = substanceInstance.Graph;

                var textureReassigned = UpdateTextureFromGraphRender(renderResult, graph, handler);

                if (textureReassigned)
                {
                    if (!string.IsNullOrEmpty(graph.FilePath))
                    {
                        AssetCreationUtils.CreateMaterialOrUpdateMaterial(graph, substanceInstance.Name);
                        EditorUtility.SetDirty(substanceInstance);
                        EditorUtility.SetDirty(graph.OutputMaterial);
                        AssetDatabase.Refresh();
                    }
                }
                else
                {
                    EditorUtility.SetDirty(graph.OutputMaterial);
                }

                handler.InRenderWork = false;
            }
        }

        /// <summary>
        /// Checks if the shaders assigned to the substance graph generated material has changed. If so, we have to change the default outputs.
        /// </summary>
        private bool HasMaterialShaderChanged(SubstanceGraph graph)
        {
            if (graph.OutputMaterial == null || string.IsNullOrEmpty(graph.MaterialShader))
                return false;

            if (graph.OutputMaterial.shader.name == graph.MaterialShader)
                return false;

            AssetCreationUtils.UpdateMeterialAssignment(graph);
            return true;
        }

        #endregion Update

        #region Public methods

        #region Instance Management

        internal void InitializeSubstanceFile(string assetPath, out int graphCount, out string guid)
        {
            var substanceArchive = Engine.OpenFile(assetPath);
            graphCount = substanceArchive.GetGraphCount();
            guid = System.Guid.NewGuid().ToString();
            _activeSubstanceDictionary.Add(guid, substanceArchive);
        }

        /// <summary>
        /// Loads a sbsar file into the engine. The engine will keep track of this file internally.
        /// </summary>
        /// <param name="assetPath">Path to a sbsar file.</param>
        public void InitializeInstance(SubstanceGraphSO substanceInstance, string instancePath)
        {
            if (substanceInstance == null)
                return;

            if (string.IsNullOrEmpty(substanceInstance.AssetPath))
                Debug.LogError("Unable to instantiate substance material with null assetPath.");

            if (!_activeSubstanceDictionary.TryGetValue(substanceInstance.GUID, out SubstanceNativeHandler _))
            {
                var substanceArchive = Engine.OpenFile(substanceInstance.AssetPath);
                _activeSubstanceDictionary.Add(substanceInstance.GUID, substanceArchive);
            }

            _delayiedInitilization.Enqueue(instancePath);
        }

        /// <summary>
        /// Unloads the target substance from th e substance engine.
        /// </summary>
        /// <param name="assetPath">Path to a sbsar file.</param>
        public void ReleaseInstance(SubstanceGraphSO substanceInstance)
        {
            if (TryGetHandlerFromInstance(substanceInstance, out SubstanceNativeHandler substanceArchive))
            {
                _activeSubstanceDictionary.Remove(substanceInstance.GUID);
                substanceArchive.Dispose();
            }
        }

        #endregion Instance Management

        /// <summary>
        /// Loads the list of substance graphs from a substance file.
        /// </summary>
        /// <param name="assetPath">Path to the target substance file.</param>
        /// <returns>List of substance graph objects.</returns>
        public SubstanceGraph CreateGraphObject(SubstanceGraphSO instance, int graphID, SubstanceGraphSO copy = null)
        {
            if (!TryGetHandlerFromInstance(instance, out SubstanceNativeHandler substanceHandle))
                return null;

            if (copy != null)
            {
                if (TryGetHandlerFromInstance(copy, out SubstanceNativeHandler copyHandle))
                {
                    var copyPreset = copyHandle.CreatePresetFromCurrentState(copy.Graph.Index);
                    substanceHandle.ApplyPreset(graphID, copyPreset);
                }
            }

            var substanceGraph = new SubstanceGraph(instance, graphID)
            {
                Input = GetGraphInputs(substanceHandle, graphID),
                Output = GetGraphOutputs(substanceHandle, graphID)
            };

            RenderingUtils.ConfigureOutputTextures(substanceHandle, substanceGraph);

            substanceGraph.GenerateAllOutputs = SubstanceEditorSettingsSO.GenerateAllTextures();
            SetOutputTextureSize(substanceGraph, substanceHandle);

            substanceGraph.DefaultPreset = substanceHandle.CreatePresetFromCurrentState(graphID);
            substanceGraph.RefreshInputVisibility(substanceHandle);

            var thumbnailData = substanceHandle.GetThumbnail(graphID);

            if (thumbnailData != null)
            {
                var thumbnail = substanceHandle.GetThumbnail(graphID);

                substanceGraph.Thumbnail = thumbnail;
                substanceGraph.HasThumbnail = true;
            }

            return substanceGraph;
        }

        /// <summary>
        /// Renders a substance file using the substance engine.
        /// </summary>
        /// <param name="assetPath">Path to a sbsar file.</param>
        /// <param name="graphID">Target graph index.</param>
        /// <returns>Task that will be finished once the rendering is finished.</returns>
        public Task RenderInstanceAsync(SubstanceGraphSO instances)
        {
            if (TryGetHandlerFromInstance(instances, out SubstanceNativeHandler substanceArchive))
                return SubmitAsyncRenderWork(substanceArchive, instances);

            return Task.CompletedTask;
        }

        public Task RenderInstanceAsync(IReadOnlyList<SubstanceGraphSO> instances)
        {
            if (TryGetHandlerFromInstance(instances.First(), out SubstanceNativeHandler substanceArchive))
                return SubmitAsyncRenderWork(substanceArchive, instances);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Assigns the substance graph objects inputs to the substance file Handlers associated with them.
        /// </summary>
        /// <param name="assetPath">Path to the sbsar object.</param>
        /// <param name="graphCopy">List of graph objects.</param>
        internal void SetSubstanceInput(SubstanceGraphSO instance)
        {
            if (TryGetHandlerFromInstance(instance, out SubstanceNativeHandler substanceArchive))
            {
                if (instance.Graph == null)
                    return;

                foreach (var input in instance.Graph.Input)
                    input.UpdateNativeHandle(substanceArchive);
            }
        }

        #region Preset

        /// <summary>
        /// Get the preset XML document for the current state of the a managed substance object.
        /// </summary>
        /// <param name="assetPath">Path to the target sbsar file.</param>
        /// <param name="graphID">Target graph id. </param>
        /// <returns>XML document with the current input states as a preset. </returns>
        public string ExportGraphPresetXML(SubstanceGraphSO instance, int graphID)
        {
            if (!TryGetHandlerFromInstance(instance, out SubstanceNativeHandler substanceArchive))
                return null;

            return substanceArchive.CreatePresetFromCurrentState(graphID);
        }

        /// <summary>
        /// Loads the inputs from a preset XML document into the target graph of a managed substance file.
        /// </summary>
        /// <param name="substanceInstancePath">Path to the target sbsar file.</param>
        /// <param name="graphID">Target graph id.</param>
        /// <param name="presetXML">Preset XML document.</param>
        public void LoadPresetsToGraph(SubstanceGraphSO instance, string presetXML)
        {
            if (TryGetHandlerFromInstance(instance, out SubstanceNativeHandler substanceHandler))
            {
                substanceHandler.ApplyPreset(instance.Graph.Index, presetXML);

                var targetGraph = instance.Graph;
                targetGraph.Input = GetGraphInputs(substanceHandler, instance.Graph.Index);
                targetGraph.RenderTextures = true;
                EditorUtility.SetDirty(instance);
            }
        }

        #endregion Preset

        #endregion Public methods

        private bool TryGetHandlerFromInstance(SubstanceGraphSO substanceInstance, out SubstanceNativeHandler substanceHandler)
        {
            substanceHandler = null;

            if (substanceInstance == null)
                return false;

            if (!_activeSubstanceDictionary.TryGetValue(substanceInstance.GUID, out substanceHandler))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Loads all sbsar files currently in the project.
        /// </summary>
        private void LoadAllSbsarFiles()
        {
            string[] files = Directory.GetFiles(Application.dataPath, "*.sbsar", SearchOption.AllDirectories);

            foreach (string filePath in files)
            {
                if (filePath.StartsWith(Application.dataPath))
                {
                    var assetPath = "Assets" + filePath.Substring(Application.dataPath.Length);
                    assetPath = assetPath.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    if (!File.Exists(assetPath))
                        continue;

                    SubstanceImporter importer = AssetImporter.GetAtPath(assetPath) as SubstanceImporter;

                    if (importer == null)
                        continue;

                    foreach (var substanceInstance in importer._instancesCopy)
                    {
                        InitializeInstance(substanceInstance, AssetDatabase.GetAssetPath(substanceInstance));

                        if (TryGetHandlerFromInstance(substanceInstance, out SubstanceNativeHandler fileHandler))
                        {
                            if (substanceInstance.Graph == null)
                                continue;

                            if (substanceInstance.Graph.IsRuntimeOnly)
                            {
                                Debug.Log($"Initalizing graph: {substanceInstance.Name}");
                                substanceInstance.Graph.RuntimeInitialize(fileHandler);
                            }
                            else
                                RenderingUtils.ConfigureOutputTextures(fileHandler, substanceInstance.Graph);
                        }
                    }
                }
            }
        }

        public void RefreshActiveInstances()
        {
            foreach (var substanceInstance in _managedInstances)
            {
                substanceInstance.Graph.RenderTextures = true;
            }
        }

        private List<ISubstanceInput> GetGraphInputs(SubstanceNativeHandler substanceFileHandler, int graphID)
        {
            var inputs = new List<ISubstanceInput>();

            var graphInputCount = substanceFileHandler.GetInputCount(graphID);

            for (int j = 0; j < graphInputCount; j++)
            {
                ISubstanceInput graphInput = substanceFileHandler.GetInputObject(graphID, j);
                inputs.Add(graphInput);
            }

            return inputs;
        }

        private List<SubstanceOutputTexture> GetGraphOutputs(SubstanceNativeHandler substanceFileHandler, int graphID)
        {
            var outputs = new List<SubstanceOutputTexture>();

            var graphOutputCount = substanceFileHandler.GetGraphOutputCount(graphID);

            for (int j = 0; j < graphOutputCount; j++)
            {
                var outputDescription = substanceFileHandler.GetOutputDescription(graphID, j);
                bool isStandard = MaterialUtils.CheckIfStandardOutput(outputDescription);
                SubstanceOutputTexture graphData = new SubstanceOutputTexture(outputDescription, graphID, isStandard);

                if (graphData.IsBaseColor() ||
                    graphData.IsDiffuse() ||
                    graphData.IsSpecular())
                {
                    graphData.sRGB = true;
                }
                else
                {
                    graphData.sRGB = false;
                }

                outputs.Add(graphData);
            }

            var diffuseOutput = outputs.FirstOrDefault(a => a.IsDiffuse());
            var baseColorOutput = outputs.FirstOrDefault(a => a.IsBaseColor());

            if (baseColorOutput == null && diffuseOutput != null)
                diffuseOutput.IsStandardOutput = true;

            return outputs;
        }

        private void SetOutputTextureSize(SubstanceGraph graph, SubstanceNativeHandler substanceFileHandler)
        {
            var outputSize = graph.Input.FirstOrDefault(a => a.Description.Label == "$outputsize");

            if (outputSize == null)
                return;

            if (outputSize is SubstanceInputInt2 outputSizeInput)
            {
                outputSizeInput.Data = SubstanceEditorSettingsSO.TextureOutputResultion();
                outputSizeInput.UpdateNativeHandle(substanceFileHandler);
            }
        }

        #region Rendering

        private Task SubmitAsyncRenderWork(SubstanceNativeHandler substanceArchive, SubstanceGraphSO instanceKey, bool forceRebuild = false)
        {
            if (substanceArchive.InRenderWork)
                return Task.CompletedTask;

            substanceArchive.InRenderWork = true;
            instanceKey.Graph.CurrentStatePreset = substanceArchive.CreatePresetFromCurrentState(instanceKey.Graph.Index);
            EditorUtility.SetDirty(instanceKey);

            var renderResut = new RenderResult()
            {
                SubstanceArchive = substanceArchive,
                ForceRebuild = forceRebuild,
                GUID = instanceKey.GUID,
                GraphID = instanceKey.Graph.Index
            };

            return Task.Run(() =>
            {
                try
                {
                    renderResut.Result = substanceArchive.Render(instanceKey.Graph.Index);
                    _renderResultsQueue.Enqueue(renderResut);
                }
                catch (Exception e)
                {
                    substanceArchive.InRenderWork = false;
                    Debug.LogException(e);
                }
            });
        }

        private Task SubmitAsyncRenderWork(SubstanceNativeHandler substanceArchive, IReadOnlyList<SubstanceGraphSO> graphs, bool forceRebuild = false)
        {
            if (substanceArchive.InRenderWork)
                return Task.CompletedTask;

            substanceArchive.InRenderWork = true;

            foreach (var graph in graphs)
            {
                graph.Graph.CurrentStatePreset = substanceArchive.CreatePresetFromCurrentState(graph.Graph.Index);
                EditorUtility.SetDirty(graph);
            }

            return Task.Run(() =>
            {
                try
                {
                    foreach (var graph in graphs)
                    {
                        var renderResut = new RenderResult()
                        {
                            SubstanceArchive = substanceArchive,
                            ForceRebuild = forceRebuild,
                            GUID = graph.GUID,
                            GraphID = graph.Graph.Index
                        };

                        renderResut.Result = substanceArchive.Render(graph.Graph.Index);
                        _renderResultsQueue.Enqueue(renderResut);
                    }
                }
                catch (Exception e)
                {
                    substanceArchive.InRenderWork = false;
                    Debug.LogException(e);
                }
            });
        }

        /// <summary>
        /// Generate the .tga file from a RenderResult.
        /// </summary>
        /// <param name="renderResult">Target render result.</param>
        /// <param name="substance">Owner substance.</param>
        /// <returns>Returns true if textures must be reassigned to the material. </returns>
        private bool UpdateTextureFromGraphRender(RenderResult renderResult, SubstanceGraph graph, SubstanceNativeHandler handler)
        {
            if (!renderResult.ForceRebuild && TryGetTextureInstances(graph))
            {
                var texturesToResize = graph.CheckIfTexturesResize(renderResult.Result);

                //Resize texture assets.
                if (texturesToResize.Count != 0)
                {
                    foreach (var resizeTexture in texturesToResize)
                    {
                        var index = resizeTexture.Item1;
                        var newSize = resizeTexture.Item2;
                        var targetPair = graph.Output.FirstOrDefault(a => a.Index == index);

#if UNITY_2021_2_OR_NEWER
                        targetPair.OutputTexture.Reinitialize(newSize.x, newSize.y);
#else
                        targetPair.OutputTexture.Resize(newSize.x, newSize.y);
#endif
                        if (!graph.IsRuntimeOnly)
                        {
                            var bytes = targetPair.OutputTexture.EncodeToTGA();
                            var assetPath = AssetDatabase.GetAssetPath(targetPair.OutputTexture);
                            File.WriteAllBytes(assetPath, bytes);
                        }
                    }

                    AssetDatabase.Refresh();
                }

                graph.UpdateOutputTextures(renderResult.Result);
                return false;
            }

            graph.CreateAndUpdateOutputTextures(renderResult.Result, handler);

            if (graph.IsRuntimeOnly)
                return true;

            foreach (var substanceOutput in graph.Output)
            {
                var texture = substanceOutput.OutputTexture;

                if (texture == null)
                    continue;

                var textureOutput = graph.GetAssociatedAssetPath(substanceOutput.Description.Identifier, "tga");
                var bytes = texture.EncodeToTGA();
                File.WriteAllBytes(textureOutput, bytes);
            }

            AssetDatabase.Refresh();

            foreach (var substanceOutput in graph.Output)
            {
                var texture = substanceOutput.OutputTexture;

                if (texture == null)
                    continue;

                var textureOutput = graph.GetAssociatedAssetPath(substanceOutput.Description.Identifier, "tga");
                substanceOutput.OutputTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(textureOutput);
                ConfigureTextureImporter(substanceOutput);
            }

            AssetDatabase.Refresh();

            return true;
        }

        /// <summary>
        /// Try to get the texture2D instances for a give graph.
        /// </summary>
        /// <param name="graph">Target graph.</param>
        /// <param name="textures">Array of texture2D instances attached to each substance output.</param>
        /// <returns>True if all textures instances exists. If false they must be rebuild.</returns>
        private bool TryGetTextureInstances(SubstanceGraph graph)
        {
            foreach (var output in graph.Output)
            {
                if (!output.IsStandardOutput && !graph.GenerateAllOutputs)
                {
                    if (output.OutputTexture != null)
                    {
                        var assetPath = AssetDatabase.GetAssetPath(output.OutputTexture);

                        if (!string.IsNullOrEmpty(assetPath))
                            AssetDatabase.DeleteAsset(assetPath);

                        output.OutputTexture = null;
                    }

                    continue;
                }

                if (output.OutputTexture == null)
                    return false;

                if (!output.OutputTexture.isReadable)
                    output.OutputTexture = TextureUtils.SetReadableFlag(output.OutputTexture, true);

                if (output.OutputTexture.format.IsCompressed())
                    output.OutputTexture = TextureUtils.MakeUncompressed(output.OutputTexture);

                output.OutputTexture = TextureUtils.EnforceMaxResolution(output.OutputTexture);
            }

            return true;
        }

        /// <summary>
        /// Configures the texture importer settings to the associated texture output.
        /// </summary>
        /// <param name="textureOutput">Target output texture.</param>
        private void ConfigureTextureImporter(SubstanceOutputTexture textureOutput)
        {
            var texturePath = AssetDatabase.GetAssetPath(textureOutput.OutputTexture);
            TextureImporter importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;

            if (importer == null)
                return;

            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.isReadable = true;
            importer.maxTextureSize = 4096;
            importer.sRGBTexture = textureOutput.sRGB;

            importer.GetDefaultPlatformTextureSettings();

            if (textureOutput.IsNormalMap() && importer.textureType != TextureImporterType.NormalMap)
                importer.textureType = TextureImporterType.NormalMap;

            EditorUtility.SetDirty(importer);
            AssetDatabase.WriteImportSettingsIfDirty(texturePath);
        }

        private void DeleteGeneratedTextures(SubstanceGraph graph)
        {
            foreach (var output in graph.Output)
            {
                if (output.OutputTexture != null)
                {
                    var texturePath = AssetDatabase.GetAssetPath(output.OutputTexture);

                    if (!string.IsNullOrEmpty(texturePath))
                        AssetDatabase.DeleteAsset(texturePath);
                }
            }
        }

        private struct RenderResult
        {
            public SubstanceNativeHandler SubstanceArchive;
            public IntPtr Result;
            public bool ForceRebuild;
            public string GUID;
            public int GraphID;
        }

        #endregion Rendering
    }
}