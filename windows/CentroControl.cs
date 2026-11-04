using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemotoFacil
{
    /// <summary>
    /// Lo que se abre cuando el PC ya está configurado: la dirección para conectarse, el estado de
    /// Tailscale y Sunshine, los dispositivos emparejados y accesos rápidos a todo lo demás.
    /// </summary>
    class CentroControl : Form
    {
        readonly Label ip, ayuda, tsCuenta, tsEquipo, ssMandos, ssPantallas;
        readonly LinkLabel ssArreglarMandos;
        readonly Pastilla estadoGeneral = new Pastilla { MargenDerecho = 20 }, tsEstado, ssEstado;
        readonly Button copiar, tsConectar, ssReiniciar;
        readonly Panel listaDispositivos;
        readonly Timer temporizador = new Timer { Interval = 10000 };
        readonly System.Threading.CancellationTokenSource alCerrar = new System.Threading.CancellationTokenSource();

        Acciones.InfoTailscale tailscale;
        (string usuario, string clave)? credenciales;
        bool refrescando, tareaEnCurso, mandosListos = true;

        public CentroControl()
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "RemotoFácil";
            Font = new Font("Segoe UI", 10F);
            ClientSize = new Size(820, 560);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Tema.Fondo(this, RolFondo.Fondo);
            Icon = Iconos.App;

            // ---------- Cabecera ----------
            // Con su ancho final desde el principio, para que los botones anclados a la derecha queden en su sitio
            var cabecera = new Cabecera { Bounds = new Rectangle(0, 0, 820, 76), Dock = DockStyle.Top, Logo = Iconos.Logo() };
            var titulo = new Label
            {
                Text = "RemotoFácil", AutoSize = true, Location = new Point(78, 10), BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 17F), ForeColor = Color.White,
            };
            var subtitulo = new Label
            {
                Text = "Centro de control de " + Environment.MachineName, AutoSize = true, Location = new Point(81, 45),
                BackColor = Color.Transparent,
            };
            var actualizar = Tema.Boton(new Button { Text = "Actualizar", Bounds = new Rectangle(690, 22, 106, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right }, TipoBoton.Cabecera);
            actualizar.Click += async (s, e) => await RefrescarTodo();
            var botonTema = Tema.CrearBoton();
            botonTema.Location = new Point(560, 22);
            // Pegado a la izquierda de "Actualizar", aunque cambie su texto
            botonTema.SizeChanged += (s, e) => botonTema.Left = actualizar.Left - botonTema.Width - Escalar(8);
            cabecera.Controls.AddRange(new Control[] { titulo, subtitulo, botonTema, actualizar });

            var cuerpo = Tema.Fondo(new Panel { Dock = DockStyle.Fill }, RolFondo.Fondo);

            // ---------- Dirección para conectarse ----------
            var principal = new Tarjeta { Bounds = new Rectangle(20, 16, 780, 104) };
            principal.Controls.Add(Etiqueta("Para conectarte desde Moonlight, usa esta dirección", 20, 12, Rol.Suave, 9.5F));
            ip = Etiqueta("…", 17, 32, Rol.Texto, 24F, negrita: true);
            principal.Controls.Add(ip);
            copiar = Boton("Copiar", primario: false);
            copiar.Bounds = new Rectangle(240, 42, 84, 30);
            copiar.Click += (s, e) => Copiar();
            principal.Controls.Add(copiar);
            estadoGeneral.Location = new Point(560, 14);
            principal.Controls.Add(estadoGeneral);
            ayuda = Etiqueta("", 20, 78, Rol.Suave, 9F);
            ayuda.AutoSize = false; ayuda.AutoEllipsis = true; ayuda.Size = new Size(740, 20);
            principal.Controls.Add(ayuda);

            // ---------- Tailscale ----------
            var ts = new Tarjeta { Bounds = new Rectangle(20, 132, 384, 120) };
            ts.Controls.Add(Etiqueta("Tailscale", 20, 12, Rol.Texto, 12F, negrita: true));
            tsEstado = EstadoDerecha(ts);
            tsCuenta = Etiqueta("", 20, 42, Rol.Suave, 9.5F);
            tsEquipo = Etiqueta("", 20, 62, Rol.Suave, 9.5F);
            var tsAbrir = Boton("Abrir Tailscale", primario: false);
            tsAbrir.Bounds = new Rectangle(20, 84, 130, 28);
            tsAbrir.Click += (s, e) =>
            {
                Acciones.AbrirBandejaTailscale();
                Acciones.AbrirEnNavegador("https://login.tailscale.com/admin/machines");
            };
            tsConectar = Boton("Conectar", primario: true);
            tsConectar.Bounds = new Rectangle(158, 84, 110, 28);
            tsConectar.Visible = false;
            tsConectar.Click += async (s, e) => await ConectarTailscale();
            ts.Controls.AddRange(new Control[] { tsCuenta, tsEquipo, tsAbrir, tsConectar });
            Consejo(tsAbrir, "Abre el panel web de Tailscale, donde ves todos los dispositivos de tu cuenta");

            // ---------- Sunshine ----------
            var ss = new Tarjeta { Bounds = new Rectangle(416, 132, 384, 120) };
            ss.Controls.Add(Etiqueta("Sunshine", 20, 12, Rol.Texto, 12F, negrita: true));
            ssEstado = EstadoDerecha(ss);
            ssMandos = Etiqueta("Mandos: …", 20, 42, Rol.Suave, 9.5F);
            ssArreglarMandos = Tema.Pintar(new LinkLabel
            {
                Text = "Instalar", AutoSize = true, Location = new Point(200, 42), Visible = false,
                Font = new Font("Segoe UI Semibold", 9.5F),
            }, Rol.Enlace);
            ssArreglarMandos.LinkClicked += async (s, e) => await ArreglarMandos();
            ssPantallas = Etiqueta("", 20, 62, Rol.Suave, 9.5F);
            var ssAbrir = Boton("Abrir Sunshine", primario: false);
            ssAbrir.Bounds = new Rectangle(20, 84, 130, 28);
            ssAbrir.Click += (s, e) => Acciones.AbrirEnNavegador("https://localhost:47990");
            ssReiniciar = Boton("Reiniciar", primario: false);
            ssReiniciar.Bounds = new Rectangle(158, 84, 110, 28);
            ssReiniciar.Click += async (s, e) => await ReiniciarSunshine();
            ss.Controls.AddRange(new Control[] { ssMandos, ssArreglarMandos, ssPantallas, ssAbrir, ssReiniciar });
            Consejo(ssArreglarMandos, "Instala el driver que necesita Sunshine para que el PC vea los mandos");
            Consejo(ssAbrir, "Ajustes avanzados de Sunshine: calidad, aplicaciones, registros…");

            // ---------- Dispositivos ----------
            var disp = new Tarjeta { Bounds = new Rectangle(20, 264, 780, 160) };
            disp.Controls.Add(Etiqueta("Dispositivos emparejados", 20, 12, Rol.Texto, 12F, negrita: true));
            var agregar = Boton("+  Agregar dispositivo", primario: true);
            agregar.Bounds = new Rectangle(580, 10, 180, 32);
            agregar.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            agregar.Click += (s, e) => AgregarDispositivo();
            listaDispositivos = Tema.Fondo(new Panel { Bounds = new Rectangle(20, 48, 740, 104), AutoScroll = true }, RolFondo.Superficie);
            disp.Controls.AddRange(new Control[] { agregar, listaDispositivos });

            // ---------- Accesos de abajo ----------
            var abajo = Tema.Fondo(new FlowLayoutPanel { Bounds = new Rectangle(16, 434, 790, 40), WrapContents = false }, RolFondo.Fondo);
            void Acceso(string texto, string consejo, Action accion)
            {
                var b = Boton(texto, primario: false);
                b.AutoSize = true;
                b.Height = 32;
                b.Margin = new Padding(4, 0, 6, 0);
                b.Padding = new Padding(8, 0, 8, 0);
                b.Click += (s, e) => accion();
                Consejo(b, consejo);
                abajo.Controls.Add(b);
            }
            Acceso("Pantallas al conectarse", "Elegir qué pantallas se apagan mientras estás conectado", ElegirPantallas);
            Acceso("Restaurar pantallas", "Volver a encender las pantallas si alguna se quedó apagada", RestaurarPantallas);
            Acceso("Repetir la configuración", "Abrir de nuevo el asistente completo", RepetirConfiguracion);
            Acceso("Ver registro", "Abrir el archivo de registro de RemotoFácil (útil si algo falla)", VerRegistro);
            Acceso("Desinstalar", "Quitar RemotoFácil y, si quieres, Tailscale y Sunshine", Desinstalar);

            cuerpo.Controls.AddRange(new Control[] { principal, ts, ss, disp, abajo });
            Controls.Add(cuerpo);
            Controls.Add(cabecera);

            temporizador.Tick += async (s, e) => await RefrescarEstado();
            Tema.Seguir(this, () => subtitulo.ForeColor = Tema.CabeceraSubtitulo);

            Load += async (s, e) =>
            {
                Asistente.AjustarAPantalla(this);
                botonTema.Left = actualizar.Left - botonTema.Width - Escalar(8);
                credenciales = Acciones.LeerCredenciales();
                // Las instalaciones de versiones anteriores no aparecían en Configuración → Aplicaciones
                _ = Task.Run(Acciones.RegistrarEnAplicaciones);
                await RefrescarTodo();
                temporizador.Start();
            };
            FormClosed += (s, e) => { alCerrar.Cancel(); temporizador.Dispose(); };
        }

        // ================== Estado ==================

        async Task RefrescarTodo()
        {
            await RefrescarEstado();
            await RefrescarDispositivos();
        }

        async Task RefrescarEstado()
        {
            if (refrescando) return;
            refrescando = true;
            try
            {
                var (info, servicio, pantallas, mandos) = await Task.Run(() =>
                {
                    var i = Acciones.EstadoTailscale();
                    var sv = Acciones.EstadoServicioSunshine();
                    int n = 0;
                    try { if (File.Exists(Rutas.ArchivoPantallas)) n = File.ReadAllLines(Rutas.ArchivoPantallas).Count(l => l.Trim().Length > 0); }
                    catch { }
                    return (i, sv, n, Acciones.MandosListos);
                });
                if (IsDisposed) return;
                tailscale = info;
                mandosListos = mandos;
                MostrarTailscale(info);
                MostrarSunshine(servicio, pantallas);
                MostrarEstadoGeneral(info, servicio);
            }
            finally { refrescando = false; }
        }

        void MostrarTailscale(Acciones.InfoTailscale info)
        {
            ip.Text = info?.Ip ?? "Sin dirección";
            copiar.Left = ip.Right + 10;
            copiar.Enabled = info?.Ip != null;

            if (info == null) PonerEstado(tsEstado, "No responde", Rol.Error);
            else if (info.Conectado) PonerEstado(tsEstado, "Conectado", Rol.Exito);
            else if (info.Estado == "NeedsLogin" || info.Estado == "NoState") PonerEstado(tsEstado, "Sin iniciar sesión", Rol.Error);
            else PonerEstado(tsEstado, "Desactivado", Rol.Aviso);

            tsCuenta.Text = "Cuenta: " + (info?.Cuenta ?? "—");
            tsEquipo.Text = "Nombre del equipo: " + (info?.Equipo ?? Environment.MachineName.ToLowerInvariant());
            tsConectar.Visible = info != null && !info.Conectado;
            tsConectar.Text = info?.Estado == "Stopped" ? "Activar" : "Iniciar sesión";

            ayuda.Text = "En el otro dispositivo: activa Tailscale con la misma cuenta, abre Moonlight y toca este PC.";
        }

        void MostrarSunshine(ServiceControllerStatus? servicio, int pantallas)
        {
            if (servicio == null) PonerEstado(ssEstado, "No instalado", Rol.Error);
            else if (servicio == ServiceControllerStatus.Running) PonerEstado(ssEstado, "Funcionando", Rol.Exito);
            else if (servicio == ServiceControllerStatus.StartPending) PonerEstado(ssEstado, "Arrancando…", Rol.Aviso);
            else PonerEstado(ssEstado, "Detenido", Rol.Error);
            ssReiniciar.Text = servicio == ServiceControllerStatus.Running ? "Reiniciar" : "Iniciar";
            ssReiniciar.Enabled = servicio != null && !tareaEnCurso;
            if (!tareaEnCurso)   // mientras se instala el driver, su texto lo pone ArreglarMandos
            {
                ssMandos.Text = mandosListos ? "Mandos: listos" : "Mandos: falta el driver";
                Tema.Pintar(ssMandos, mandosListos ? Rol.Suave : Rol.Aviso);
                ssArreglarMandos.Left = ssMandos.Right + Escalar(4);
                ssArreglarMandos.Visible = !mandosListos;
                ssArreglarMandos.Enabled = servicio != null;
            }
            ssPantallas.Text = pantallas == 0
                ? "Al conectarte: no se apaga ninguna pantalla"
                : $"Al conectarte: se apaga{(pantallas == 1 ? "" : "n")} {pantallas} pantalla{(pantallas == 1 ? "" : "s")}";
        }

        void MostrarEstadoGeneral(Acciones.InfoTailscale info, ServiceControllerStatus? servicio)
        {
            if (info == null) estadoGeneral.Poner("Tailscale no responde", Rol.Error, conFondo: true);
            else if (info.Estado == "NeedsLogin" || info.Estado == "NoState") estadoGeneral.Poner("Falta iniciar sesión en Tailscale", Rol.Error, conFondo: true);
            else if (!info.Conectado) estadoGeneral.Poner("Tailscale está desactivado", Rol.Aviso, conFondo: true);
            else if (servicio != ServiceControllerStatus.Running) estadoGeneral.Poner("Sunshine no está funcionando", Rol.Error, conFondo: true);
            else if (!mandosListos) estadoGeneral.Poner("Los mandos no funcionarán", Rol.Aviso, conFondo: true);
            else estadoGeneral.Poner("Listo para conectarte", Rol.Exito, conFondo: true);
        }

        static void PonerEstado(Pastilla p, string texto, Rol rol) => p.Poner(texto, rol, conFondo: false);

        // ================== Dispositivos ==================

        async Task RefrescarDispositivos()
        {
            if (credenciales == null)
            {
                MensajeLista("Escribe la contraseña de Sunshine para ver y gestionar los dispositivos.", "Escribir contraseña",
                             () => { if (PedirCredenciales()) _ = RefrescarDispositivos(); });
                return;
            }
            var (u, c) = credenciales.Value;
            try
            {
                var lista = await Task.Run(() => Acciones.Dispositivos(u, c));
                if (IsDisposed) return;
                if (lista.Count == 0)
                    MensajeLista("Todavía no hay ningún dispositivo emparejado. Pulsa \"Agregar dispositivo\" para empezar.");
                else
                    MostrarDispositivos(lista);
            }
            catch (Acciones.CredencialesIncorrectas)
            {
                Acciones.BorrarCredenciales();
                credenciales = null;
                MensajeLista("La contraseña de Sunshine guardada ya no es válida.", "Escribir la nueva",
                             () => { if (PedirCredenciales()) _ = RefrescarDispositivos(); });
            }
            catch (Exception ex)
            {
                Registro.Escribir("No se pudo leer la lista de dispositivos: " + ex.Message);
                MensajeLista("Sunshine no responde ahora mismo, así que no se puede ver la lista.", "Reintentar",
                             () => _ = RefrescarDispositivos());
            }
        }

        void MensajeLista(string texto, string enlace = null, Action accion = null)
        {
            listaDispositivos.SuspendLayout();
            LimpiarLista();
            var l = Tema.Pintar(new Label { Text = texto, AutoSize = true, Location = new Point(0, 8) }, Rol.Suave);
            listaDispositivos.Controls.Add(l);
            if (enlace != null)
            {
                var a = Tema.Pintar(new LinkLabel { Text = enlace, AutoSize = true, Location = new Point(0, l.Bottom + Escalar(6)) }, Rol.Enlace);
                a.LinkClicked += (s, e) => accion();
                listaDispositivos.Controls.Add(a);
            }
            listaDispositivos.ResumeLayout();
        }

        void MostrarDispositivos(List<Acciones.Dispositivo> lista)
        {
            listaDispositivos.SuspendLayout();
            LimpiarLista();
            int alto = Escalar(34), y = 0;
            int ancho = listaDispositivos.ClientSize.Width - (lista.Count * alto > listaDispositivos.ClientSize.Height ? SystemInformation.VerticalScrollBarWidth : 0);
            foreach (var d in lista)
            {
                var fila = new FilaDispositivo(d.Nombre, ultima: d == lista.Last()) { Bounds = new Rectangle(0, y, ancho, alto) };
                var dispositivo = d;
                fila.Quitar.LinkClicked += async (s, e) => await Quitar(dispositivo);
                listaDispositivos.Controls.Add(fila);
                y += alto;
            }
            listaDispositivos.ResumeLayout();
        }

        void LimpiarLista()
        {
            var viejos = listaDispositivos.Controls.Cast<Control>().ToList();
            listaDispositivos.Controls.Clear();
            viejos.ForEach(c => c.Dispose());
        }

        async Task Quitar(Acciones.Dispositivo d)
        {
            if (MessageBox.Show(this, $"¿Quitar \"{d.Nombre}\"?\n\nYa no podrá conectarse a este PC hasta que lo vuelvas a emparejar.",
                                "RemotoFácil", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var (u, c) = credenciales.Value;
            try
            {
                bool ok = await Task.Run(() => Acciones.QuitarDispositivo(u, c, d.Uuid));
                if (!ok) Error("Sunshine no pudo quitar el dispositivo. Prueba desde el panel de Sunshine.");
            }
            catch (Exception ex) { Error(ex.Message); }
            await RefrescarDispositivos();
        }

        void AgregarDispositivo()
        {
            if (credenciales == null && !PedirCredenciales()) return;
            if (tailscale?.Ip == null)
            {
                Error("Este PC no está conectado a Tailscale ahora mismo, así que los dispositivos no podrán llegar a él.\n\n" +
                      "Pulsa \"" + tsConectar.Text + "\" en la tarjeta de Tailscale y vuelve a intentarlo.");
                return;
            }
            var (u, c) = credenciales.Value;
            using (var a = new Asistente(new Estado { Ip = tailscale.Ip, Usuario = u, Clave = c }, new PaginaEmparejar()))
                a.ShowDialog(this);
            _ = RefrescarDispositivos();
        }

        /// Pide el usuario y la contraseña del panel de Sunshine, los comprueba y los guarda. true si quedaron guardados.
        bool PedirCredenciales()
        {
            using (var f = new DialogoCredenciales())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return false;
                credenciales = (f.Usuario, f.Clave);
                Acciones.GuardarCredenciales(f.Usuario, f.Clave);
                return true;
            }
        }

        // ================== Acciones ==================

        void Copiar()
        {
            if (tailscale?.Ip == null) return;
            try { Clipboard.SetText(tailscale.Ip); } catch { return; }
            copiar.Text = "¡Copiada!";
            var t = new Timer { Interval = 1500 };
            t.Tick += (s, e) => { t.Dispose(); if (!copiar.IsDisposed) copiar.Text = "Copiar"; };
            t.Start();
        }

        async Task ConectarTailscale()
        {
            if (tareaEnCurso) return;
            tareaEnCurso = true;
            tsConectar.Enabled = false;
            PonerEstado(tsEstado, "Conectando…", Rol.Aviso);
            try
            {
                var (ok, salida) = await Acciones.IniciarSesionTailscale(
                    url => BeginInvoke((Action)(() => Acciones.AbrirEnNavegador(url))), alCerrar.Token);
                if (!ok) Error("No se pudo conectar Tailscale.\n\n" + salida);
                else Acciones.AbrirBandejaTailscale();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Error(ex.Message); }
            finally { tareaEnCurso = false; if (!IsDisposed) tsConectar.Enabled = true; }
            await RefrescarEstado();
        }

        async Task ReiniciarSunshine()
        {
            if (tareaEnCurso) return;
            tareaEnCurso = true;
            ssReiniciar.Enabled = false;
            PonerEstado(ssEstado, "Reiniciando…", Rol.Aviso);
            try { await Task.Run(Acciones.ReiniciarSunshine); }
            catch (Exception ex) { Error("No se pudo reiniciar Sunshine:\n" + ex.Message); }
            finally { tareaEnCurso = false; }
            await RefrescarTodo();
        }

        async Task ArreglarMandos()
        {
            if (tareaEnCurso) return;
            tareaEnCurso = true;
            ssArreglarMandos.Enabled = ssReiniciar.Enabled = false;
            ssMandos.Text = "Mandos: instalando el driver…";
            ssArreglarMandos.Visible = false;
            var hechos = new List<string>();
            try
            {
                bool pideReinicio = await Acciones.PrepararMandos(null, t => { hechos.Add(t); Registro.Escribir(t); });
                PonerEstado(ssEstado, "Reiniciando…", Rol.Aviso);
                await Task.Run(Acciones.ReiniciarSunshine);
                MessageBox.Show(this, "Listo: el PC ya verá los mandos (como mando de Xbox 360 o DualShock 4).\n\n" +
                                      "Si estabas conectado desde Moonlight, sal y vuelve a entrar." +
                                      (pideReinicio ? "\n\nWindows pide reiniciar el PC para terminar: si no van, reinícialo." : ""),
                                "RemotoFácil", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Registro.Escribir("No se pudo instalar el driver de mandos: " + ex.Message);
                Error("No se pudo instalar el driver de mandos:\n" + ex.Message);
            }
            finally { tareaEnCurso = false; }
            await RefrescarTodo();
        }

        void ElegirPantallas()
        {
            using (var a = new Asistente(new Estado(), new PaginaPantallas()))
                a.ShowDialog(this);
            _ = RefrescarEstado();
        }

        void RestaurarPantallas()
        {
            try { Pantallas.Restaurar(forzar: true); }
            catch (Exception ex) { Error("No se pudieron restaurar las pantallas:\n" + ex.Message); }
        }

        void RepetirConfiguracion()
        {
            temporizador.Stop();
            Hide();
            try
            {
                using (var a = new Asistente()) a.ShowDialog();
            }
            finally
            {
                Show();
                credenciales = Acciones.LeerCredenciales();
                _ = RefrescarTodo();
                temporizador.Start();
            }
        }

        void Desinstalar()
        {
            temporizador.Stop();
            var pagina = new PaginaDesinstalar();
            using (var a = new Asistente(new Estado(), pagina)) a.ShowDialog(this);
            if (pagina.Hecho) { Close(); return; }
            temporizador.Start();
            _ = RefrescarTodo();
        }

        void VerRegistro()
        {
            if (!File.Exists(Rutas.ArchivoRegistro)) { Error("Todavía no hay nada en el registro."); return; }
            try { System.Diagnostics.Process.Start("notepad.exe", Acciones.Citar(Rutas.ArchivoRegistro)); }
            catch (Exception ex) { Error(ex.Message); }
        }

        void Error(string mensaje) => MessageBox.Show(this, mensaje, "RemotoFácil", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        // ================== Piezas visuales ==================

        int Escalar(int px) => (int)Math.Round(px * DeviceDpi / 96f);

        readonly ToolTip consejos = new ToolTip();
        void Consejo(Control c, string texto) => consejos.SetToolTip(c, texto);

        static Label Etiqueta(string texto, int x, int y, Rol rol, float tam, bool negrita = false) => Tema.Pintar(new Label
        {
            Text = texto, AutoSize = true, Location = new Point(x, y),
            Font = new Font(negrita ? "Segoe UI Semibold" : "Segoe UI", tam),
        }, rol);

        static Pastilla EstadoDerecha(Tarjeta t)
        {
            var p = new Pastilla { Location = new Point(t.Width - 160, 12), MargenDerecho = 10 };
            t.Controls.Add(p);
            return p;
        }

        static Button Boton(string texto, bool primario) =>
            Tema.Boton(new Button { Text = texto, Font = new Font("Segoe UI Semibold", 9.5F) }, primario ? TipoBoton.Primario : TipoBoton.Secundario);

        /// Banda azul con degradado y el logo
        class Cabecera : Panel
        {
            public Image Logo;
            public Cabecera() { DoubleBuffered = true; ResizeRedraw = true; }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var b = new LinearGradientBrush(ClientRectangle, Tema.CabeceraIzquierda, Tema.CabeceraDerecha, LinearGradientMode.Horizontal))
                    g.FillRectangle(b, ClientRectangle);
                if (Logo == null) return;
                float k = DeviceDpi / 96f;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Logo, new RectangleF(24 * k, 16 * k, 44 * k, 44 * k));
            }
        }

        /// Recuadro con esquinas redondeadas sobre el fondo; lo que va dentro (menos los botones) usa el color de superficie
        class Tarjeta : Panel
        {
            public Tarjeta() { DoubleBuffered = true; ResizeRedraw = true; Tema.Fondo(this, RolFondo.Fondo); }

            protected override void OnControlAdded(ControlEventArgs e)
            {
                base.OnControlAdded(e);
                if (!(e.Control is ButtonBase)) Tema.Fondo(e.Control, RolFondo.Superficie);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var camino = Redondeado(r, (int)(10 * DeviceDpi / 96f)))
                using (var relleno = new SolidBrush(Tema.Superficie))
                using (var borde = new Pen(Tema.Borde))
                {
                    g.FillPath(relleno, camino);
                    g.DrawPath(borde, camino);
                }
            }
        }

        /// Indicador de estado: un punto de color y un texto, opcionalmente sobre un fondo redondeado.
        /// Se coloca solo pegado al borde derecho de su contenedor.
        class Pastilla : Control
        {
            Rol rol = Rol.Texto;
            bool conFondo;
            public int MargenDerecho;   // en píxeles a 96 ppp
            public Pastilla()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                Font = new Font("Segoe UI Semibold", 9.5F);
            }

            float K => DeviceDpi / 96f;
            int Punto => (int)(8 * K);
            int Hueco => (int)(12 * K);

            public void Poner(string texto, Rol rol, bool conFondo)
            {
                Text = texto;
                this.rol = rol;
                this.conFondo = conFondo;
                var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
                Size = new Size(Hueco + Punto + (int)(8 * K) + t.Width + Hueco, t.Height + (int)(10 * K));
                if (Parent != null) Left = Parent.ClientSize.Width - Width - (int)(MargenDerecho * K);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var color = Tema.De(rol);
                using (var camino = Redondeado(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2))
                using (var b = new SolidBrush(conFondo ? Tema.FondoDe(rol) : Tema.Superficie))
                    g.FillPath(b, camino);
                using (var b = new SolidBrush(color))
                    g.FillEllipse(b, Hueco, (Height - Punto) / 2f, Punto, Punto);
                var zona = new Rectangle(Hueco + Punto + (int)(8 * K), 0, Width, Height);
                TextRenderer.DrawText(g, Text, Font, zona, color,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }

        /// Una fila de la lista: icono, nombre y "Quitar"
        class FilaDispositivo : Panel
        {
            public readonly LinkLabel Quitar;
            readonly bool ultima;

            public FilaDispositivo(string nombre, bool ultima)
            {
                this.ultima = ultima;
                Tema.Fondo(this, RolFondo.Superficie);
                DoubleBuffered = true;
                // Un círculo con la inicial del nombre, al estilo de los contactos
                var icono = new Inicial(nombre) { Dock = DockStyle.Left, Width = 40 };
                var texto = Tema.Pintar(new Label
                {
                    Text = string.IsNullOrWhiteSpace(nombre) ? "(sin nombre)" : nombre,
                    AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                }, Rol.Texto);
                Quitar = Tema.Pintar(new LinkLabel
                {
                    Text = "Quitar", AutoSize = false, Dock = DockStyle.Right, Width = 70, TextAlign = ContentAlignment.MiddleRight,
                }, Rol.Error);
                Controls.Add(texto);
                Controls.Add(Quitar);
                Controls.Add(icono);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (ultima) return;
                using (var p = new Pen(Tema.Separador))
                    e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
            }
        }

        class Inicial : Control
        {
            public Inicial(string nombre)
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                Tema.Fondo(this, RolFondo.Superficie);
                Text = string.IsNullOrWhiteSpace(nombre) ? "?" : nombre.Trim().Substring(0, 1).ToUpper();
                Font = new Font("Segoe UI Semibold", 9.5F);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int d = (int)(26 * DeviceDpi / 96f);
                var r = new Rectangle(0, (Height - d) / 2, d, d);
                using (var b = new SolidBrush(Tema.AvatarFondo))
                    g.FillEllipse(b, r);
                TextRenderer.DrawText(g, Text, Font, r, Tema.De(Rol.Destacado),
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }

        static GraphicsPath Redondeado(Rectangle r, int radio)
        {
            int d = Math.Max(1, Math.Min(radio * 2, Math.Min(r.Width, r.Height)));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// Ventanita para escribir el usuario y la contraseña del panel de Sunshine
    class DialogoCredenciales : Form
    {
        readonly TextBox usuario = new TextBox { Width = 220 }, clave = new TextBox { Width = 220, UseSystemPasswordChar = true };
        readonly Label error = Tema.Pintar(new Label { AutoSize = true, Text = "" }, Rol.Error);
        readonly Button aceptar = Tema.Boton(new Button { Text = "Aceptar", Width = 100, Height = 30 }, TipoBoton.Primario);

        public string Usuario => usuario.Text.Trim();
        public string Clave => clave.Text;

        public DialogoCredenciales()
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Contraseña de Sunshine";
            Font = new Font("Segoe UI", 10F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(20);
            Icon = Iconos.App;

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
            var explicacion = new Label
            {
                Text = "Es el usuario y la contraseña que elegiste para el panel de Sunshine al configurar RemotoFácil.",
                AutoSize = true, MaximumSize = new Size(360, 0), Margin = new Padding(0, 0, 0, 12),
            };
            t.Controls.Add(explicacion, 0, 0);
            t.SetColumnSpan(explicacion, 2);
            t.Controls.Add(new Label { Text = "Usuario:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) }, 0, 1);
            t.Controls.Add(usuario, 1, 1);
            t.Controls.Add(new Label { Text = "Contraseña:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) }, 0, 2);
            t.Controls.Add(clave, 1, 2);
            t.Controls.Add(error, 0, 3);
            t.SetColumnSpan(error, 2);

            var botones = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
            var cancelar = Tema.Boton(new Button { Text = "Cancelar", Width = 100, Height = 30, DialogResult = DialogResult.Cancel, Margin = new Padding(8, 3, 0, 3) }, TipoBoton.Secundario);
            botones.Controls.Add(cancelar);
            botones.Controls.Add(aceptar);
            t.Controls.Add(botones, 0, 4);
            t.SetColumnSpan(botones, 2);
            Controls.Add(t);

            AcceptButton = aceptar;
            CancelButton = cancelar;
            aceptar.Click += async (s, e) => await Comprobar();
            usuario.Text = Environment.UserName;
            Shown += (s, e) => clave.Focus();
            Tema.Fondo(this, RolFondo.Superficie);
            Tema.Seguir(this);
        }

        async Task Comprobar()
        {
            if (Usuario.Length == 0 || Clave.Length == 0) { error.Text = "Escribe el usuario y la contraseña."; return; }
            aceptar.Enabled = false;
            Tema.Pintar(error, Rol.Suave);
            error.Text = "Comprobando…";
            try
            {
                string u = Usuario, c = Clave;
                if (await Task.Run(() => Acciones.ComprobarCredenciales(u, c))) { DialogResult = DialogResult.OK; return; }
                Tema.Pintar(error, Rol.Error);
                error.Text = "No son correctos. Si no los recuerdas, usa \"Repetir la configuración\".";
            }
            catch (Exception ex)
            {
                Tema.Pintar(error, Rol.Error);
                error.Text = "Sunshine no responde: " + ex.Message;
            }
            finally { aceptar.Enabled = true; }
        }
    }
}
