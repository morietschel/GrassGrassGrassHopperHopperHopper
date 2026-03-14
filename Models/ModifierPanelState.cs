using System;
using System.Collections.Generic;

namespace HelloRhinoCommon.Models;

internal sealed class ModifierPanelState
{
    public bool CanEdit { get; init; }

    public Guid? SelectedObjectId { get; init; }

    public string SelectionLabel { get; init; } = string.Empty;

    public string StatusMessage { get; init; } = string.Empty;

    public IReadOnlyList<ModifierStepPanelState> Steps { get; init; } = Array.Empty<ModifierStepPanelState>();
}

internal sealed class ModifierStepPanelState
{
    public int Index { get; init; }

    public bool Enabled { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string FullPath { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;

    public IReadOnlyList<ModifierStepInputPanelState> Inputs { get; init; } = Array.Empty<ModifierStepInputPanelState>();

    public IReadOnlyList<ModifierStepOutputPanelState> Outputs { get; init; } = Array.Empty<ModifierStepOutputPanelState>();
}

internal enum ModifierIoKind
{
    Number,
    NumberSlider,
    Point,
    String,
    Boolean,
    Color,
    Geometry,
}

internal sealed class ModifierStepInputPanelState
{
    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public ModifierIoKind Kind { get; init; }

    public string SerializedValue { get; init; } = string.Empty;

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public int DecimalPlaces { get; init; }

    public bool IsReadOnly { get; init; }
}

internal sealed class ModifierStepOutputPanelState
{
    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public ModifierIoKind Kind { get; init; }

    public string DisplayValue { get; init; } = string.Empty;
}
