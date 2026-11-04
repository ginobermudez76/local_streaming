# Plan de Implementación: Refinamiento de Arquitectura y UI en LanScreenShare

Este plan recoge, prioriza y estructura técnicamente todas las observaciones, advertencias y recomendaciones arquitectónicas derivadas de la evaluación externa. Se mantendrá actualizado dinámicamente conforme avance la conversación.

---

## 📌 Estado del Repositorio
* **Rama de trabajo activa:** `feature/ui-and-architecture-refinement`
* **Base:** `main` (commit `fbda5b8`)
* **Framework y runtime:** .NET 9.0 (C# 13), Avalonia 12.0.2, Vortice 3.8.3, NAudio 3.3.0.

---

## 🗺️ Mapa de Fases de Implementación

```mermaid
flowchart TD
    subgraph Fase 1: Honestidad Técnica y UI Inmediata
        F1A["1.1 Corregir etiquetas engañosas en UI (H.264 CPU vs GPU)"]
        F1B["1.2 Reorganizar barra lateral con Expanders (Video, Audio, Seguridad, Dispositivos)"]
        F1C["1.3 Diferenciar Resolución de Captura vs Resolución de Salida"]
        F1D["1.4 Rediseñar aviso de Administrador (badge/tarjeta contextual sin comprimir)"]
    end

    subgraph Fase 2: Control Remoto y Seguridad Real
        F2A["2.1 Textos claros en autorización (Desactivado / Manual / PIN)"]
        F2B["2.2 Token de sesión y botón de revocación inmediata (Killswitch)"]
        F2C["2.3 Validación rigurosa de paquetes binarios en puerto 5003"]
    end

    subgraph Fase 3: Perfiles de Transmisión
        F3A["3.1 Modo Sencillo con 3 Presets (Calidad, Equilibrado, Red Lenta)"]
        F3B["3.2 Modo Avanzado colapsable con controles granulares"]
    end

    subgraph Fase 4: Pipeline de Video y Aceleración MFT Real
        F4A["4.1 Detección dinámica de Hardware MFT con MFTEnumEx (NVENC/QuickSync/AMF)"]
        F4B["4.2 Fallback explícito y transparente a MFT Software si no hay GPU"]
        F4C["4.3 Conversión de color optimizada (Investigar Direct3D11 / Compute Shader)"]
    end

    subgraph Fase 5: Telemetría, Latencia y Sincronización A/V
        F5A["5.1 Panel de métricas reales (FPS host vs red, Bitrate MB/s, RTT)"]
        F5B["5.2 Descarte activo de buffers y evaluación de transporte"]
        F5C["5.3 Sincronización de reloj común entre canal 5001 (video) y 5002 (audio)"]
    end

    Fase 1 --> Fase 2 --> Fase 3 --> Fase 4 --> Fase 5
```

---

## Detalle de Tareas por Fase

### 🚀 Fase 1: Claridad Técnica y Ergonomía Visual (Inmediata)
> [!IMPORTANT]
> El objetivo es resolver la saturación visual de la barra lateral y transparentar el estado real de la aplicación sin alterar la lógica de transporte de forma invasiva.

1. **Corrección de etiquetas de códec:**
   * Cambiar `H.264 / AVC (Aceleración GPU)` a `H.264 / AVC (Media Foundation)` para no inducir a error mientras se completa la Fase 4.
   * Mantener el título dinámico `Tasa de Bits (Bitrate H.264)` cuando se use H.264 y `Calidad JPEG` cuando se use JPEG.
2. **Reorganización de la barra lateral en `Expander`:**
   * **Sección 1: Video y Pantalla** (Selector de fuente, códec, fps, resolución y calidad/bitrate).
   * **Sección 2: Audio y Micrófono** (Toggles dinámicos en caliente, controles de volumen del visor).
   * **Sección 3: Control Remoto y Seguridad** (Permitir control, modo de aprobación, botón de pánico).
   * **Sección 4: Dispositivos en LAN** (Buscador y lista de pares detectados).
3. **Diferenciación de resolución de entrada y salida:**
   * Etiqueta explícita: `Captura: 1920×1080` → `Transmisión: 1366×768 (Escala 70%)`.
4. **Optimización del aviso de Administrador:**
   * Transformar el banner fijo que se corta en una tarjeta informativa dentro de la sección de seguridad o un badge con estado interactivo que no desplace el scroll ni comprima la UI.

---

### 🛡️ Fase 2: Seguridad y Autorización del Control Remoto (Puerto 5003)
> [!WARNING]
> La recepción de paquetes `SendInput` en una red compartida sin autenticación supone un vector de riesgo que debe acotarse. La aprobación interactiva en UI debe estar respaldada por identidad de sesión criptográfica.

1. **Clarificación del selector de modo de control:**
   * Reemplazar los textos ambiguos por:
     * `Solo Visualización (Control desactivado)`
     * `Solicitar Aprobación al Anfitrión (Recomendado, con diálogo interactivo)`
     * `Acceso Directo con PIN Temporal`
2. **Autenticación e Identidad de Sesión:**
   * Generar un token de sesión/nonce criptográfico en el handshake de aprobación.
   * Vincular los paquetes del puerto 5003 al token concedido; rechazar cualquier paquete que no provenga de la sesión aprobada.
   * Prevenir inyección previa a la aprobación: el socket de control no procesa ningún mensaje de entrada hasta que la promesa `ClientApprovalCallback` retorne afirmativamente.
3. **Botón de revocación inmediata (*Panic Button*):**
   * Cuando un cliente tenga el control remoto activo, mostrar un botón prominente en el anfitrión: `Revocar Control Remoto`, que cierre el socket inmediatamente y limpie cualquier pulsación retenida en `SendInput`.

---

### 🎛️ Fase 3: Perfiles de Transmisión (Modo Sencillo vs Avanzado)
1. **Selector de Perfil Rápido:**
   * **Calidad Alta / Nitidez:** Resolución nativa (100%), 60 FPS, Bitrate 12 Mbps (H.264) / 85% (JPEG). Ideal para texto, programación y diseño.
   * **Equilibrado (Recomendado):** Resolución 70-80%, 60 FPS, Bitrate 8 Mbps (H.264) / 75% (JPEG). Balance óptimo para uso general.
   * **Red Limitada / Fluidez:** Resolución 50%, 30 FPS, Bitrate 3 Mbps (H.264) / 50% (JPEG). Para redes Wi-Fi débiles o tráfico congestionado.
2. **Toggle a Modo Avanzado:**
   * Permite desplegar los sliders y comboboxes manuales (motor DXGI/GDI, códec H264/JPEG, FPS exactos, resolución forzada y bitrate personalizado).

---

### ⚙️ Fase 4: Pipeline de Video y Aceleración MFT Real
1. **Enumeración dinámica de MFT por Hardware:**
   * Usar `MFTEnumEx` de Media Foundation con el flag `MFT_ENUM_FLAG_HARDWARE` y `MFT_CATEGORY_VIDEO_ENCODER`.
   * Identificar y seleccionar el MFT provisto por el fabricante de la GPU instalada (NVIDIA NVENC, Intel QuickSync, AMD AMF).
2. **Fallback transparente:**
   * Si no se detecta hardware compatible o la GPU rechaza el formato, degradar automáticamente al codificador de software de Microsoft (`msmpeg2venc.dll`), reflejándolo fielmente en la interfaz con un badge: `H.264 (Software CPU)`.
3. **Optimización de conversión de color:**
   * Estudiar la conversión BGRA → NV12 en la GPU (usando Direct3D11 Video Processor o Compute Shader) para eliminar el cuello de botella de la copia y conversión en CPU.

---

### 📊 Fase 5: Métricas de Red, Latencia y Sincronización A/V
1. **Telemetría transparente en pantalla:**
   * Mostrar métricas diferenciadas:
     * *FPS de Captura (Host)* vs *FPS de Renderizado (Visor)*.
     * *Bitrate real de transferencia* en MB/s y Mbps.
     * *Tiempo de codificación / captura* en milisegundos.
2. **Sincronización Audiovisual:**
   * Implementar un algoritmo de alineación basado en los timestamps compartidos entre el canal de audio (puerto 5002) y video (puerto 5001).
3. **Manejo de congestión:**
   * Monitorear si el descarte en la cola de la aplicación es suficiente en la LAN o si se deben afinar los tamaños de búfer de socket TCP (`SendBufferSize` y `ReceiveBufferSize`) para minimizar el bloqueo de cabecera de línea (*head-of-line blocking*).

---

## 📝 Registro de Actualizaciones del Plan
* **2026-11-04 (Paso 2):** Incorporación del Inventario Funcional Verificable y matriz de limitaciones conocidas (Reconexión automática ausente, control multicliente concurrente sin exclusividad, límites de DXGI ante pantalla completa exclusiva / minimización de ventanas, y compresión de audio PCM).
* **2026-11-04 (Paso 1):** Creación inicial del plan en la rama `feature/ui-and-architecture-refinement` tras el análisis y contraste técnico con la IA externa.

