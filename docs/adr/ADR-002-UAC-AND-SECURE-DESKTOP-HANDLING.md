# ADR-002: Arquitectura para Soporte de UAC, Winlogon y Escritorio Seguro en LanScreenShare

* **Estado:** Aprobado
* **Fecha:** 2026-11-13
* **Rama:** `feature/issue-16-uac-secure-desktop-support`
* **Módulos afectados:** `LanScreenShare.Core`, `LanScreenShare.Media`, `LanScreenShare.Network`, `LanScreenShare.Desktop`
* **Decisores:** Equipo de Arquitectura de LanScreenShare

---

## 1. Contexto y Problema

Cuando una aplicación en el equipo anfitrión solicita permisos de administrador (abriendo el cuadro de diálogo de UAC / *Consent.exe*) o cuando el equipo se bloquea / suspende y requiere el PIN o contraseña de inicio de sesión de Windows:
1. **Pérdida de control remoto:** El cliente remoto pierde la capacidad de enviar eventos de teclado y ratón. El cursor remoto no responde sobre el cuadro de UAC (no se puede hacer clic en "Sí") ni sobre los campos de ingreso del PIN en la pantalla de bloqueo.
2. **Congelamiento de la transmisión:** La captura de pantalla se congela en el último fotograma o se interrumpe con pérdida de acceso a la superficie de duplicación (`DXGI_ERROR_ACCESS_LOST`).
3. **Persistencia del bloqueo aún con privilegios de Administrador:** Ejecutar la aplicación con "Ejecutar como Administrador" no resuelve el problema.

### 1.1 Causa Raíz: Arquitectura de Seguridad de Windows (Session & Desktop Isolation)

Windows aísla estrictamente las interfaces de seguridad del sistema operativo a través de tres fronteras:
1. **Aislamiento de Sesiones (Session 0 Isolation):**
   - Desde Windows Vista, los servicios del sistema corren en `Session 0` sin interfaz gráfica interactiva, mientras que los usuarios interactivos inician sesión en `Session 1`, `Session 2`, etc.
2. **Frontera de Escritorios Win32 (*Desktop Boundary*):**
   - Dentro de una misma sesión interactiva existen múltiples escritorios gráficos independientes administrados por el subsistema `win32k`:
     - `Default`: Escritorio normal donde corren las aplicaciones del usuario (Explorer, navegadores, juegos, etc.).
     - `Winlogon`: **Escritorio Seguro (*Secure Desktop*)**, donde se ejecutan la pantalla de inicio de sesión (*LogonUI.exe*), el selector de credenciales y los diálogos de elevación de UAC (*Consent.exe*).
     - `Screen-saver`: Escritorio donde se ejecutan los protectores de pantalla protegidos.
3. **Aislamiento de Privilegios de Interfaz de Usuario (*UIPI / User Interface Privilege Isolation*):**
   - Windows prohíbe que procesos con menor nivel de integridad envíen mensajes de ventana (`SendMessage`, `PostMessage`) o inyecten entradas (`SendInput`) a ventanas de mayor nivel de integridad, salvo que cuenten con el atributo especial `uiAccess="true"` o corran como `NT AUTHORITY\SYSTEM` en el escritorio activo.

---

## 2. Comparativa de Enfoques de la Industria (AnyDesk, RustDesk, TeamViewer)

