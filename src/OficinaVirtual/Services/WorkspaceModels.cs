using System.Collections.Generic;

namespace OficinaVirtual.Services;

/// <summary>
/// Modelo serializable (System.Text.Json) del workspace completo: todas las pestañas, con sus
/// nodos (apps embebidas) y cables. Se guarda/carga vía <see cref="WorkspaceService"/>.
/// </summary>
public sealed class WorkspaceFile
{
    public List<WorkspaceTab> Tabs { get; set; } = new();

    /// <summary>Posición/tamaño de la propia ventana de la oficina (null en workspaces antiguos).</summary>
    public WorkspaceWindowPlacement? Window { get; set; }
}

public sealed class WorkspaceWindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed class WorkspaceTab
{
    public string Name { get; set; } = "";
    public List<WorkspaceNode> Nodes { get; set; } = new();
    public List<WorkspaceConnection> Connections { get; set; } = new();

    /// <summary>Vista del canvas (pan/zoom). Null en workspaces antiguos → se centra la vista.</summary>
    public double? ViewX { get; set; }
    public double? ViewY { get; set; }
    public double? ViewScale { get; set; }
}

/// <summary>
/// Una app embebida. Al restaurar se lanza una instancia NUEVA de ExecutablePath — nunca se
/// intenta "recuperar" una ventana ya abierta del usuario.
/// </summary>
public sealed class WorkspaceNode
{
    public string Id { get; set; } = "";
    public string ExecutablePath { get; set; } = "";

    /// <summary>
    /// Reservado para una fase futura (recordar el proyecto/carpeta/URL exacta). Vacío en esta
    /// versión: cada app se reabre en su estado inicial normal.
    /// </summary>
    public string LaunchArgs { get; set; } = "";

    /// <summary>
    /// Acceso directo con el que se abrió (si se sabe): al relanzar sin documento se usa este, para
    /// que p.ej. una consola salga con sus mismos colores y fuente.
    /// </summary>
    public string LaunchShortcut { get; set; } = "";

    public string Title { get; set; } = "";
    public int HomeCol { get; set; }
    public int HomeRow { get; set; }

    /// <summary>
    /// Última posición/tamaño de la ventana expandida, en coordenadas de canvas. Null si nunca se
    /// expandió (o en workspaces antiguos) → la primera vez se abre centrada sobre el icono.
    /// </summary>
    public double? ExpandedLeft { get; set; }
    public double? ExpandedTop { get; set; }
    public double? ExpandedWidth { get; set; }
    public double? ExpandedHeight { get; set; }
}

public sealed class WorkspaceConnection
{
    public string FromNodeId { get; set; } = "";
    public string FromSide { get; set; } = "";
    public string ToNodeId { get; set; } = "";
    public string ToSide { get; set; } = "";
}
