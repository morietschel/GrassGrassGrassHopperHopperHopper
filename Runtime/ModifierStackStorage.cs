using System;
using System.Text.Json;
using HelloRhinoCommon.Models;
using Rhino;
using Rhino.DocObjects;

namespace HelloRhinoCommon.Runtime;

internal static class ModifierStackStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public static ModifierStackSpec Load(RhinoObject? rhinoObject)
    {
        if (rhinoObject is null)
        {
            return new ModifierStackSpec();
        }

        var dictionary = rhinoObject.Attributes.UserDictionary;
        if (!dictionary.ContainsKey(ModifierStackSpec.UserDictionaryKey))
        {
            return new ModifierStackSpec();
        }

        if (dictionary[ModifierStackSpec.UserDictionaryKey] is not string json || string.IsNullOrWhiteSpace(json))
        {
            return new ModifierStackSpec();
        }

        try
        {
            return JsonSerializer.Deserialize<ModifierStackSpec>(json, JsonOptions) ?? new ModifierStackSpec();
        }
        catch
        {
            return new ModifierStackSpec();
        }
    }

    public static bool Save(RhinoDoc doc, Guid objectId, ModifierStackSpec spec)
    {
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            return false;
        }

        var attributes = rhinoObject.Attributes.Duplicate();
        if (spec.Steps.Count == 0)
        {
            attributes.UserDictionary.Remove(ModifierStackSpec.UserDictionaryKey);
        }
        else
        {
            var json = JsonSerializer.Serialize(spec, JsonOptions);
            attributes.UserDictionary[ModifierStackSpec.UserDictionaryKey] = json;
        }

        return doc.Objects.ModifyAttributes(rhinoObject, attributes, true);
    }
}
