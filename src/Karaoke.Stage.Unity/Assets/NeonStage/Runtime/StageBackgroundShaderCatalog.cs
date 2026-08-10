using System;
using System.Linq;
using UnityEngine;

namespace NeonStage.Stage
{

/// <summary>
/// Resolves a background shader without coupling StageVisualView to a concrete
/// implementation. New backgrounds need only a shader below Resources and an
/// entry in NeonStageBackgrounds.json.
/// </summary>
internal static class StageBackgroundShaderCatalog
{
    private const string CatalogResource = "NeonStageBackgrounds";
    private const string LegacyDefaultResource = "NeonBackdrop";
    private const string PreferenceKey = "NeonStage.BackgroundShader";
    private const string EnvironmentVariable = "NEONSTAGE_BACKGROUND_SHADER";
    private const string ArgumentName = "--background-shader";

    internal static Material CreateMaterial()
    {
        var catalog = LoadCatalog();
        var requestedId = ResolveRequestedId(catalog.defaultId);
        var selected = Find(catalog, requestedId);
        if (selected == null && !string.Equals(requestedId, catalog.defaultId, StringComparison.OrdinalIgnoreCase))
            Debug.LogWarning($"Unknown background shader ID '{requestedId}'; using catalog default '{catalog.defaultId}'.");
        selected ??= Find(catalog, catalog.defaultId) ?? catalog.backgrounds.FirstOrDefault();
        var shader = selected == null ? null : Resources.Load<Shader>(selected.resource);

        if (shader == null && selected != null)
            Debug.LogWarning($"Background shader '{selected.id}' could not be loaded from Resources/{selected.resource}.");
        shader ??= Resources.Load<Shader>(LegacyDefaultResource);
        if (shader == null)
            throw new InvalidOperationException("Neon Stage could not load a background shader. Check NeonStageBackgrounds.json and Assets/Resources.");

        Debug.Log($"Neon Stage background shader: {selected?.id ?? LegacyDefaultResource} ({shader.name})");
        return new Material(shader) { name = $"Neon Stage Background · {selected?.id ?? "legacy"}" };
    }

    private static StageBackgroundCatalog LoadCatalog()
    {
        var asset = Resources.Load<TextAsset>(CatalogResource);
        if (asset == null)
        {
            Debug.LogWarning($"Resources/{CatalogResource}.json is missing; using the legacy background.");
            return new StageBackgroundCatalog
            {
                defaultId = "neon-grid",
                backgrounds = new[] { new StageBackgroundDefinition { id = "neon-grid", resource = LegacyDefaultResource } }
            };
        }

        var catalog = JsonUtility.FromJson<StageBackgroundCatalog>(asset.text);
        if (catalog?.backgrounds == null || catalog.backgrounds.Length == 0)
            throw new InvalidOperationException($"Resources/{CatalogResource}.json does not contain any background shaders.");
        return catalog;
    }

    private static StageBackgroundDefinition? Find(StageBackgroundCatalog catalog, string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : catalog.backgrounds.FirstOrDefault(item =>
                string.Equals(item.id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string ResolveRequestedId(string defaultId)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].Equals(ArgumentName, StringComparison.OrdinalIgnoreCase) && index + 1 < arguments.Length)
                return arguments[index + 1].Trim();
            var prefix = ArgumentName + "=";
            if (arguments[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return arguments[index].Substring(prefix.Length).Trim();
        }

        var environment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environment)) return environment.Trim();
        var preference = PlayerPrefs.GetString(PreferenceKey, "");
        return string.IsNullOrWhiteSpace(preference) ? defaultId : preference.Trim();
    }

    [Serializable]
    private sealed class StageBackgroundCatalog
    {
        public string defaultId = "neon-grid";
        public StageBackgroundDefinition[] backgrounds = Array.Empty<StageBackgroundDefinition>();
    }

    [Serializable]
    private sealed class StageBackgroundDefinition
    {
        public string id = "";
        public string displayName = "";
        public string resource = "";
    }
}
}