| Enfoque | Mecanismo | Ventajas | Desventajas / Requisitos |
| :--- | :--- | :--- | :--- |
| **Opción A: Servicio de Windows (*Service Helper*)** | Un servicio en segundo plano bajo `NT AUTHORITY\SYSTEM` monitorea sesiones (`WTSRegisterSessionNotification`) y conmuta de escritorio (`OpenInputDesktop` / `SetThreadDesktop`) o genera un proceso worker en la sesión activa. | Funciona siempre de forma desatendida. Capaz de desbloquear Windows y presionar UAC sin restricciones. | Requiere instalación administrativa previa (no funciona en modo 100% portable sin permisos de instalación). Mayor complejidad IPC. |
| **Opción B: Binario con atributo `uiAccess="true"`** | El manifiesto `app.manifest` solicita `uiAccess="true"`. Windows permite a este proceso inyectar entradas en ventanas elevadas y en el escritorio seguro. | No requiere servicio en segundo plano continuo. | **Exigencias estrictas del kernel de Windows:**<br>1. El binario debe estar firmado digitalmente con un certificado de confianza (Trusted Root).<br>2. El binario debe ejecutarse desde una ruta protegida del sistema (ej. `C:\Program Files\` o `C:\Windows\System32\`). |
| **Opción C: Detección Adaptativa + Protocolo de Señalización de Estado Seguro** | Detección de pérdida de duplicación (`DXGI_ERROR_ACCESS_LOST` / `ACCESS_DENIED`) y conmutación de escritorio. Conmutación dinámica de hilo con `OpenInputDesktop`. En caso de denegación, emisión de paquete de señalización `SecureDesktopState` para feedback inmediato al visor. | Evita el congelamiento silencioso, recupera la sesión automáticamente tan pronto el escritorio vuelve a `Default` y permite feedback claro e interactivo al usuario. | Sin privilegios de sistema o firma digital, requiere interacción física del usuario local en la pantalla de UAC. |

---

## 3. Decisión Arquitectónica Adoptada: Modelo Híbrido Escalonado

LanScreenShare adopta un **modelo híbrido escalonado** que garantiza compatibilidad tanto en modo portable/desarrollo como en entornos de producción instalados:

```
+-----------------------------------------------------------------------------------+
|                           LanScreenShare Architecture                             |
+-----------------------------------------------------------------------------------+
                                        |
                   +--------------------+--------------------+
                   |                                         |
        [Entorno Portable / Dev]                  [Entorno Instalado / Prod]
                   |                                         |
        • Detección Adaptativa de Winlogon        • Opción B: Manifiesto con uiAccess
        • Intento de OpenInputDesktop             • Opción A: Servicio de Windows
        • Fallback a Feedback Visual en Visor       (NT AUTHORITY\SYSTEM Helper)
        • Recuperación Automática post-UAC                   |
                   |                                         |
                   +--------------------+--------------------+
                                        |
               [Protocolo de Señalización: InputEventType.SecureDesktopState]
```

### 3.1 Componentes Implementados

1. **Gestor de Escritorios Win32 (`WindowsDesktopManager` en `LanScreenShare.Media`):**
   - Provee funciones seguras para inspeccionar y conmutar el hilo actual al escritorio de entrada activo:
     - `OpenInputDesktop(0, false, DESKTOP_ALL_ACCESS)`
     - `GetUserObjectInformation(hDesktop, UOI_NAME, ...)`
     - `SetThreadDesktop(hInputDesktop)`
   - Detecta si el escritorio activo es `Winlogon` o `Default`.

2. **Inyector de Entrada Adaptativo (`WindowsInputSimulatorService`):**
   - Antes de despachar eventos mediante `SendInput` o `SetCursorPos`, intenta conmutar el hilo al escritorio de entrada si ha cambiado.
   - Si cuenta con los privilegios requeridos (`uiAccess` o `SYSTEM`), los eventos se inyectan exitosamente en el diálogo de UAC / Winlogon.
   - Si el sistema operativo deniega el acceso (`ERROR_ACCESS_DENIED`), registra el estado sin lanzar excepciones no controladas.

3. **Captura Resiliente ante Cambios de Escritorio (`DxgiScreenCaptureService` y `GdiScreenCaptureService`):**
   - Cuando DXGI arroja `DXGI_ERROR_ACCESS_LOST` o `ACCESS_DENIED`:
     - Identifica si la causa es la presencia del escritorio seguro (`Winlogon`).
     - Notifica el cambio de estado (`SecureDesktopDetected`).
     - Entra en un ciclo de sondeo pasivo cada 400 ms sin saturar recursos ni reiniciar la GPU innecesariamente.
     - En cuanto el diálogo se cierra o se completa el inicio de sesión, reinicializa la duplicación y reanuda la transmisión a 60 FPS de forma instantánea.

4. **Protocolo de Señalización y Notificación Bidireccional:**
   - Adición de `InputEventType.SecureDesktopState` en `RemoteInputMessage.cs`.
   - `TcpControlServer.BroadcastSecureDesktopStateAsync(bool isBlocked, string message)`.
   - `TcpControlClient.SecureDesktopStateReceived`.
   - El anfitrión informa de inmediato al visor cuando el escritorio está en modo seguro.
   - El visor (`MainWindow` y `StreamPopoutWindow`) despliega un banner amigable de advertencia (`SecureDesktopBanner`) indicando el estado en lugar de congelar la pantalla.

5. **Preparación del Manifiesto para Entornos Firmados (`app.manifest`):**
   - Configuración de la sección `<trustInfo>` para habilitar `uiAccess="true"` cuando el ejecutable sea firmado en el proceso de empaquetado de producción.

---

## 4. Consecuencias y Beneficios

* **Resiliencia Total:** La aplicación no colapsa ni satura la CPU cuando Windows conmuta al escritorio seguro.
* **Transparencia y Claridad UX:** El usuario remoto comprende de inmediato por qué no puede interactuar en una pantalla de UAC si el anfitrión corre como aplicación estándar.
* **Recuperación Inmediata:** La sesión se reanuda a 60 FPS de inmediato sin necesidad de reconectar manualmente cuando se acepta el diálogo o se inicia sesión.
* **Preparación para Producción:** El diseño queda listo para habilitar control transparente de UAC mediante firma digital o servicio helper de Windows.
