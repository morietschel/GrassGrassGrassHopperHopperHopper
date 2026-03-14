using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Grasshopper.GUI;
using Grasshopper.GUI.Base;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using HelloRhinoCommon.Models;
using HelloRhinoCommon.UI;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace HelloRhinoCommon.Runtime;

internal sealed class ModifierEngine : IDisposable
{
    private const string LogPrefix = "GGH";
    private static readonly string[] InputAliases = { "GeomIn", "GeoIn" };
    private static readonly string[] OutputAliases = { "GeomOut", "GeoOut" };

    private readonly Dictionary<uint, DocumentState> _documents = new();
    private readonly Dictionary<string, DefinitionTemplate> _definitionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<QueuedStack> _queuedStacks = new();
    private readonly HashSet<string> _queuedKeys = new(StringComparer.Ordinal);
    private readonly StackPreviewConduit _previewConduit;
    private ulong _revisionCounter = 1;
    private bool _disposed;
    private bool _idleAttached;

    public ModifierEngine()
    {
        _previewConduit = new StackPreviewConduit(this);
        Log("ModifierEngine initialized.");

        RhinoDoc.ReplaceRhinoObject += OnReplaceRhinoObject;
        RhinoDoc.DeleteRhinoObject += OnDeleteRhinoObject;
        RhinoDoc.UndeleteRhinoObject += OnUndeleteRhinoObject;
        RhinoDoc.CloseDocument += OnCloseDocument;
        RhinoDoc.SelectObjects += OnSelectionChanged;
        RhinoDoc.DeselectObjects += OnSelectionChanged;
        RhinoDoc.DeselectAllObjects += OnDeselectAllObjects;
    }

    public event EventHandler? StateChanged;

    public ModifierPanelState GetPanelState(RhinoDoc? doc)
    {
        if (doc is null)
        {
            return new ModifierPanelState
            {
                StatusMessage = "No active Rhino document.",
            };
        }

        if (!TryGetSingleSelectedObject(doc, out var rhinoObject, out var statusMessage))
        {
            return new ModifierPanelState
            {
                StatusMessage = statusMessage,
            };
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        var runtime = TryGetStackRuntime(doc, rhinoObject!.Id);
        var steps = new List<ModifierStepPanelState>(spec.Steps.Count);

        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var step = spec.Steps[i];
            var stepError = runtime?.GetErrorForIndex(i) ?? string.Empty;
            var inputs = Array.Empty<ModifierStepInputPanelState>();
            var outputs = Array.Empty<ModifierStepOutputPanelState>();

            if (TryGetDefinitionContract(step.Path, out var contract, out var contractError))
            {
                inputs = BuildInputPanelState(step, contract).ToArray();
                outputs = BuildOutputPanelState(runtime?.GetOutputsForIndex(i), contract).ToArray();
            }
            else if (string.IsNullOrWhiteSpace(stepError))
            {
                stepError = contractError;
            }

            steps.Add(new ModifierStepPanelState
            {
                Index = i,
                Enabled = step.Enabled,
                FullPath = step.Path,
                DisplayName = Path.GetFileName(step.Path),
                ErrorMessage = stepError,
                Inputs = inputs,
                Outputs = outputs,
            });
        }

        var selectionLabel = $"{rhinoObject.ObjectType}  {rhinoObject.Id}";
        var runtimeMessage = runtime?.ErrorMessage ?? string.Empty;

        return new ModifierPanelState
        {
            CanEdit = true,
            SelectedObjectId = rhinoObject.Id,
            SelectionLabel = selectionLabel,
            StatusMessage = runtimeMessage,
            Steps = steps,
        };
    }

    public IEnumerable<PreviewStack> GetPreviewStacks(RhinoDoc? doc)
    {
        if (doc is null)
        {
            yield break;
        }

        if (!_documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState))
        {
            yield break;
        }

