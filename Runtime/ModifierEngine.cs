using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
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
            steps.Add(new ModifierStepPanelState
            {
                Index = i,
                Enabled = step.Enabled,
                FullPath = step.Path,
                DisplayName = Path.GetFileName(step.Path),
                ErrorMessage = runtime?.GetErrorForIndex(i) ?? string.Empty,
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

    public IEnumerable<GeometryBase> GetPreviewGeometry(RhinoDoc? doc)
    {
        if (doc is null)
        {
            yield break;
        }

        if (!_documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState))
        {
            yield break;
        }

        foreach (var runtime in documentState.Stacks.Values)
        {
            foreach (var geometry in runtime.PreviewGeometry)
            {
                yield return geometry;
            }
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
                currentGeometry = CloneGeometry(stepRuntime.CachedOutput);
                upstreamRevision = stepRuntime.LastOutputRevision;
                continue;
            }

            Log($"Step {i} solving. Modifier={Path.GetFileName(stepSpec.Path)}, InputRevision={upstreamRevision}, InputCount={currentGeometry.Count}, InputSummary={DescribeGeometry(currentGeometry)}");
            var result = EvaluateStep(stepRuntime, currentGeometry);
            if (!result.Success)
            {
                runtime.SetError(i, result.ErrorMessage);
                Log($"Step {i} solve failed for '{Path.GetFileName(stepSpec.Path)}'. {result.ErrorMessage}");
                failedAtFirstEnabledStep = i == firstEnabledIndex;
                break;
            }

            stepRuntime.CachedOutput = result.Output;
            stepRuntime.LastInputRevision = upstreamRevision;
            stepRuntime.LastOutputRevision = NextRevision();
            currentGeometry = CloneGeometry(stepRuntime.CachedOutput);
            upstreamRevision = stepRuntime.LastOutputRevision;
            anyStepSucceeded = true;
            Log($"Step {i} solve complete. Modifier={Path.GetFileName(stepSpec.Path)}, OutputRevision={stepRuntime.LastOutputRevision}, RawOutputCount={result.RawOutputItemCount}, ConvertedOutputCount={currentGeometry.Count}, OutputSummary={DescribeGeometry(currentGeometry)}");
            if (result.SkippedOutputTypes.Count > 0)
            {
                Log($"Step {i} skipped output items. Modifier={Path.GetFileName(stepSpec.Path)}, Skipped={string.Join(", ", result.SkippedOutputTypes)}");
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

        var inputParam = FindContractParam(document, InputAliases);
        var outputParam = FindContractParam(document, OutputAliases);

        if (inputParam is null || outputParam is null)
        {
            document.Dispose();
            throw new InvalidOperationException($"Modifier '{Path.GetFileName(fullPath)}' must expose exactly one GeoIn/GeomIn and GeoOut/GeomOut parameter.");
        }

        if (inputParam.Sources.Count > 0)
        {
            document.Dispose();
            throw new InvalidOperationException($"Modifier '{Path.GetFileName(fullPath)}' must leave its input parameter unwired.");
        }

        var sourceParam = CreateRuntimeSourceParam(document, inputParam);
        Log($"Step {index} runtime created. Modifier={Path.GetFileName(fullPath)}, Input={inputParam.NickName}, Output={outputParam.NickName}");
        var stepRuntime = new StepRuntime(fullPath, lastWriteUtc, document, sourceParam, inputParam, outputParam);
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

        var template = new DefinitionTemplate(fullPath, lastWriteUtc, io.Document);
        _definitionCache[fullPath] = template;
        Log($"Definition template cached. Path={fullPath}, LastWriteUtc={lastWriteUtc:O}");
        return template;
    }

    private static StepEvaluationResult EvaluateStep(StepRuntime runtime, IReadOnlyList<GeometryBase> inputGeometry)
    {
        if (!GeometryConversion.TryToGooList(inputGeometry, out var goos, out var error))
        {
            return StepEvaluationResult.Fail(error);
        }

        runtime.SourceParam.ClearData();
        runtime.OutputParam.ClearData();

        if (goos.Count > 0 && !runtime.SourceParam.AddVolatileDataList(new GH_Path(0), goos))
        {
            return StepEvaluationResult.Fail($"Failed to push geometry into '{Path.GetFileName(runtime.Path)}'.");
        }

        runtime.SourceParam.ExpireSolution(false);
        runtime.InputParam.ExpireSolution(false);
        runtime.OutputParam.ExpireSolution(false);

        // Force the duplicated definition to fully recompute from the injected runtime input,
        // then explicitly collect the contract output param before reading its volatile data.
        runtime.Document.NewSolution(true, GH_SolutionMode.Silent);
        runtime.OutputParam.CollectData();
        runtime.OutputParam.ComputeData();

        var output = GeometryConversion.ReadOutput(runtime.OutputParam);
        return StepEvaluationResult.Successful(output.Geometry, output.TotalItemCount, output.SkippedTypes);
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

    private static IGH_Param? FindContractParam(GH_Document document, IEnumerable<string> aliases)
    {
        return document.Objects
            .OfType<IGH_Param>()
            .FirstOrDefault(param => aliases.Any(alias => param.NickName.Equals(alias, StringComparison.OrdinalIgnoreCase)));
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
        Log($"Requesting viewport redraw. Doc={doc.RuntimeSerialNumber}, PreviewObjectCount={GetPreviewGeometry(doc).Count()}");
        doc.Views.Redraw();
    }

    private void UpdateConduitState()
    {
        var enabled = _documents.Values.Any(d => d.Stacks.Values.Any(s => s.PreviewGeometry.Count > 0));
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

            for (var i = 0; i < stepCount; i++)
            {
                StepRuntimes.Add(null);
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

        public void Dispose()
        {
            foreach (var stepRuntime in StepRuntimes)
            {
                stepRuntime?.Dispose();
            }

            StepRuntimes.Clear();
            PreviewGeometry.Clear();
            _stepErrors.Clear();
        }
    }

    private sealed class StepRuntime : IDisposable
    {
        public StepRuntime(string path, DateTime lastWriteUtc, GH_Document document, Param_Geometry sourceParam, IGH_Param inputParam, IGH_Param outputParam)
        {
            Path = path;
            LastWriteUtc = lastWriteUtc;
            Document = document;
            SourceParam = sourceParam;
            InputParam = inputParam;
            OutputParam = outputParam;
            LastInputRevision = ulong.MaxValue;
        }

        public string Path { get; }

        public DateTime LastWriteUtc { get; }

        public GH_Document Document { get; }

        public Param_Geometry SourceParam { get; }

        public IGH_Param InputParam { get; }

        public IGH_Param OutputParam { get; }

        public ulong LastInputRevision { get; set; }

        public ulong LastOutputRevision { get; set; }

        public List<GeometryBase> CachedOutput { get; set; } = new();

        public void Dispose()
        {
            Document.Dispose();
            CachedOutput.Clear();
        }
    }

    private sealed class DefinitionTemplate : IDisposable
    {
        public DefinitionTemplate(string path, DateTime lastWriteUtc, GH_Document document)
        {
            Path = path;
            LastWriteUtc = lastWriteUtc;
            Document = document;
        }

        public string Path { get; }

        public DateTime LastWriteUtc { get; }

        public GH_Document Document { get; }

        public void Dispose()
        {
            Document.Dispose();
        }
    }

    private readonly record struct QueuedStack(uint DocumentSerial, Guid ObjectId, string Key);

    private readonly record struct StepEvaluationResult(bool Success, List<GeometryBase> Output, string ErrorMessage, int RawOutputItemCount, List<string> SkippedOutputTypes)
    {
        public static StepEvaluationResult Successful(List<GeometryBase> output, int rawOutputItemCount, List<string> skippedOutputTypes)
        {
            return new StepEvaluationResult(true, output, string.Empty, rawOutputItemCount, skippedOutputTypes);
        }

        public static StepEvaluationResult Fail(string message)
        {
            return new StepEvaluationResult(false, new List<GeometryBase>(), message, 0, new List<string>());
        }
    }
}
