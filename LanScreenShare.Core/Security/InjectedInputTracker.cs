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
        if (keyCode is > 0 and <= 0xFF)
        {
            UpdateState(_pressedKeys, keyCode, isDown);
        }
    }

    /// <summary>
    /// Registra el estado de un botón de ratón según el resultado de inyección.
    /// </summary>
    public void RecordMouseButton(RemoteMouseButton button, bool isDown)
    {
        if (button != RemoteMouseButton.None)
        {
            UpdateState(_pressedMouseButtons, button, isDown);
        }
    }

    private void UpdateState<T>(HashSet<T> set, T item, bool isDown)
    {
        lock (_lock)
        {
            if (isDown)
            {
                set.Add(item);
            }
            else
            {
                set.Remove(item);
            }
        }
    }

    /// <summary>
    /// Comprueba si una tecla está actualmente registrada como presionada.
    /// </summary>
    public bool IsKeyPressed(int keyCode) => ContainsItem(_pressedKeys, keyCode);

    /// <summary>
    /// Comprueba si un botón de ratón está actualmente registrado como presionado.
    /// </summary>
    public bool IsMouseButtonPressed(RemoteMouseButton button) => ContainsItem(_pressedMouseButtons, button);

    private bool ContainsItem<T>(HashSet<T> set, T item)
    {
        lock (_lock)
        {
            return set.Contains(item);
        }
    }

    /// <summary>
    /// Obtiene una instantánea de las teclas actualmente inyectadas y pendientes de liberación.
    /// </summary>
    public int[] GetPressedKeysSnapshot() => GetSnapshot(_pressedKeys);

    /// <summary>
    /// Obtiene una instantánea de los botones de ratón actualmente inyectados y pendientes de liberación.
    /// </summary>
    public RemoteMouseButton[] GetPressedMouseButtonsSnapshot() => GetSnapshot(_pressedMouseButtons);

    private T[] GetSnapshot<T>(HashSet<T> set)
    {
        lock (_lock)
        {
            return set.ToArray();
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