        foreach (var stack in documentState.Stacks)
        {
            if (stack.Value.PreviewGeometry.Count == 0)
            {
                continue;
            }

            yield return new PreviewStack(stack.Key, stack.Value.PreviewGeometry);
        }
    }

    public IEnumerable<Guid> GetManagedObjectIds(RhinoDoc? doc)
    {
        if (doc is null)
        {
            yield break;
        }

        if (!_documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState))
        {
            yield break;
        }

        foreach (var objectId in documentState.Stacks.Keys)
        {
            yield return objectId;
        }
    }

    public bool AddStep(RhinoDoc doc, Guid objectId, string path, out string message)
    {
        message = string.Empty;
        Log($"AddStep requested. Object={objectId}, Path={path}");
        if (!File.Exists(path))
        {
            message = $"Modifier file not found: {path}";
            Log(message);
            return false;
        }

        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        if (!IsSupportedGeometryObject(rhinoObject))
        {
            message = $"Object type '{rhinoObject.ObjectType}' is not supported by the MVP.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        spec.Steps.Add(new ModifierStepSpec
        {
            Enabled = true,
            Path = Path.GetFullPath(path),
        });

        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to store the modifier stack on the Rhino object.";
            Log(message);
            return false;
        }

        ResetStackRuntime(doc, objectId, spec);
        message = $"Added modifier: {Path.GetFileName(path)}";
        Log($"{message} StackCount={spec.Steps.Count}");
        return true;
    }

    public bool RemoveStep(RhinoDoc doc, Guid objectId, int index, out string message)
    {
        message = string.Empty;
        Log($"RemoveStep requested. Object={objectId}, Index={index}");
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (index < 0 || index >= spec.Steps.Count)
        {
            message = "Step index is out of range.";
            Log(message);
            return false;
        }

        spec.Steps.RemoveAt(index);
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier stack.";
            Log(message);
            return false;
        }

        ResetStackRuntime(doc, objectId, spec);
        message = "Removed modifier step.";
        Log($"{message} StackCount={spec.Steps.Count}");
        return true;
    }

    public bool MoveStep(RhinoDoc doc, Guid objectId, int index, int offset, out string message)
    {
        message = string.Empty;
        Log($"MoveStep requested. Object={objectId}, Index={index}, Offset={offset}");
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var targetIndex = index + offset;
        var spec = ModifierStackStorage.Load(rhinoObject);
        if (index < 0 || index >= spec.Steps.Count || targetIndex < 0 || targetIndex >= spec.Steps.Count)
        {
            message = "Cannot move the modifier step further in that direction.";
            Log(message);
            return false;
        }

        (spec.Steps[index], spec.Steps[targetIndex]) = (spec.Steps[targetIndex], spec.Steps[index]);
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier stack.";
            Log(message);
            return false;
        }

        ResetStackRuntime(doc, objectId, spec);
        message = "Moved modifier step.";
        Log($"{message} NewIndex={targetIndex}");
        return true;
    }

    public bool SetStepEnabled(RhinoDoc doc, Guid objectId, int index, bool enabled, out string message)
    {
        message = string.Empty;
        Log($"SetStepEnabled requested. Object={objectId}, Index={index}, Enabled={enabled}");
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (index < 0 || index >= spec.Steps.Count)
        {
            message = "Step index is out of range.";
            Log(message);
            return false;
        }

        spec.Steps[index].Enabled = enabled;
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier stack.";
            Log(message);
            return false;
        }

        ResetStackRuntime(doc, objectId, spec);
        message = enabled ? "Modifier enabled." : "Modifier disabled.";
        Log(message);
        return true;
    }

    public bool SetStepInputValue(RhinoDoc doc, Guid objectId, int index, string inputId, string serializedValue, out string message)
    {
        message = string.Empty;
        Log($"SetStepInputValue requested. Object={objectId}, Index={index}, Input={inputId}, Value='{serializedValue}'");
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (index < 0 || index >= spec.Steps.Count)
        {
            message = "Step index is out of range.";
            Log(message);
            return false;
        }

        spec.Steps[index].InputValues[inputId] = serializedValue ?? string.Empty;
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier input value.";
            Log(message);
            return false;
        }

        ResetStackRuntime(doc, objectId, spec);
        message = "Updated modifier input.";
        Log(message);
        return true;
    }

    public bool RefreshSelectedObject(RhinoDoc doc, out string message)
    {
        if (!TryGetSingleSelectedObject(doc, out var rhinoObject, out message))
        {
            Log($"RefreshSelectedObject rejected. {message}");
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (spec.Steps.Count == 0)
        {
            message = "Selected object does not have any modifier steps.";
            Log(message);
            return false;
        }

        ResetStackRuntime(doc, rhinoObject!.Id, spec);
        message = "Queued stack refresh.";
        Log($"{message} Object={rhinoObject.Id}, StepCount={spec.Steps.Count}");
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Log("ModifierEngine disposing.");

        RhinoDoc.ReplaceRhinoObject -= OnReplaceRhinoObject;
        RhinoDoc.DeleteRhinoObject -= OnDeleteRhinoObject;
        RhinoDoc.UndeleteRhinoObject -= OnUndeleteRhinoObject;
        RhinoDoc.CloseDocument -= OnCloseDocument;
        RhinoDoc.SelectObjects -= OnSelectionChanged;
        RhinoDoc.DeselectObjects -= OnSelectionChanged;
        RhinoDoc.DeselectAllObjects -= OnDeselectAllObjects;

        if (_idleAttached)
        {
            RhinoApp.Idle -= OnIdle;
        }

        foreach (var documentState in _documents.Values)
        {
            documentState.Dispose();
        }

        foreach (var template in _definitionCache.Values)
        {
            template.Dispose();
        }

        _documents.Clear();
        _definitionCache.Clear();
        _queuedStacks.Clear();
        _queuedKeys.Clear();
        _previewConduit.Enabled = false;
        Log("ModifierEngine disposed.");
    }

    private void OnReplaceRhinoObject(object? sender, RhinoReplaceObjectEventArgs e)
    {
        var spec = ModifierStackStorage.Load(e.NewRhinoObject);
        if (spec.Steps.Count == 0)
        {
            return;
        }

        Log($"Rhino object replaced. Object={e.ObjectId}, Steps={spec.Steps.Count}");
        var runtime = GetOrCreateStackRuntime(e.Document, e.ObjectId);
        runtime.RootRevision = NextRevision();
        QueueEvaluation(e.Document, e.ObjectId);
    }

    private void OnDeleteRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        Log($"Rhino object deleted. Object={e.ObjectId}");
        RemoveStackRuntime(e.TheObject?.Document, e.ObjectId);
    }

    private void OnUndeleteRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        var doc = e.TheObject?.Document ?? RhinoDoc.FromRuntimeSerialNumber(e.TheObject?.Document.RuntimeSerialNumber ?? 0);
        if (doc is null)
        {
            return;
        }

        var rhinoObject = doc.Objects.FindId(e.ObjectId);
        var spec = ModifierStackStorage.Load(rhinoObject);
        if (spec.Steps.Count == 0)
        {
            return;
        }

        Log($"Rhino object undeleted. Object={e.ObjectId}, Steps={spec.Steps.Count}");
        var runtime = GetOrCreateStackRuntime(doc, e.ObjectId);
        runtime.RootRevision = NextRevision();
        QueueEvaluation(doc, e.ObjectId);
    }

    private void OnCloseDocument(object? sender, DocumentEventArgs e)
    {
        Log($"Rhino document closing. Serial={e.Document.RuntimeSerialNumber}");
        if (_documents.Remove(e.Document.RuntimeSerialNumber, out var state))
        {
            state.Dispose();
            UpdateConduitState();
        }

        RaiseStateChanged();
    }

    private void OnSelectionChanged(object? sender, RhinoObjectSelectionEventArgs e)
    {
        var count = e.RhinoObjects?.Length ?? 0;
        Log($"Selection changed. AffectedCount={count}, TotalSelected={e.Document.Objects.GetSelectedObjects(false, false).Count()}");
        RaiseStateChanged();
    }

    private void OnDeselectAllObjects(object? sender, RhinoDeselectAllObjectsEventArgs e)
    {
        Log("Selection cleared.");
        RaiseStateChanged();
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        if (_queuedStacks.Count > 0)
        {
            Log($"Idle processing started. QueueCount={_queuedStacks.Count}");
        }

        while (_queuedStacks.Count > 0)
        {
            var queued = _queuedStacks.Dequeue();
            _queuedKeys.Remove(queued.Key);
            Log($"Dequeued stack evaluation. Doc={queued.DocumentSerial}, Object={queued.ObjectId}, Remaining={_queuedStacks.Count}");

            var doc = RhinoDoc.FromRuntimeSerialNumber(queued.DocumentSerial);
            if (doc is null)
            {
                Log($"Skipped queued stack because document {queued.DocumentSerial} is unavailable.");
                continue;
            }

            EvaluateStack(doc, queued.ObjectId);
        }

        if (_queuedStacks.Count == 0 && _idleAttached)
        {
            RhinoApp.Idle -= OnIdle;
            _idleAttached = false;
            Log("Idle processing finished. Queue empty.");
        }
    }

    private void EvaluateStack(RhinoDoc doc, Guid objectId)
    {
        Log($"EvaluateStack started. Doc={doc.RuntimeSerialNumber}, Object={objectId}");
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            Log($"EvaluateStack aborted. Object={objectId} no longer exists.");
            RemoveStackRuntime(doc, objectId);
            return;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (spec.Steps.Count == 0)
        {
            Log($"EvaluateStack aborted. Object={objectId} has no modifier steps.");
            RemoveStackRuntime(doc, objectId);
            return;
        }

        Log($"Stack spec loaded. Object={DescribeRhinoObject(rhinoObject)}, StepCount={spec.Steps.Count}");

        var runtime = GetOrCreateStackRuntime(doc, objectId);
        runtime.EnsureStepCapacity(spec.Steps.Count);
        runtime.ClearErrors(spec.Steps.Count);
        runtime.ClearAllOutputs(spec.Steps.Count);

        if (!GeometryConversion.TryGetSourceGeometry(rhinoObject.Geometry, out var currentGeometry, out var sourceError))
        {
            runtime.PreviewGeometry.Clear();
            runtime.SetError(-1, sourceError);
            Log($"Source geometry conversion failed. Object={objectId}. {sourceError}");
            UpdateConduitAndViews(doc);
            RaiseStateChanged();
            return;
        }

        Log($"Source geometry ready. Count={currentGeometry.Count}. {DescribeGeometry(currentGeometry)}");

        ulong upstreamRevision = runtime.RootRevision;
        var anyStepSucceeded = false;
        var failedAtFirstEnabledStep = false;
        var firstEnabledIndex = spec.Steps.FindIndex(step => step.Enabled);
        Log($"Stack evaluation entering step loop. RootRevision={runtime.RootRevision}, FirstEnabledIndex={firstEnabledIndex}");

        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var stepSpec = spec.Steps[i];
            if (!stepSpec.Enabled)
            {
                Log($"Step {i} disabled. Disposing any existing runtime and skipping.");
                runtime.DisposeStep(i);
                runtime.ClearOutputs(i);
                continue;
            }

            StepRuntime stepRuntime;
            try
            {
                stepRuntime = EnsureStepRuntime(runtime, i, stepSpec);
            }
            catch (Exception ex)
            {
                runtime.SetError(i, ex.Message);
                Log($"Step {i} runtime setup failed for '{Path.GetFileName(stepSpec.Path)}'. {ex.Message}");
                failedAtFirstEnabledStep = i == firstEnabledIndex;
                break;
            }

            if (stepRuntime.LastInputRevision == upstreamRevision)
            {
                Log($"Step {i} cache hit. Modifier={Path.GetFileName(stepSpec.Path)}, InputRevision={upstreamRevision}, CachedOutputCount={stepRuntime.CachedOutput.Count}");
                runtime.SetOutputs(i, stepRuntime.CachedDisplayedOutputs);
                if (stepRuntime.HasGeometryOutputs)
                {
                    currentGeometry = CloneGeometry(stepRuntime.CachedOutput);
                }

                upstreamRevision = stepRuntime.LastOutputRevision;
                anyStepSucceeded = true;
                continue;
            }

            Log($"Step {i} solving. Modifier={Path.GetFileName(stepSpec.Path)}, InputRevision={upstreamRevision}, InputCount={currentGeometry.Count}, InputSummary={DescribeGeometry(currentGeometry)}");
            var result = EvaluateStep(doc, stepRuntime, stepSpec, currentGeometry);
            if (!result.Success)
            {
                runtime.SetError(i, result.ErrorMessage);
                runtime.ClearOutputs(i);
                Log($"Step {i} solve failed for '{Path.GetFileName(stepSpec.Path)}'. {result.ErrorMessage}");
                failedAtFirstEnabledStep = i == firstEnabledIndex;
                break;
            }

            stepRuntime.CachedOutput = result.OutputGeometry;
            stepRuntime.CachedDisplayedOutputs = result.DisplayOutputs;
            stepRuntime.LastInputRevision = upstreamRevision;
            stepRuntime.LastOutputRevision = result.HasGeometryOutput
                ? NextRevision()
                : upstreamRevision;
            runtime.SetOutputs(i, result.DisplayOutputs);
            if (result.HasGeometryOutput)
            {
                currentGeometry = CloneGeometry(stepRuntime.CachedOutput);
            }

            upstreamRevision = stepRuntime.LastOutputRevision;
            anyStepSucceeded = true;
            Log($"Step {i} solve complete. Modifier={Path.GetFileName(stepSpec.Path)}, OutputRevision={stepRuntime.LastOutputRevision}, HasGeometryOutput={result.HasGeometryOutput}, RawOutputCount={result.RawGeometryOutputItemCount}, ConvertedOutputCount={stepRuntime.CachedOutput.Count}, OutputSummary={DescribeGeometry(result.HasGeometryOutput ? stepRuntime.CachedOutput : currentGeometry)}");
            if (result.SkippedGeometryOutputTypes.Count > 0)
            {
                Log($"Step {i} skipped output items. Modifier={Path.GetFileName(stepSpec.Path)}, Skipped={string.Join(", ", result.SkippedGeometryOutputTypes)}");
            }
        }

        runtime.PreviewGeometry = failedAtFirstEnabledStep && !anyStepSucceeded
            ? new List<GeometryBase>()
            : CloneGeometry(currentGeometry);

        if (runtime.PreviewGeometry.Count == 0 && string.IsNullOrWhiteSpace(runtime.ErrorMessage))
        {
            Log($"Stack on {objectId} evaluated but produced no preview geometry.");
        }

        Log($"EvaluateStack finished. Object={objectId}, PreviewCount={runtime.PreviewGeometry.Count}, PreviewSummary={DescribeGeometry(runtime.PreviewGeometry)}, Error='{runtime.ErrorMessage}'");
        UpdateConduitAndViews(doc);
        RaiseStateChanged();
    }

    private StepRuntime EnsureStepRuntime(StackRuntime runtime, int index, ModifierStepSpec spec)
    {
        var fullPath = Path.GetFullPath(spec.Path);
        var lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);

        var existing = runtime.StepRuntimes[index];
        if (existing is not null && existing.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase) && existing.LastWriteUtc == lastWriteUtc)
        {
            Log($"Step {index} runtime reused. Modifier={Path.GetFileName(fullPath)}, LastWriteUtc={lastWriteUtc:O}");
            return existing;
        }

        existing?.Dispose();
        if (existing is not null)
        {
            Log($"Step {index} runtime replaced. Modifier={Path.GetFileName(fullPath)}, LastWriteUtc={lastWriteUtc:O}");
        }

        var template = GetDefinitionTemplate(fullPath, lastWriteUtc);
        var document = GH_Document.DuplicateDocument(template.Document);
        Log($"Step {index} duplicated GH document. Modifier={Path.GetFileName(fullPath)}");

        Param_Geometry? sceneInputSource = null;
        IGH_Param? sceneInputParam = null;
        if (template.Contract.SceneInput is not null)
        {
            sceneInputParam = ResolveDocumentParam(document, template.Contract.SceneInput.ObjectId)
                ?? throw new InvalidOperationException($"Modifier '{Path.GetFileName(fullPath)}' is missing its scene geometry input in the duplicated document.");

            sceneInputSource = CreateRuntimeSourceParam(document, sceneInputParam);
        }

        var inputBindings = BindInputDescriptors(document, template.Contract.Inputs);
        var outputBindings = BindOutputDescriptors(document, template.Contract.Outputs);
        var geometryOutputBindings = BindOutputDescriptors(document, template.Contract.GeometryOutputs);

        Log($"Step {index} runtime created. Modifier={Path.GetFileName(fullPath)}, ExposedInputs={inputBindings.Count}, ExposedOutputs={outputBindings.Count}, GeometryOutputs={geometryOutputBindings.Count}");
        var stepRuntime = new StepRuntime(
            fullPath,
            lastWriteUtc,
            document,
            template.Contract,
            sceneInputSource,
            sceneInputParam,
            inputBindings,
            outputBindings,
            geometryOutputBindings);
        runtime.StepRuntimes[index] = stepRuntime;
        return stepRuntime;
    }

    private DefinitionTemplate GetDefinitionTemplate(string fullPath, DateTime lastWriteUtc)
    {
        if (_definitionCache.TryGetValue(fullPath, out var cached) && cached.LastWriteUtc == lastWriteUtc)
        {
            Log($"Definition template cache hit. Path={fullPath}, LastWriteUtc={lastWriteUtc:O}");
            return cached;
        }

        cached?.Dispose();
        if (cached is not null)
        {
            Log($"Definition template invalidated. Path={fullPath}, LastWriteUtc={lastWriteUtc:O}");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Modifier file not found.", fullPath);
        }

        Log($"Loading Grasshopper definition from disk. Path={fullPath}");
        var io = new GH_DocumentIO();
        if (!io.Open(fullPath))
        {
            throw new InvalidOperationException($"Failed to load Grasshopper definition '{fullPath}'.");
        }

        if (io.Document is null)
        {
            throw new InvalidOperationException($"Grasshopper definition '{fullPath}' did not produce a document.");
        }

        var template = new DefinitionTemplate(fullPath, lastWriteUtc, io.Document, CreateDefinitionContract(io.Document));
        _definitionCache[fullPath] = template;
        Log($"Definition template cached. Path={fullPath}, LastWriteUtc={lastWriteUtc:O}");
        return template;
    }

    private static StepEvaluationResult EvaluateStep(RhinoDoc doc, StepRuntime runtime, ModifierStepSpec stepSpec, IReadOnlyList<GeometryBase> inputGeometry)
    {
        if (runtime.SceneInputSource is not null)
        {
            if (!TryAppendGeometry(runtime.SceneInputSource, inputGeometry, out var error))
            {
                return StepEvaluationResult.Fail(error);
            }
        }

        foreach (var binding in runtime.Inputs)
        {
            if (!ApplyInputBinding(doc, binding, stepSpec, inputGeometry, out var error))
            {
                return StepEvaluationResult.Fail(error);
            }
        }

        runtime.SceneInputSource?.ExpireSolution(false);
        runtime.SceneInputParam?.ExpireSolution(false);
        foreach (var binding in runtime.Inputs)
        {
            binding.Expire();
        }

        foreach (var binding in runtime.Outputs)
        {
            binding.Expire();
        }

        foreach (var binding in runtime.GeometryOutputs)
        {
            binding.Expire();
        }

        runtime.Document.NewSolution(true, GH_SolutionMode.Silent);

        var outputValues = new List<StepOutputValue>(runtime.Outputs.Count);
        foreach (var binding in runtime.Outputs)
        {
            binding.Param.CollectData();
            binding.Param.ComputeData();
            outputValues.Add(new StepOutputValue(binding.Descriptor.Id, FormatOutputValue(binding.Descriptor, binding.Param)));
        }

        if (runtime.GeometryOutputs.Count == 0)
        {
            return StepEvaluationResult.Successful(false, new List<GeometryBase>(), outputValues, 0, new List<string>());
        }

        var geometry = new List<GeometryBase>();
        var skipped = new List<string>();
        var totalRawItemCount = 0;
        foreach (var binding in runtime.GeometryOutputs)
        {
            binding.Param.CollectData();
            binding.Param.ComputeData();

            var output = GeometryConversion.ReadOutput(binding.Param);
            geometry.AddRange(output.Geometry);
            skipped.AddRange(output.SkippedTypes);
            totalRawItemCount += output.TotalItemCount;
        }

        return StepEvaluationResult.Successful(true, geometry, outputValues, totalRawItemCount, skipped);
    }

    private static Param_Geometry CreateRuntimeSourceParam(GH_Document document, IGH_Param inputParam)
    {
        var sourceParam = new Param_Geometry
        {
            Name = "GGH Runtime Input",
            NickName = "GGH Runtime Input",
            Description = "Injected runtime input for scene-bound modifier evaluation.",
            MutableNickName = false,
            Hidden = true,
            Optional = false,
        };

        document.AddObject(sourceParam, false);
        inputParam.AddSource(sourceParam);
        return sourceParam;
    }

    private bool TryGetDefinitionContract(string path, out DefinitionContract contract, out string error)
    {
        contract = null!;
        error = string.Empty;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var template = GetDefinitionTemplate(fullPath, File.GetLastWriteTimeUtc(fullPath));
            contract = template.Contract;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static IEnumerable<ModifierStepInputPanelState> BuildInputPanelState(ModifierStepSpec stepSpec, DefinitionContract contract)
    {
        foreach (var input in contract.Inputs)
        {
            var serializedValue = stepSpec.InputValues.TryGetValue(input.Id, out var storedValue)
                ? storedValue
                : input.HasDefaultValue
                    ? input.DefaultSerializedValue
                    : string.Empty;

            yield return new ModifierStepInputPanelState
            {
                Id = input.Id,
                Label = input.Label,
                Description = input.Kind == ModifierIoKind.Geometry
                    ? AppendDescription(input.Description, "Blank uses the current stack geometry. Paste Rhino object IDs or `self` to override.")
                    : input.Description,
                Kind = input.Kind,
                SerializedValue = serializedValue,
                Minimum = input.Minimum,
                Maximum = input.Maximum,
                DecimalPlaces = input.DecimalPlaces,
            };
        }
    }

    private static IEnumerable<ModifierStepOutputPanelState> BuildOutputPanelState(IReadOnlyList<StepOutputValue>? runtimeOutputs, DefinitionContract contract)
    {
        var displayById = runtimeOutputs?.ToDictionary(output => output.Id, output => output.DisplayValue, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var output in contract.Outputs)
        {
            displayById.TryGetValue(output.Id, out var displayValue);
            yield return new ModifierStepOutputPanelState
            {
                Id = output.Id,
                Label = output.Label,
                Description = output.Description,
                Kind = output.Kind,
                DisplayValue = displayValue ?? string.Empty,
            };
        }
    }

    private static DefinitionContract CreateDefinitionContract(GH_Document document)
    {
        var inputGroupIds = GetGroupObjectIds(document, "Inputs");
        var outputGroupIds = GetGroupObjectIds(document, "Outputs");

        var inputs = new List<ModifierInputDescriptor>();
        foreach (var objectId in inputGroupIds)
        {
            var documentObject = ResolveDocumentObject(document, objectId);
            if (documentObject is null || documentObject is GH_Group)
            {
                continue;
            }

            if (TryCreateInputDescriptor(documentObject, out var descriptor, out var error) && descriptor is not null)
            {
                inputs.Add(descriptor);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(error);
            }
        }

        var outputs = new List<ModifierOutputDescriptor>();
        foreach (var objectId in outputGroupIds)
        {
            var documentObject = ResolveDocumentObject(document, objectId);
            if (documentObject is null || documentObject is GH_Group)
            {
                continue;
            }

            if (TryCreateOutputDescriptor(documentObject, out var descriptor) && descriptor is not null)
            {
                outputs.Add(descriptor);
            }
        }

        var sceneInput = FindLegacySceneInput(document, inputGroupIds);

        var geometryOutputs = outputs
            .Where(output => output.Kind == ModifierIoKind.Geometry)
            .ToList();

        if (geometryOutputs.Count == 0)
        {
            var legacyOutput = FindLegacyGeometryOutput(document, outputGroupIds);
            if (legacyOutput is not null)
            {
                geometryOutputs.Add(legacyOutput);
            }
        }

        return new DefinitionContract(sceneInput, inputs, outputs, geometryOutputs);
    }

    private static HashSet<Guid> GetGroupObjectIds(GH_Document document, string groupName)
    {
        var group = document.Objects
            .OfType<GH_Group>()
            .FirstOrDefault(candidate => candidate.NickName.Equals(groupName, StringComparison.OrdinalIgnoreCase));

        return group is null
            ? new HashSet<Guid>()
            : new HashSet<Guid>(group.ObjectIDs);
    }

    private static ModifierInputDescriptor? FindLegacySceneInput(GH_Document document, HashSet<Guid> groupedInputIds)
    {
        var param = document.Objects
            .OfType<IGH_Param>()
            .FirstOrDefault(candidate =>
                !groupedInputIds.Contains(candidate.InstanceGuid) &&
                InputAliases.Any(alias => candidate.NickName.Equals(alias, StringComparison.OrdinalIgnoreCase)));

        if (param is null)
        {
            return null;
        }

        if (param.Sources.Count > 0)
        {
            throw new InvalidOperationException("Legacy scene geometry input must be unwired.");
        }

        return CreateParamInputDescriptor(param, ModifierIoKind.Geometry, hasDefaultValue: false, defaultSerializedValue: string.Empty, usesSceneGeometryWhenBlank: true, minimum: null, maximum: null, decimalPlaces: 0);
    }

    private static ModifierOutputDescriptor? FindLegacyGeometryOutput(GH_Document document, HashSet<Guid> groupedOutputIds)
    {
        var param = document.Objects
            .OfType<IGH_Param>()
            .FirstOrDefault(candidate =>
                !groupedOutputIds.Contains(candidate.InstanceGuid) &&
                OutputAliases.Any(alias => candidate.NickName.Equals(alias, StringComparison.OrdinalIgnoreCase)));

        return param is null
            ? null
            : CreateOutputDescriptor(param, ModifierIoKind.Geometry);
    }

    private static bool TryCreateInputDescriptor(IGH_DocumentObject documentObject, out ModifierInputDescriptor? descriptor, out string error)
    {
        descriptor = null;
        error = string.Empty;

        if (documentObject is GH_NumberSlider slider)
        {
            descriptor = new ModifierInputDescriptor(
                slider.InstanceGuid,
                slider.InstanceGuid.ToString("D"),
                GetDisplayLabel(slider),
                slider.Description ?? string.Empty,
                ModifierIoKind.NumberSlider,
                SerializeNumber(slider.CurrentValue),
                true,
                false,
                (double)slider.Slider.Minimum,
                (double)slider.Slider.Maximum,
                slider.Slider.Type == GH_SliderAccuracy.Float ? slider.Slider.DecimalPlaces : 0);
            return true;
        }

        if (documentObject is not IGH_Param param || !TryGetSupportedParamKind(param, out var kind))
        {
            return false;
        }

        if (param.Sources.Count > 0)
        {
            error = $"Input '{GetDisplayLabel(param)}' must be unwired.";
            return false;
        }

        var hasDefaultValue = TryReadDefaultSerializedValue(param, kind, out var defaultSerializedValue);
        descriptor = CreateParamInputDescriptor(
            param,
            kind,
            hasDefaultValue,
            defaultSerializedValue,
            usesSceneGeometryWhenBlank: kind == ModifierIoKind.Geometry,
            minimum: null,
            maximum: null,
            decimalPlaces: kind == ModifierIoKind.Number ? 3 : 0);
        return true;
    }

    private static bool TryCreateOutputDescriptor(IGH_DocumentObject documentObject, out ModifierOutputDescriptor? descriptor)
    {
        descriptor = null;
        if (documentObject is not IGH_Param param || !TryGetSupportedParamKind(param, out var kind))
        {
            return false;
        }

        descriptor = CreateOutputDescriptor(param, kind);
        return true;
    }

    private static ModifierInputDescriptor CreateParamInputDescriptor(
        IGH_Param param,
        ModifierIoKind kind,
        bool hasDefaultValue,
        string defaultSerializedValue,
        bool usesSceneGeometryWhenBlank,
        double? minimum,
        double? maximum,
        int decimalPlaces)
    {
        return new ModifierInputDescriptor(
            param.InstanceGuid,
            param.InstanceGuid.ToString("D"),
            GetDisplayLabel(param),
            param.Description ?? string.Empty,
            kind,
            defaultSerializedValue,
            hasDefaultValue,
            usesSceneGeometryWhenBlank,
            minimum,
            maximum,
            decimalPlaces);
    }

    private static ModifierOutputDescriptor CreateOutputDescriptor(IGH_Param param, ModifierIoKind kind)
    {
        return new ModifierOutputDescriptor(
            param.InstanceGuid,
            param.InstanceGuid.ToString("D"),
            GetDisplayLabel(param),
            param.Description ?? string.Empty,
            kind);
    }

    private static string GetDisplayLabel(IGH_DocumentObject documentObject)
    {
        return !string.IsNullOrWhiteSpace(documentObject.NickName)
            ? documentObject.NickName
            : documentObject.Name;
    }

    private static bool TryGetSupportedParamKind(IGH_Param param, out ModifierIoKind kind)
    {
        switch (param)
        {
            case Param_Number:
            case Param_Integer:
                kind = ModifierIoKind.Number;
                return true;
            case Param_Point:
                kind = ModifierIoKind.Point;
                return true;
            case Param_String:
                kind = ModifierIoKind.String;
                return true;
            case Param_Boolean:
                kind = ModifierIoKind.Boolean;
                return true;
            case Param_Colour:
                kind = ModifierIoKind.Color;
                return true;
            case Param_Geometry:
                kind = ModifierIoKind.Geometry;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static bool TryReadDefaultSerializedValue(IGH_Param param, ModifierIoKind kind, out string serializedValue)
    {
        serializedValue = string.Empty;
        var first = EnumeratePersistentData(param).FirstOrDefault();
        if (first is null)
        {
            return false;
        }

        return TrySerializeGooValue(first, kind, out serializedValue);
    }

    private static List<RuntimeInputBinding> BindInputDescriptors(GH_Document document, IEnumerable<ModifierInputDescriptor> descriptors)
    {
        var bindings = new List<RuntimeInputBinding>();
        foreach (var descriptor in descriptors)
        {
            var documentObject = ResolveDocumentObject(document, descriptor.ObjectId)
                ?? throw new InvalidOperationException($"Modifier input '{descriptor.Label}' could not be found in the duplicated document.");

            if (documentObject is GH_NumberSlider slider)
            {
                bindings.Add(new RuntimeInputBinding(descriptor, slider));
                continue;
            }

            if (documentObject is IGH_Param param)
            {
                bindings.Add(new RuntimeInputBinding(descriptor, param));
                continue;
            }

            throw new InvalidOperationException($"Modifier input '{descriptor.Label}' is not a supported Grasshopper input object.");
        }

        return bindings;
    }

    private static List<RuntimeOutputBinding> BindOutputDescriptors(GH_Document document, IEnumerable<ModifierOutputDescriptor> descriptors)
    {
        var bindings = new List<RuntimeOutputBinding>();
        foreach (var descriptor in descriptors)
        {
            var param = ResolveDocumentParam(document, descriptor.ObjectId)
                ?? throw new InvalidOperationException($"Modifier output '{descriptor.Label}' could not be found in the duplicated document.");
            bindings.Add(new RuntimeOutputBinding(descriptor, param));
        }

        return bindings;
    }

    private static IGH_DocumentObject? ResolveDocumentObject(GH_Document document, Guid instanceGuid)
    {
        return document.Objects.FirstOrDefault(candidate => candidate.InstanceGuid == instanceGuid);
    }

    private static IGH_Param? ResolveDocumentParam(GH_Document document, Guid instanceGuid)
    {
        return ResolveDocumentObject(document, instanceGuid) as IGH_Param;
    }

    private static bool ApplyInputBinding(RhinoDoc doc, RuntimeInputBinding binding, ModifierStepSpec stepSpec, IReadOnlyList<GeometryBase> currentGeometry, out string error)
    {
        error = string.Empty;
        var hasValue = TryGetEffectiveInputValue(stepSpec, binding.Descriptor, out var serializedValue);

        if (binding.Slider is not null)
        {
            if (!hasValue)
            {
                return true;
            }

            if (!decimal.TryParse(serializedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var sliderValue))
            {
                error = $"Input '{binding.Descriptor.Label}' expects a number.";
                return false;
            }

            if (!binding.Slider.TrySetSliderValue(sliderValue))
            {
                binding.Slider.SetSliderValue(sliderValue);
            }

            return true;
        }

        if (binding.Param is null)
        {
            error = $"Input '{binding.Descriptor.Label}' is not bound to a Grasshopper parameter.";
            return false;
        }

        ClearParamData(binding.Param);
        if (!hasValue)
        {
            if (binding.Descriptor.UsesSceneGeometryWhenBlank)
            {
                return TryAppendGeometry(binding.Param, currentGeometry, out error);
            }

            return true;
        }

        switch (binding.Descriptor.Kind)
        {
            case ModifierIoKind.Number:
                if (!double.TryParse(serializedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var numberValue))
                {
                    error = $"Input '{binding.Descriptor.Label}' expects a number.";
                    return false;
                }

                AppendSingleValue(binding.Param, binding.Param is Param_Integer
                    ? (object)(int)Math.Round(numberValue, MidpointRounding.AwayFromZero)
                    : numberValue);
                return true;

            case ModifierIoKind.Point:
                if (string.IsNullOrWhiteSpace(serializedValue))
                {
                    return true;
                }

                if (!TryParsePoint(serializedValue, out var pointValue))
                {
                    error = $"Input '{binding.Descriptor.Label}' expects a point formatted like x,y,z.";
                    return false;
                }

                AppendSingleValue(binding.Param, pointValue);
                return true;

            case ModifierIoKind.String:
                AppendSingleValue(binding.Param, serializedValue);
                return true;

            case ModifierIoKind.Boolean:
                if (!bool.TryParse(serializedValue, out var boolValue))
                {
                    error = $"Input '{binding.Descriptor.Label}' expects true or false.";
                    return false;
                }

                AppendSingleValue(binding.Param, boolValue);
                return true;

            case ModifierIoKind.Color:
                if (string.IsNullOrWhiteSpace(serializedValue))
                {
                    return true;
                }

                if (!TryParseColor(serializedValue, out var colorValue))
                {
                    error = $"Input '{binding.Descriptor.Label}' expects a color like #RRGGBB or r,g,b.";
                    return false;
                }

                AppendSingleValue(binding.Param, colorValue);
                return true;

            case ModifierIoKind.Geometry:
                if (string.IsNullOrWhiteSpace(serializedValue))
                {
                    return binding.Descriptor.UsesSceneGeometryWhenBlank
                        ? TryAppendGeometry(binding.Param, currentGeometry, out error)
                        : true;
                }

                return TryAppendReferencedGeometry(doc, binding.Param, serializedValue, currentGeometry, out error);

            default:
                error = $"Input '{binding.Descriptor.Label}' uses an unsupported input type.";
                return false;
        }
    }

    private static bool TryGetEffectiveInputValue(ModifierStepSpec stepSpec, ModifierInputDescriptor descriptor, out string serializedValue)
    {
        if (stepSpec.InputValues.TryGetValue(descriptor.Id, out var storedValue))
        {
            serializedValue = storedValue ?? string.Empty;
            return true;
        }

        serializedValue = descriptor.DefaultSerializedValue;
        return descriptor.HasDefaultValue;
    }

    private static void ClearParamData(IGH_Param param)
    {
        param.ClearData();
        SetPersistentData(param, Array.Empty<object>());
    }

    private static void AppendSingleValue(IGH_Param param, object value)
    {
        SetPersistentData(param, new[] { value });
    }

    private static bool TryAppendGeometry(IGH_Param param, IEnumerable<GeometryBase> geometry, out string error)
    {
        error = string.Empty;
        ClearParamData(param);
        try
        {
            SetPersistentData(param, geometry.Cast<object>());
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryAppendReferencedGeometry(RhinoDoc doc, IGH_Param param, string serializedValue, IReadOnlyList<GeometryBase> currentGeometry, out string error)
    {
        error = string.Empty;
        var tokens = serializedValue
            .Split(new[] { ',', ';', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var geometry = new List<GeometryBase>();
        foreach (var token in tokens)
        {
            if (token.Equals("self", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("selected", StringComparison.OrdinalIgnoreCase))
            {
                geometry.AddRange(CloneGeometry(currentGeometry));
                continue;
            }

            if (!Guid.TryParse(token, out var objectId))
            {
                error = $"Geometry input token '{token}' is not a Rhino object id.";
                return false;
            }

            var rhinoObject = doc.Objects.FindId(objectId);
            if (rhinoObject is null)
            {
                error = $"Rhino object '{objectId}' could not be found for geometry input.";
                return false;
            }

            if (!GeometryConversion.TryGetSourceGeometry(rhinoObject.Geometry, out var converted, out var conversionError))
            {
                error = conversionError;
                return false;
            }

            geometry.AddRange(converted);
        }

        return TryAppendGeometry(param, geometry, out error);
    }

    private static string FormatOutputValue(ModifierOutputDescriptor descriptor, IGH_Param param)
    {
        if (descriptor.Kind == ModifierIoKind.Geometry)
        {
            var geometry = GeometryConversion.ReadOutput(param);
            return geometry.Geometry.Count == 0
                ? "none"
                : DescribeGeometry(geometry.Geometry);
        }

        var values = new List<string>();
        foreach (var goo in param.VolatileData.AllData(true))
        {
            if (TrySerializeGooValue(goo, descriptor.Kind, out var serializedValue))
            {
                values.Add(serializedValue);
            }
        }

        if (values.Count == 0)
        {
            return "none";
        }

        const int maxItems = 4;
        if (values.Count <= maxItems)
        {
            return string.Join(", ", values);
        }

        return $"{string.Join(", ", values.Take(maxItems))} (+{values.Count - maxItems} more)";
    }

    private static bool TrySerializeGooValue(IGH_Goo goo, ModifierIoKind kind, out string serializedValue)
    {
        serializedValue = string.Empty;
        var value = goo.ScriptVariable();
        if (value is null)
        {
            return false;
        }

        switch (kind)
        {
            case ModifierIoKind.Number:
            case ModifierIoKind.NumberSlider:
                if (TryConvertToDouble(value, out var numberValue))
                {
                    serializedValue = SerializeNumber(numberValue);
                    return true;
                }

                return false;

            case ModifierIoKind.Point:
                if (value is Point3d point)
                {
                    serializedValue = SerializePoint(point);
                    return true;
                }

                return false;

            case ModifierIoKind.String:
                serializedValue = value.ToString() ?? string.Empty;
                return true;

            case ModifierIoKind.Boolean:
                if (value is bool boolValue)
                {
                    serializedValue = boolValue ? bool.TrueString.ToLowerInvariant() : bool.FalseString.ToLowerInvariant();
                    return true;
                }

                return false;

            case ModifierIoKind.Color:
                if (value is System.Drawing.Color color)
                {
                    serializedValue = SerializeColor(color);
                    return true;
                }

                return false;

            case ModifierIoKind.Geometry:
                serializedValue = value.ToString() ?? string.Empty;
                return true;

            default:
                return false;
        }
    }

    private static bool TryConvertToDouble(object value, out double numberValue)
    {
        switch (value)
        {
            case double doubleValue:
                numberValue = doubleValue;
                return true;
            case decimal decimalValue:
                numberValue = (double)decimalValue;
                return true;
            case int intValue:
                numberValue = intValue;
                return true;
            case long longValue:
                numberValue = longValue;
                return true;
            case float floatValue:
                numberValue = floatValue;
                return true;
            default:
                numberValue = 0;
                return false;
        }
    }

    private static string SerializeNumber(decimal value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string SerializeNumber(double value)
    {
        return value.ToString("0.###############", CultureInfo.InvariantCulture);
    }

    private static string SerializePoint(Point3d point)
    {
        return FormattableString.Invariant($"{point.X:0.###############},{point.Y:0.###############},{point.Z:0.###############}");
    }

    private static bool TryParsePoint(string serializedValue, out Point3d point)
    {
        var cleaned = serializedValue.Replace("(", string.Empty).Replace(")", string.Empty);
        var parts = cleaned
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 3 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            point = Point3d.Unset;
            return false;
        }

        point = new Point3d(x, y, z);
        return true;
    }

    private static string SerializeColor(System.Drawing.Color color)
    {
        return color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static bool TryParseColor(string serializedValue, out System.Drawing.Color color)
    {
        var trimmed = serializedValue.Trim();
        if (trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            var hex = trimmed[1..];
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                color = System.Drawing.Color.FromArgb(
                    byte.MaxValue,
                    (rgb >> 16) & 0xFF,
                    (rgb >> 8) & 0xFF,
                    rgb & 0xFF);
                return true;
            }

            if (hex.Length == 8 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            {
                color = System.Drawing.Color.FromArgb(
                    (argb >> 24) & 0xFF,
                    (argb >> 16) & 0xFF,
                    (argb >> 8) & 0xFF,
                    argb & 0xFF);
                return true;
            }
        }

        var parts = trimmed
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if ((parts.Length == 3 || parts.Length == 4) &&
            byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
        {
            var a = byte.MaxValue;
            if (parts.Length == 4 &&
                !byte.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out a))
            {
                color = default;
                return false;
            }

            color = System.Drawing.Color.FromArgb(a, r, g, b);
            return true;
        }

        color = default;
        return false;
    }

    private static string AppendDescription(string description, string note)
    {
        return string.IsNullOrWhiteSpace(description)
            ? note
            : $"{description} {note}";
    }

    private static IEnumerable<IGH_Goo> EnumeratePersistentData(IGH_Param param)
    {
        var persistentData = param.GetType().GetProperty("PersistentData")?.GetValue(param);
        var allData = persistentData?.GetType().GetMethod("AllData", new[] { typeof(bool) });
        if (allData?.Invoke(persistentData, new object[] { true }) is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (var item in enumerable)
        {
            if (item is IGH_Goo goo)
            {
                yield return goo;
            }
        }
    }

    private static void SetPersistentData(IGH_Param param, IEnumerable<object> values)
    {
        var clearMethod = param.GetType().GetMethod("Script_ClearPersistentData", Type.EmptyTypes);
        var addMethod = param.GetType().GetMethod("Script_AddPersistentData", new[] { typeof(List<object>) });
        if (clearMethod is null || addMethod is null)
        {
            throw new InvalidOperationException($"Parameter '{GetDisplayLabel(param)}' does not expose persistent data scripting APIs.");
        }

        clearMethod.Invoke(param, Array.Empty<object>());
        addMethod.Invoke(param, new object[] { values.ToList() });
    }

    private void ResetStackRuntime(RhinoDoc doc, Guid objectId, ModifierStackSpec spec)
    {
        if (spec.Steps.Count == 0)
        {
            Log($"ResetStackRuntime removing empty stack. Object={objectId}");
            RemoveStackRuntime(doc, objectId);
            RaiseStateChanged();
            return;
        }

        var runtime = GetOrCreateStackRuntime(doc, objectId);
        runtime.Reset(spec.Steps.Count);
        runtime.RootRevision = NextRevision();
        Log($"Stack runtime reset. Object={objectId}, StepCount={spec.Steps.Count}, RootRevision={runtime.RootRevision}");
        QueueEvaluation(doc, objectId);
        RaiseStateChanged();
    }

    private StackRuntime GetOrCreateStackRuntime(RhinoDoc doc, Guid objectId)
    {
        var documentState = GetOrCreateDocumentState(doc);
        if (!documentState.Stacks.TryGetValue(objectId, out var runtime))
        {
            runtime = new StackRuntime
            {
                RootRevision = NextRevision(),
            };
            documentState.Stacks[objectId] = runtime;
            Log($"Stack runtime created. Doc={doc.RuntimeSerialNumber}, Object={objectId}, RootRevision={runtime.RootRevision}");
        }

        return runtime;
    }

    private StackRuntime? TryGetStackRuntime(RhinoDoc doc, Guid objectId)
    {
        return _documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState) &&
               documentState.Stacks.TryGetValue(objectId, out var runtime)
            ? runtime
            : null;
    }

    private DocumentState GetOrCreateDocumentState(RhinoDoc doc)
    {
        if (!_documents.TryGetValue(doc.RuntimeSerialNumber, out var state))
        {
            state = new DocumentState();
            _documents[doc.RuntimeSerialNumber] = state;
            Log($"Document state created. Doc={doc.RuntimeSerialNumber}");
        }

        return state;
    }

    private void RemoveStackRuntime(RhinoDoc? doc, Guid objectId)
    {
        if (doc is null)
        {
            return;
        }

        if (_documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState) &&
            documentState.Stacks.Remove(objectId, out var runtime))
        {
            Log($"Stack runtime removed. Doc={doc.RuntimeSerialNumber}, Object={objectId}");
            runtime.Dispose();
            UpdateConduitAndViews(doc);
            RaiseStateChanged();
        }
    }

    private void QueueEvaluation(RhinoDoc doc, Guid objectId)
    {
        var key = $"{doc.RuntimeSerialNumber}:{objectId}";
        if (_queuedKeys.Add(key))
        {
            _queuedStacks.Enqueue(new QueuedStack(doc.RuntimeSerialNumber, objectId, key));
            Log($"Queued stack evaluation. Doc={doc.RuntimeSerialNumber}, Object={objectId}, QueueCount={_queuedStacks.Count}");
        }
        else
        {
            Log($"Skipped queueing duplicate stack evaluation. Doc={doc.RuntimeSerialNumber}, Object={objectId}");
        }

        if (_idleAttached)
        {
            return;
        }

        RhinoApp.Idle += OnIdle;
        _idleAttached = true;
        Log("Attached Rhino idle handler for queued stack processing.");
    }

    private void UpdateConduitAndViews(RhinoDoc doc)
    {
        UpdateConduitState();
        Log($"Requesting viewport redraw. Doc={doc.RuntimeSerialNumber}, PreviewObjectCount={GetPreviewStacks(doc).Count()}");
        doc.Views.Redraw();
    }

    private void UpdateConduitState()
    {
        var enabled = _documents.Values.Any(d => d.Stacks.Count > 0);
        if (_previewConduit.Enabled != enabled)
        {
            Log($"Preview conduit {(enabled ? "enabled" : "disabled")}.");
        }

        _previewConduit.Enabled = enabled;
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool TryGetSingleSelectedObject(RhinoDoc doc, out RhinoObject? rhinoObject, out string message)
    {
        rhinoObject = null;
        message = string.Empty;

        var selected = doc.Objects.GetSelectedObjects(false, false).ToArray();
        if (selected is null || selected.Length == 0)
        {
            message = "Select one object to edit its modifier stack.";
            return false;
        }

        if (selected.Length > 1)
        {
            message = "Select a single object to edit.";
            return false;
        }

        rhinoObject = selected[0];
        if (rhinoObject is null)
        {
            message = "Selected object is unavailable.";
            return false;
        }

        if (!IsSupportedGeometryObject(rhinoObject))
        {
            message = $"Object type '{rhinoObject.ObjectType}' is not supported by the MVP.";
            return false;
        }

        return true;
    }

    private static bool IsSupportedGeometryObject(RhinoObject rhinoObject)
    {
        return rhinoObject.Geometry is Rhino.Geometry.Point or Curve or Brep or Extrusion or Mesh or SubD;
    }

    private static List<GeometryBase> CloneGeometry(IEnumerable<GeometryBase> geometry)
    {
        return geometry.Select(g => g.Duplicate()).ToList();
    }

    private ulong NextRevision()
    {
        _revisionCounter += 1;
        return _revisionCounter;
    }

    private static void Log(string message)
    {
        RhinoApp.WriteLine($"{LogPrefix}: {message}");
    }

    private static string DescribeRhinoObject(RhinoObject rhinoObject)
    {
        return $"{rhinoObject.ObjectType} {rhinoObject.Id}";
    }

    private static string DescribeGeometry(IEnumerable<GeometryBase> geometry)
    {
        var items = geometry.ToList();
        if (items.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", items
            .GroupBy(item => item.ObjectType)
            .Select(group => $"{group.Key} x{group.Count()}"));
    }

    private sealed class DocumentState : IDisposable
    {
        public Dictionary<Guid, StackRuntime> Stacks { get; } = new();

        public void Dispose()
        {
            foreach (var runtime in Stacks.Values)
            {
                runtime.Dispose();
            }

            Stacks.Clear();
        }
    }

    private sealed class StackRuntime : IDisposable
    {
        public ulong RootRevision { get; set; }

        public List<StepRuntime?> StepRuntimes { get; } = new();

        public List<GeometryBase> PreviewGeometry { get; set; } = new();

        public string ErrorMessage { get; private set; } = string.Empty;

        private List<string> _stepErrors = new();
        private List<List<StepOutputValue>> _stepOutputs = new();

        public void EnsureStepCapacity(int count)
        {
            while (StepRuntimes.Count < count)
            {
                StepRuntimes.Add(null);
            }

            if (StepRuntimes.Count > count)
            {
                for (var i = count; i < StepRuntimes.Count; i++)
                {
                    StepRuntimes[i]?.Dispose();
                }

                StepRuntimes.RemoveRange(count, StepRuntimes.Count - count);
            }

            if (_stepErrors.Count != count)
            {
                _stepErrors = Enumerable.Repeat(string.Empty, count).ToList();
            }

            if (_stepOutputs.Count < count)
            {
                while (_stepOutputs.Count < count)
                {
                    _stepOutputs.Add(new List<StepOutputValue>());
                }
            }
            else if (_stepOutputs.Count > count)
            {
                _stepOutputs.RemoveRange(count, _stepOutputs.Count - count);
            }
        }

        public void Reset(int stepCount)
        {
            foreach (var stepRuntime in StepRuntimes)
            {
                stepRuntime?.Dispose();
            }

            StepRuntimes.Clear();
            PreviewGeometry.Clear();
            ErrorMessage = string.Empty;
            _stepErrors = Enumerable.Repeat(string.Empty, stepCount).ToList();
            _stepOutputs = new List<List<StepOutputValue>>(stepCount);

            for (var i = 0; i < stepCount; i++)
            {
                StepRuntimes.Add(null);
                _stepOutputs.Add(new List<StepOutputValue>());
            }
        }

        public void DisposeStep(int index)
        {
            if (index < 0 || index >= StepRuntimes.Count)
            {
                return;
            }

            StepRuntimes[index]?.Dispose();
            StepRuntimes[index] = null;
            ClearOutputs(index);
        }

        public void ClearErrors(int stepCount)
        {
            ErrorMessage = string.Empty;
            if (_stepErrors.Count != stepCount)
            {
                _stepErrors = Enumerable.Repeat(string.Empty, stepCount).ToList();
                return;
            }

            for (var i = 0; i < _stepErrors.Count; i++)
            {
                _stepErrors[i] = string.Empty;
            }
        }

        public void SetError(int index, string message)
        {
            ErrorMessage = message;
            if (index >= 0 && index < _stepErrors.Count)
            {
                _stepErrors[index] = message;
            }
        }

        public string GetErrorForIndex(int index)
        {
            return index >= 0 && index < _stepErrors.Count ? _stepErrors[index] : string.Empty;
        }

        public IReadOnlyList<StepOutputValue> GetOutputsForIndex(int index)
        {
            return index >= 0 && index < _stepOutputs.Count
                ? _stepOutputs[index]
                : Array.Empty<StepOutputValue>();
        }

        public void ClearAllOutputs(int stepCount)
        {
            if (_stepOutputs.Count != stepCount)
            {
                _stepOutputs = Enumerable.Range(0, stepCount)
                    .Select(_ => new List<StepOutputValue>())
                    .ToList();
                return;
            }

            foreach (var outputs in _stepOutputs)
            {
                outputs.Clear();
            }
        }

        public void ClearOutputs(int index)
        {
            if (index >= 0 && index < _stepOutputs.Count)
            {
                _stepOutputs[index].Clear();
            }
        }

        public void SetOutputs(int index, IEnumerable<StepOutputValue> outputs)
        {
            if (index < 0 || index >= _stepOutputs.Count)
            {
                return;
            }

            _stepOutputs[index] = outputs.ToList();
        }

        public void Dispose()
        {
            foreach (var stepRuntime in StepRuntimes)
            {
                stepRuntime?.Dispose();
            }

            StepRuntimes.Clear();
            PreviewGeometry.Clear();
            _stepErrors.Clear();
            _stepOutputs.Clear();
        }
    }

    private sealed class StepRuntime : IDisposable
    {
        public StepRuntime(
            string path,
            DateTime lastWriteUtc,
            GH_Document document,
            DefinitionContract contract,
            Param_Geometry? sceneInputSource,
            IGH_Param? sceneInputParam,
            List<RuntimeInputBinding> inputs,
            List<RuntimeOutputBinding> outputs,
            List<RuntimeOutputBinding> geometryOutputs)
        {
            Path = path;
            LastWriteUtc = lastWriteUtc;
            Document = document;
            Contract = contract;
            SceneInputSource = sceneInputSource;
            SceneInputParam = sceneInputParam;
            Inputs = inputs;
            Outputs = outputs;
            GeometryOutputs = geometryOutputs;
            LastInputRevision = ulong.MaxValue;
        }

        public string Path { get; }

        public DateTime LastWriteUtc { get; }

        public GH_Document Document { get; }

        public DefinitionContract Contract { get; }

        public Param_Geometry? SceneInputSource { get; }

        public IGH_Param? SceneInputParam { get; }

        public List<RuntimeInputBinding> Inputs { get; }

        public List<RuntimeOutputBinding> Outputs { get; }

        public List<RuntimeOutputBinding> GeometryOutputs { get; }

        public ulong LastInputRevision { get; set; }

        public ulong LastOutputRevision { get; set; }

        public List<GeometryBase> CachedOutput { get; set; } = new();

        public List<StepOutputValue> CachedDisplayedOutputs { get; set; } = new();

        public bool HasGeometryOutputs => GeometryOutputs.Count > 0;

        public void Dispose()
        {
            Document.Dispose();
            CachedOutput.Clear();
            CachedDisplayedOutputs.Clear();
        }
    }

    private sealed class DefinitionTemplate : IDisposable
    {
        public DefinitionTemplate(string path, DateTime lastWriteUtc, GH_Document document, DefinitionContract contract)
        {
            Path = path;
            LastWriteUtc = lastWriteUtc;
            Document = document;
            Contract = contract;
        }

        public string Path { get; }

        public DateTime LastWriteUtc { get; }

        public GH_Document Document { get; }

        public DefinitionContract Contract { get; }

        public void Dispose()
        {
            Document.Dispose();
        }
    }

    private readonly record struct QueuedStack(uint DocumentSerial, Guid ObjectId, string Key);

    public readonly record struct PreviewStack(Guid SourceObjectId, IReadOnlyList<GeometryBase> Geometry);

    private sealed record DefinitionContract(
        ModifierInputDescriptor? SceneInput,
        IReadOnlyList<ModifierInputDescriptor> Inputs,
        IReadOnlyList<ModifierOutputDescriptor> Outputs,
        IReadOnlyList<ModifierOutputDescriptor> GeometryOutputs);

    private sealed record ModifierInputDescriptor(
        Guid ObjectId,
        string Id,
        string Label,
        string Description,
        ModifierIoKind Kind,
        string DefaultSerializedValue,
        bool HasDefaultValue,
        bool UsesSceneGeometryWhenBlank,
        double? Minimum,
        double? Maximum,
        int DecimalPlaces);

    private sealed record ModifierOutputDescriptor(
        Guid ObjectId,
        string Id,
        string Label,
        string Description,
        ModifierIoKind Kind);

    private sealed class RuntimeInputBinding
    {
        public RuntimeInputBinding(ModifierInputDescriptor descriptor, IGH_Param param)
        {
            Descriptor = descriptor;
            Param = param;
        }

        public RuntimeInputBinding(ModifierInputDescriptor descriptor, GH_NumberSlider slider)
        {
            Descriptor = descriptor;
            Slider = slider;
        }

        public ModifierInputDescriptor Descriptor { get; }

        public IGH_Param? Param { get; }

        public GH_NumberSlider? Slider { get; }

        public void Expire()
        {
            Param?.ExpireSolution(false);
            Slider?.ExpireSolution(false);
        }
    }

    private sealed class RuntimeOutputBinding
    {
        public RuntimeOutputBinding(ModifierOutputDescriptor descriptor, IGH_Param param)
        {
            Descriptor = descriptor;
            Param = param;
        }

        public ModifierOutputDescriptor Descriptor { get; }

        public IGH_Param Param { get; }

        public void Expire()
        {
            Param.ExpireSolution(false);
        }
    }

    private readonly record struct StepOutputValue(string Id, string DisplayValue);

    private readonly record struct StepEvaluationResult(
        bool Success,
        bool HasGeometryOutput,
        List<GeometryBase> OutputGeometry,
        List<StepOutputValue> DisplayOutputs,
        string ErrorMessage,
        int RawGeometryOutputItemCount,
        List<string> SkippedGeometryOutputTypes)
    {
        public static StepEvaluationResult Successful(
            bool hasGeometryOutput,
            List<GeometryBase> outputGeometry,
            List<StepOutputValue> displayOutputs,
            int rawGeometryOutputItemCount,
            List<string> skippedGeometryOutputTypes)
        {
            return new StepEvaluationResult(
                true,
                hasGeometryOutput,
                outputGeometry,
                displayOutputs,
                string.Empty,
                rawGeometryOutputItemCount,
                skippedGeometryOutputTypes);
        }

        public static StepEvaluationResult Fail(string message)
        {
            return new StepEvaluationResult(
                false,
                false,
                new List<GeometryBase>(),
                new List<StepOutputValue>(),
                message,
                0,
                new List<string>());
        }
    }
}
