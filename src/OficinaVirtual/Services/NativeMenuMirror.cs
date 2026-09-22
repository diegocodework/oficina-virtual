using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace OficinaVirtual.Services;

/// <summary>
/// Windows no dibuja la barra de menú clásica (Archivo, Edición...) de una ventana que es hija de
/// otra, que es como quedan las ventanas embebidas. Esto la replica en WPF, genérico para cualquier
/// app con menú clásico: lee el menú nativo, lo muestra y, al elegir una opción, le envía a la app
/// el mismo WM_COMMAND que recibiría si se hubiera usado su propio menú.
/// Los menús son objetos del escritorio de Windows, legibles desde otro proceso.
/// </summary>
internal static class NativeMenuMirror
{
    private const uint InitMenuTimeoutMs = 300;

    /// <summary>Rellena <paramref name="target"/> con el menú de la ventana. False si no tiene menú utilizable.</summary>
    public static bool TryBuild(IntPtr ownerHwnd, IntPtr hMenu, Menu target)
    {
        target.Items.Clear();
        if (hMenu == IntPtr.Zero || !Win32.IsMenu(hMenu)) return false;

        int count = Win32.GetMenuItemCount(hMenu);
        for (int i = 0; i < count; i++)
        {
            if (BuildItem(ownerHwnd, hMenu, i, isTopLevel: true) is { } item)
                target.Items.Add(item);
        }

        return target.Items.Count > 0;
    }

    private static object? BuildItem(IntPtr ownerHwnd, IntPtr hMenu, int position, bool isTopLevel)
    {
        var info = new Win32.MENUITEMINFO
        {
            cbSize = (uint)Marshal.SizeOf<Win32.MENUITEMINFO>(),
            fMask = Win32.MIIM_FTYPE | Win32.MIIM_STATE | Win32.MIIM_ID | Win32.MIIM_SUBMENU
        };
        if (!Win32.GetMenuItemInfo(hMenu, (uint)position, true, ref info)) return null;

        if ((info.fType & Win32.MFT_SEPARATOR) != 0)
            return isTopLevel ? null : new Separator();

        string text = ReadText(hMenu, position);
        if (string.IsNullOrWhiteSpace(text)) return null; // elementos solo-imagen o dibujados por la app

        int tab = text.IndexOf('\t');
        string label = tab >= 0 ? text[..tab] : text;
        string gesture = tab >= 0 ? text[(tab + 1)..] : "";

        var menuItem = new MenuItem
        {
            Header = ToWpfAccessText(label),
            InputGestureText = gesture,
            IsEnabled = (info.fState & Win32.MFS_DISABLED) == 0,
            IsChecked = (info.fState & Win32.MFS_CHECKED) != 0
        };

        // Los desplegables usan el aspecto claro por defecto de WPF: su texto no debe heredar el
        // color claro de la cabecera oscura (quedaría blanco sobre blanco).
        if (!isTopLevel)
            menuItem.Foreground = SystemColors.MenuTextBrush;

        if (info.hSubMenu != IntPtr.Zero)
        {
            IntPtr subMenu = info.hSubMenu;
            menuItem.Items.Add(new MenuItem { Header = "…", IsEnabled = false, Foreground = SystemColors.GrayTextBrush });
            menuItem.SubmenuOpened += (_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, menuItem)) return;
                PopulateSubmenu(ownerHwnd, subMenu, position, menuItem);
            };
        }
        else
        {
            uint id = info.wID & 0xFFFF;
            menuItem.Click += (_, _) => Win32.PostMessage(ownerHwnd, Win32.WM_COMMAND, (IntPtr)id, IntPtr.Zero);
        }

        return menuItem;
    }

    /// <summary>
    /// Justo antes de abrir un desplegable, la app recibe WM_INITMENUPOPUP (como con su menú real)
    /// para que actualice qué está activo o marcado — p.ej. "Copiar" solo con texto seleccionado.
    /// Después se leen los elementos ya actualizados.
    /// </summary>
    private static void PopulateSubmenu(IntPtr ownerHwnd, IntPtr subMenu, int positionInParent, MenuItem parent)
    {
        Win32.SendMessageTimeout(ownerHwnd, Win32.WM_INITMENUPOPUP, subMenu, (IntPtr)positionInParent,
            Win32.SMTO_ABORTIFHUNG, InitMenuTimeoutMs, out _);

        parent.Items.Clear();
        if (Win32.IsMenu(subMenu))
        {
            int count = Win32.GetMenuItemCount(subMenu);
            for (int i = 0; i < count; i++)
            {
                if (BuildItem(ownerHwnd, subMenu, i, isTopLevel: false) is { } item)
                    parent.Items.Add(item);
            }
        }

        if (parent.Items.Count == 0)
            parent.Items.Add(new MenuItem { Header = "(vacío)", IsEnabled = false, Foreground = SystemColors.GrayTextBrush });
    }

    private static string ReadText(IntPtr hMenu, int position)
    {
        var sb = new StringBuilder(256);
        int length = Win32.GetMenuString(hMenu, (uint)position, sb, sb.Capacity, Win32.MF_BYPOSITION);
        return length > 0 ? sb.ToString() : "";
    }

    /// <summary>"&Archivo" (Windows) → "_Archivo" (WPF); "&&" es un & literal; los "_" literales se escapan.</summary>
    private static string ToWpfAccessText(string windowsText)
    {
        var sb = new StringBuilder(windowsText.Length + 4);
        for (int i = 0; i < windowsText.Length; i++)
        {
            char c = windowsText[i];
            if (c == '&')
            {
                if (i + 1 < windowsText.Length && windowsText[i + 1] == '&')
                {
                    sb.Append('&');
                    i++;
                }
                else
                {
                    sb.Append('_');
                }
            }
            else if (c == '_')
            {
                sb.Append("__");
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
