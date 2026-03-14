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
}
