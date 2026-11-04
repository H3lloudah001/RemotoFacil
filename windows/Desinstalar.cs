using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RemotoFacil
{
    /// <summary>
    /// Quita lo que instaló y configuró RemotoFácil. Tailscale y Sunshine son opcionales: si ya estaban
    /// antes de RemotoFácil no se marcan, y si se dejan solo se les quita lo que añadió RemotoFácil.
    /// </summary>
    class PaginaDesinstalar : Pagina
    {
        public override string Titulo => "Desinstalar RemotoFácil";
        public override string Subtitulo => "Quita los programas y los ajustes que dejó en este PC";
        public override string TextoSiguiente => Hecho ? TextoPorDefecto : "Desinstalar";
        public override bool PuedeSeguir => !Ocupada;

        /// Ya se desinstaló (aunque la ventana se cierre con la X)
        public bool Hecho { get; private set; }

        readonly CheckBox tailscale = new CheckBox { Text = "Tailscale (antes cierra la sesión de este PC en tu cuenta)", AutoSize = true };
        readonly CheckBox sunshine = new CheckBox { Text = "Sunshine, con su contraseña y los dispositivos emparejados", AutoSize = true };
        readonly CheckBox propio = new CheckBox
        {
            Text = "RemotoFácil y lo que configuró (accesos directos, cortafuegos, pantallas, energía…)",
            // Siempre marcada; sin deshabilitarla, porque el texto gris apenas se lee en modo oscuro
            AutoSize = true, Checked = true, AutoCheck = false,
        };
        readonly Label notaTailscale, notaSunshine, estado;
        readonly TextBox registro = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 120,
            Anchor = AnchorStyles.Left | AnchorStyles.Right, Font = new Font("Consolas", 9F), Visible = false,
        };

        public PaginaDesinstalar()
        {
            Texto("Elige qué quieres quitar de este PC:", margenAbajo: 8);
            Poner(tailscale, 0);
            notaTailscale = Nota();
            Poner(sunshine, 0);
            notaSunshine = Nota();
            Poner(propio, 10);
            Tema.Pintar(Texto("Lo que no marques seguirá funcionando por su cuenta: RemotoFácil solo le quita lo que añadió él."), Rol.Suave);
            estado = Texto("", margenAbajo: 6);
            Poner(registro, 0);
        }

        Label Nota()
        {
            var l = Tema.Pintar(Texto("", margenAbajo: 6), Rol.Suave);
            l.Margin = new Padding(20, 0, 0, 6);
            return l;
        }

        public override void AlMostrar()
        {
            Preparar(tailscale, notaTailscale, Acciones.TailscaleInstalado, "tailscale", "Tailscale");
            Preparar(sunshine, notaSunshine, Acciones.SunshineInstalado, "sunshine", "Sunshine");
            if (Acciones.LoInstaloRemotoFacil("vigembus") == true)
                sunshine.Text = "Sunshine, con su contraseña, los dispositivos emparejados y el driver de mandos";
        }

        static void Preparar(CheckBox casilla, Label nota, bool instalado, string clave, string nombre)
        {
            casilla.Visible = instalado;
            var deRemotoFacil = Acciones.LoInstaloRemotoFacil(clave);
            // Si no se sabe (versiones anteriores no lo apuntaban), se propone quitarlo
            casilla.Checked = instalado && deRemotoFacil != false;
            nota.Text = instalado && deRemotoFacil == false
                ? $"{nombre} ya estaba instalado antes de RemotoFácil, así que por defecto se deja."
                : "";
        }

        void Log(string t)
        {
            registro.AppendText(t + Environment.NewLine);
            Registro.Escribir("Desinstalar: " + t);
        }

        public override async Task<bool> AlSiguiente()
        {
            if (Hecho) return true;

            var quitar = new List<string>();
            if (tailscale.Visible && tailscale.Checked) quitar.Add("Tailscale");
            if (sunshine.Visible && sunshine.Checked) quitar.Add("Sunshine");
            quitar.Add("RemotoFácil");
            if (MessageBox.Show(A, "Se va a quitar: " + string.Join(", ", quitar) + ".\n\n" +
                                   "Después ya no podrás conectarte a este PC desde otros dispositivos. ¿Seguir?",
                                "RemotoFácil", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return false;

            tailscale.Enabled = sunshine.Enabled = false;
            registro.Visible = true;
            Ocupada = true; Avisar();
            Tema.Pintar(estado, Rol.Texto);
            estado.Text = "Desinstalando… puede tardar un par de minutos.";
            var opciones = (tailscale: tailscale.Visible && tailscale.Checked, sunshine: sunshine.Visible && sunshine.Checked);
            var progreso = new Progress<string>(Log);
            List<string> pendientes;
            try
            {
                pendientes = await Task.Run(() => Desinstalador.Ejecutar(opciones.tailscale, opciones.sunshine, progreso));
            }
            catch (Exception ex)
            {
                // Cada paso atrapa sus propios errores; esto sería algo muy raro
                pendientes = new List<string> { "Error inesperado: " + ex.Message };
            }
            Ocupada = false;
            Hecho = true;

            if (pendientes.Count == 0)
            {
                Tema.Pintar(estado, Rol.Exito);
                estado.Text = "Listo: RemotoFácil se ha desinstalado. Al cerrar esta ventana se borra él mismo.";
            }
            else
            {
                Tema.Pintar(estado, Rol.Aviso);
                estado.Text = "Desinstalado, pero hay cosas que revisar:\n•  " + string.Join("\n•  ", pendientes);
            }
            Avisar();
            return false;
        }
    }

    static class Desinstalador
    {
        /// Hace todos los pasos y devuelve lo que no se pudo hacer (vacío si todo fue bien)
        public static List<string> Ejecutar(bool quitarTailscale, bool quitarSunshine, IProgress<string> log)
        {
            var pendientes = new List<string>();
            void Paso(string que, Action accion)
            {
                try { accion(); }
                catch (Exception ex)
                {
                    log.Report($"No se pudo {que}: {ex.Message}");
                    pendientes.Add($"No se pudo {que}.");
                }
            }

            // 1. Pantallas: si quedó alguna apagada por una sesión a medias, encenderla
            if (File.Exists(Rutas.ArchivoEstado))
                Paso("volver a encender las pantallas", () => { Pantallas.Restaurar(forzar: false); log.Report("Pantallas restauradas."); });

            // 2. Sunshine
            if (quitarSunshine)
            {
                Paso("desinstalar Sunshine", () =>
                {
                    log.Report("Desinstalando Sunshine…");
                    if (!DesinstalarPrograma("Sunshine", log)) { pendientes.Add("Quita Sunshine desde Configuración → Aplicaciones."); return; }
                    // El desinstalador deja la carpeta config (contraseña, dispositivos emparejados, registros)
                    BorrarCarpeta(Rutas.SunshineDir, log);
                });
                // El driver de mandos solo si lo puso RemotoFácil: otros programas (DS4Windows…) también lo usan
                if (Acciones.LoInstaloRemotoFacil("vigembus") == true)
                    Paso("desinstalar el driver de mandos", () =>
                    {
                        log.Report("Desinstalando el driver de mandos (ViGEmBus)…");
                        if (Acciones.DesinstalarViGEm()) log.Report("Driver de mandos desinstalado.");
                        else pendientes.Add("Quita \"ViGEm Bus Driver\" desde Configuración → Aplicaciones.");
                    });
            }
            else if (Acciones.SunshineInstalado)
            {
                Paso("quitar los comandos de RemotoFácil de Sunshine", () =>
                {
                    Acciones.ConfigurarPantallasEnSunshine(false);
                    log.Report("Sunshine ya no apagará pantallas al conectarse.");
                    try { Acciones.ReiniciarSunshine(); } catch { }
                });
            }

            // 3. Tailscale
            if (quitarTailscale)
            {
                Paso("desinstalar Tailscale", () =>
                {
                    if (Acciones.TailscaleInstalado)
                    {
                        log.Report("Cerrando la sesión de Tailscale…");
                        Acciones.Ejecutar(Rutas.TailscaleExe, "logout", 30000);
                    }
                    log.Report("Desinstalando Tailscale…");
                    if (!DesinstalarPrograma("Tailscale", log)) { pendientes.Add("Quita Tailscale desde Configuración → Aplicaciones."); return; }
                    // Su estado (la identidad de este PC) y las directivas que puso el instalador
                    BorrarCarpeta(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Tailscale"), log);
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                        hklm.DeleteSubKeyTree(@"SOFTWARE\Policies\Tailscale", false);
                });
            }

            // 4. Lo propio de RemotoFácil
            Paso("devolver la configuración de energía", () =>
            {
                if (Acciones.RestaurarEnergia()) log.Report("Suspensión del PC devuelta a como estaba.");
                else pendientes.Add("La suspensión del PC sigue desactivada (no se sabía cómo estaba antes). " +
                                    "Puedes cambiarla en Configuración → Sistema → Energía.");
            });
            Paso("quitar la regla del cortafuegos", () => { Acciones.BorrarReglaCortafuegos(); log.Report("Regla del cortafuegos quitada."); });
            Paso("quitar los accesos directos", () => { Acciones.BorrarAccesosDirectos(); log.Report("Accesos directos quitados."); });
            Paso("quitar RemotoFácil de la lista de aplicaciones", Acciones.QuitarDeAplicaciones);

            log.Report("Borrando los ajustes de RemotoFácil…");
            Registro.Desactivado = true;
            Paso("borrar los ajustes de RemotoFácil", () =>
            {
                BorrarCarpeta(Rutas.CarpetaComun, log);
                BorrarCarpeta(Rutas.CarpetaUsuario, log);
                BorrarCarpeta(Rutas.CarpetaTemporal, log);
            });
            Paso("borrar RemotoFácil de Archivos de programa", () => BorrarseASiMismo(log));

            log.Report(pendientes.Count == 0 ? "Terminado." : "Terminado, con avisos.");
            return pendientes;
        }

        /// Busca el programa en la lista de aplicaciones de Windows y lo desinstala sin preguntas.
        /// false si no se pudo (hay que hacerlo a mano).
        static bool DesinstalarPrograma(string nombre, IProgress<string> log)
        {
            var entrada = BuscarEnAplicaciones(nombre);
            if (entrada == null)
            {
                log.Report($"{nombre} no aparece en la lista de aplicaciones; puede que ya no esté instalado.");
                return true;
            }
            var (codigoProducto, desinstalador) = entrada.Value;
            int codigo;
            if (codigoProducto != null)
            {
                var registroMsi = Path.Combine(Path.GetTempPath(), $"desinstalar-{nombre.ToLowerInvariant()}.log");
                (codigo, _) = Acciones.Ejecutar("msiexec.exe", $"/x {codigoProducto} /qn /norestart /l*v {Acciones.Citar(registroMsi)}", 15 * 60 * 1000);
                // 1605 = ya no estaba instalado; 3010 = hecho, pide reiniciar
                if (codigo == 0 || codigo == 1605 || codigo == 3010) { log.Report($"{nombre} desinstalado."); return true; }
            }
            else
            {
                // Instalado con un .exe (no MSI): los de Sunshine y Tailscale aceptan /S y /quiet respectivamente
                var (exe, args) = SepararComando(desinstalador);
                (codigo, _) = Acciones.Ejecutar(exe, (args + (nombre == "Tailscale" ? " /quiet" : " /S")).Trim(), 15 * 60 * 1000);
                if (codigo == 0) { log.Report($"{nombre} desinstalado."); return true; }
            }
            log.Report($"El desinstalador de {nombre} terminó con el código {codigo}.");
            return false;
        }

        static (string codigoProducto, string desinstalador)? BuscarEnAplicaciones(string nombre)
        {
            foreach (var vista in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, vista))
                using (var raiz = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (raiz == null) continue;
                    foreach (var sub in raiz.GetSubKeyNames())
                        using (var k = raiz.OpenSubKey(sub))
                        {
                            var nombreMostrado = k?.GetValue("DisplayName") as string ?? "";
                            if (!nombreMostrado.Equals(nombre, StringComparison.OrdinalIgnoreCase) &&
                                !nombreMostrado.StartsWith(nombre + " ", StringComparison.OrdinalIgnoreCase)) continue;
                            if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                            bool esMsi = k.GetValue("WindowsInstaller") is int wi && wi == 1 && sub.StartsWith("{");
                            var cadena = k.GetValue("QuietUninstallString") as string ?? k.GetValue("UninstallString") as string;
                            if (esMsi) return (sub, null);
                            if (!string.IsNullOrWhiteSpace(cadena)) return (null, cadena);
                        }
                }
            return null;
        }

        static (string exe, string args) SepararComando(string comando)
        {
            comando = comando.Trim();
            if (comando.StartsWith("\""))
            {
                int fin = comando.IndexOf('"', 1);
                return (comando.Substring(1, fin - 1), comando.Substring(fin + 1).Trim());
            }
            int i = comando.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return i < 0 ? (comando, "") : (comando.Substring(0, i + 4), comando.Substring(i + 4).Trim());
        }

        static void BorrarCarpeta(string carpeta, IProgress<string> log)
        {
            if (!Directory.Exists(carpeta)) return;
            Directory.Delete(carpeta, true);
            log.Report("Borrado: " + carpeta);
        }

        /// La carpeta de Archivos de programa no se puede borrar mientras este exe se ejecuta desde ella:
        /// en ese caso se deja encargado a PowerShell, que espera a que se cierre la ventana y la borra.
        static void BorrarseASiMismo(IProgress<string> log)
        {
            var carpeta = Rutas.CarpetaPrograma;
            if (!Directory.Exists(carpeta)) return;
            var propio = Path.GetFullPath(Application.ExecutablePath);
            if (!propio.StartsWith(Path.GetFullPath(carpeta) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                BorrarCarpeta(carpeta, log);
                return;
            }
            var ruta = carpeta.Replace("'", "''");
            var script = $"Wait-Process -Id {Process.GetCurrentProcess().Id} -ErrorAction SilentlyContinue; " +
                         $"Start-Sleep -Seconds 1; Remove-Item -LiteralPath '{ruta}' -Recurse -Force";
            Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -Command " + Acciones.Citar(script))
            {
                UseShellExecute = false, CreateNoWindow = true,
            });
            log.Report("RemotoFácil se borrará al cerrar esta ventana.");
        }
    }
}
