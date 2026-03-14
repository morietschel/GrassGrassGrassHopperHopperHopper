using System.Collections.Generic;

namespace HelloRhinoCommon.Models;

internal sealed class ModifierStackSpec
{
    internal const string UserDictionaryKey = "GGH.ModifierStack.v1";

    public int Version { get; set; } = 1;

    public List<ModifierStepSpec> Steps { get; set; } = new();

    public ModifierStackSpec Clone()
    {
        var clone = new ModifierStackSpec
        {
            Version = Version,
        };

        foreach (var step in Steps)
        {
            clone.Steps.Add(step.Clone());
        }

        return clone;
    }
}

internal sealed class ModifierStepSpec
{
    public string Path { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public ModifierStepSpec Clone()
    {
        return new ModifierStepSpec
        {
            Path = Path,
            Enabled = Enabled,
        };
    }
}
