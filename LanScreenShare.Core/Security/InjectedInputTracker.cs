using System;
using System.Collections.Generic;
using System.Linq;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Security;

/// <summary>
/// Registra y rastrea de manera segura y concurrente únicamente las teclas y botones de ratón
/// efectivamente inyectados por LanScreenShare, para permitir su liberación idempotente
/// exclusiva ante desconexión o revocación sin interferir con el teclado físico del anfitrión.
/// Especificación: ADR-001 Sección 9.
/// </summary>
public class InjectedInputTracker
{
    private readonly object _lock = new();
    private readonly HashSet<int> _pressedKeys = new();
    private readonly HashSet<RemoteMouseButton> _pressedMouseButtons = new();

    /// <summary>
    /// Registra el estado de una tecla según el resultado de inyección.
    /// </summary>
    public void RecordKey(int keyCode, bool isDown)
    {
        if (keyCode <= 0 || keyCode > 0xFF) return;

        lock (_lock)
        {
            if (isDown)
            {
                _pressedKeys.Add(keyCode);
            }
            else
            {
                _pressedKeys.Remove(keyCode);
            }
        }
    }

    /// <summary>
    /// Registra el estado de un botón de ratón según el resultado de inyección.
    /// </summary>
    public void RecordMouseButton(RemoteMouseButton button, bool isDown)
    {
        if (button == RemoteMouseButton.None) return;

        lock (_lock)
        {
            if (isDown)
            {
                _pressedMouseButtons.Add(button);
            }
            else
            {
                _pressedMouseButtons.Remove(button);
            }
        }
    }

    /// <summary>
    /// Comprueba si una tecla está actualmente registrada como presionada.
    /// </summary>
    public bool IsKeyPressed(int keyCode)
    {
        lock (_lock)
        {
            return _pressedKeys.Contains(keyCode);
        }
    }

    /// <summary>
    /// Comprueba si un botón de ratón está actualmente registrado como presionado.
    /// </summary>
    public bool IsMouseButtonPressed(RemoteMouseButton button)
    {
        lock (_lock)
        {
            return _pressedMouseButtons.Contains(button);
        }
    }

    /// <summary>
    /// Obtiene una instantánea de las teclas actualmente inyectadas y pendientes de liberación.
    /// </summary>
    public int[] GetPressedKeysSnapshot()
    {
        lock (_lock)
        {
            return _pressedKeys.ToArray();
        }
    }

    /// <summary>
    /// Obtiene una instantánea de los botones de ratón actualmente inyectados y pendientes de liberación.
    /// </summary>
    public RemoteMouseButton[] GetPressedMouseButtonsSnapshot()
    {
        lock (_lock)
        {
            return _pressedMouseButtons.ToArray();
        }
    }

    /// <summary>
    /// Limpia todo el registro interno de teclas y botones inyectados.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _pressedKeys.Clear();
            _pressedMouseButtons.Clear();
        }
    }
}

