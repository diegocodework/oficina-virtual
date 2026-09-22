using System;
using System.IO;
using System.Text.Json;

namespace OficinaVirtual.Services;

/// <summary>
/// Guarda/carga el workspace en la carpeta de datos del usuario de Windows — funciona igual para
/// cualquier cuenta o máquina, sin ningún ajuste manual.
/// </summary>
internal static class WorkspaceService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OficinaVirtual", "workspace.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Save(WorkspaceFile workspace)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (dir != null) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(workspace, JsonOptions);
        File.WriteAllText(FilePath, json);
    }

    public static WorkspaceFile? Load()
    {
        if (!File.Exists(FilePath)) return null;

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<WorkspaceFile>(json);
        }
        catch
        {
            // Archivo corrupto o de una versión incompatible: se ignora en vez de impedir arrancar la app.
            return null;
        }
    }
}
