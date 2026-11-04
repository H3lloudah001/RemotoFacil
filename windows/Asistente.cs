using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemotoFacil
{
    /// Datos que se van rellenando a lo largo del asistente
    class Estado
    {
        public string Ip;
        public string Usuario;
        public string Clave;
        public bool ApagarPantallas;
    }

    class Asistente : Form
    {
        public readonly Estado Datos;
        public readonly CancellationTokenSource AlCerrar = new CancellationTokenSource();

        readonly Label titulo = new Label(), subtitulo = new Label();
        readonly Panel contenido = new Panel();
        readonly Button atras = new Button(), siguiente = new Button(), cancelar = new Button();
        readonly List<Pagina> paginas;
        // Abierto desde el centro de control para una sola tarea (emparejar, elegir pantallas…)
        readonly bool suelto;
        public bool Suelto => suelto;
        int actual = -1;
        bool ocupado;
        bool terminado;

        public static readonly Color Acento = Color.FromArgb(0, 103, 192);

        /// Sin páginas: el asistente completo. Con páginas: solo esas, con los datos indicados.
        public Asistente(Estado datos = null, params Pagina[] soloEstas)
        {
            Datos = datos ?? new Estado();
            suelto = soloEstas.Length > 0;

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "RemotoFácil";
            Font = new Font("Segoe UI", 10F);
            ClientSize = new Size(780, 580);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = !suelto;
            ShowInTaskbar = !suelto;
            StartPosition = suelto ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
            Tema.Fondo(this, RolFondo.Superficie);
            Icon = Iconos.App;

            // Margen derecho amplio: ahí va el botón de tema, y los títulos no deben pasar por debajo
            var cabecera = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(28, 16, 190, 12) };
            titulo.Dock = DockStyle.Top; titulo.Height = 36;
            titulo.Font = new Font("Segoe UI Semibold", 16F); titulo.ForeColor = Color.White;
            subtitulo.Dock = DockStyle.Top; subtitulo.Height = 26;
            cabecera.Controls.Add(subtitulo);
            cabecera.Controls.Add(titulo);
            // Botón de tema arriba a la derecha (se coloca al cargar, cuando la cabecera ya tiene su ancho)
            var botonTema = Tema.CrearBoton();
            cabecera.Controls.Add(botonTema);
            botonTema.BringToFront();

            var pie = Tema.Fondo(new Panel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(20, 12, 20, 12) }, RolFondo.Pie);
            foreach (var b in new[] { cancelar, siguiente, atras })
            {
                b.Dock = DockStyle.Right; b.Width = 120;
                Tema.Boton(b, b == siguiente ? TipoBoton.Primario : TipoBoton.Secundario);
                pie.Controls.Add(b);
                if (b != atras) pie.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 10 });
            }
            atras.Text = "< Atrás";
            siguiente.Text = "Siguiente >";
            cancelar.Text = "Cancelar";
            atras.Click += (s, e) => Ir(actual - 1);
            siguiente.Click += async (s, e) => await Siguiente();
            cancelar.Click += (s, e) => Close();
            AcceptButton = siguiente;

            contenido.Dock = DockStyle.Fill;
            contenido.Padding = new Padding(28, 20, 28, 10);

            Controls.Add(contenido);
            Controls.Add(pie);
            Controls.Add(cabecera);

            // Todas las páginas se crean ya para que el escalado por DPI las alcance
            paginas = suelto
                ? soloEstas.ToList()
                : new List<Pagina>
                {
                    new PaginaBienvenida(), new PaginaInstalar(), new PaginaTailscale(), new PaginaSunshine(),
                    new PaginaPantallas(), new PaginaEmparejar(), new PaginaFin(),
                };
            foreach (var p in paginas)
            {
                p.A = this;
                p.Visible = false;
                contenido.Controls.Add(p);
            }
            Load += (s, e) =>
            {
                AjustarAPantalla(this);
                botonTema.Location = new Point(cabecera.ClientSize.Width - botonTema.Width - (int)(20 * DeviceDpi / 96f), (int)(14 * DeviceDpi / 96f));
                botonTema.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                Ir(0);
            };
            Tema.Seguir(this, () =>
            {
                cabecera.BackColor = Tema.CabeceraIzquierda;
                subtitulo.ForeColor = Tema.CabeceraSubtitulo;
            });
        }

        /// En portátiles pequeños con escalado alto la ventana puede no caber: se recorta y las páginas hacen scroll
        public static void AjustarAPantalla(Form f)
        {
            var area = Screen.FromControl(f).WorkingArea;
            if (f.Height > area.Height) f.Height = area.Height;
            if (f.Width > area.Width) f.Width = area.Width;
            f.Location = new Point(Math.Max(area.X, Math.Min(f.Left, area.Right - f.Width)),
                                   Math.Max(area.Y, Math.Min(f.Top, area.Bottom - f.Height)));
        }

        void Ir(int i)
        {
            if (i < 0 || i >= paginas.Count) return;
            if (actual >= 0) paginas[actual].Visible = false;
            actual = i;
            var p = paginas[i];
            titulo.Text = p.Titulo;
            subtitulo.Text = p.Subtitulo;
            p.Visible = true;
            p.AlMostrar();
            Refrescar();
        }

        bool EsUltima => actual == paginas.Count - 1;

        async Task Siguiente()
        {
            if (ocupado || !paginas[actual].PuedeSeguir) return;
            bool seguir;
            ocupado = true; Refrescar(); UseWaitCursor = true;
            try { seguir = await paginas[actual].AlSiguiente(); }
            catch (Exception ex) { Error(ex.Message); seguir = false; }
            finally { ocupado = false; UseWaitCursor = false; }
            if (!seguir) { Refrescar(); return; }
            if (EsUltima) { terminado = true; DialogResult = DialogResult.OK; Close(); }
            else Ir(actual + 1);
        }

        public void Refrescar()
        {
            if (actual < 0) return;
            var p = paginas[actual];
            // Una página suelta sin acción propia solo necesita "Cerrar"
            bool soloCerrar = suelto && p.TextoSiguiente == Pagina.TextoPorDefecto;
            atras.Visible = !suelto;
            atras.Enabled = !ocupado && !p.Ocupada && actual > 0 && !EsUltima;
            siguiente.Enabled = !ocupado && !p.Ocupada && p.PuedeSeguir;
            if (soloCerrar) siguiente.Text = "Cerrar";
            else if (suelto) siguiente.Text = p.TextoSiguiente.TrimEnd(' ', '>');   // "Aplicar"
            else siguiente.Text = EsUltima ? "Terminar" : p.TextoSiguiente;
            cancelar.Visible = suelto ? !soloCerrar : !EsUltima;
        }

        public void Error(string mensaje) =>
            MessageBox.Show(this, mensaje, "RemotoFácil", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        /// Abre la desinstalación encima de esta ventana; si se llega a desinstalar, esta se cierra también
        public void Desinstalar()
        {
            var pagina = new PaginaDesinstalar();
            using (var d = new Asistente(new Estado(), pagina)) d.ShowDialog(this);
            if (pagina.Hecho) { terminado = true; Close(); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Una tarea suelta a medias (desinstalando, aplicando pantallas…) no se interrumpe
            if (suelto && actual >= 0 && (paginas[actual].Ocupada || ocupado)) { e.Cancel = true; return; }
            if (!suelto && !terminado && actual > 0 && !EsUltima &&
                MessageBox.Show(this, "¿Seguro que quieres salir? La configuración quedará a medias.\n" +
                                      "Puedes volver a abrir RemotoFácil más tarde para terminarla.",
                                "RemotoFácil", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            AlCerrar.Cancel();
            base.OnFormClosing(e);
        }
    }

    // ======================================================================
    // Base de las páginas: una columna donde cada elemento va justo debajo del anterior.
    // Los textos ocupan todo el ancho y crecen hacia abajo según lo que necesiten; el scroll
    // solo aparece si de verdad no cabe todo.
    // ======================================================================

    abstract class Pagina : Panel
    {
        public const string TextoPorDefecto = "Siguiente >";

        public Asistente A;
        public abstract string Titulo { get; }
        public abstract string Subtitulo { get; }
        public virtual string TextoSiguiente => TextoPorDefecto;
        public virtual bool PuedeSeguir => true;
        public bool Ocupada { get; protected set; }
        public virtual void AlMostrar() { }
        public virtual Task<bool> AlSiguiente() => Task.FromResult(true);

        readonly HashSet<Control> anchoCompleto = new HashSet<Control>();
        bool colocando;

        protected Pagina()
        {
            Dock = DockStyle.Fill;
            AutoScroll = true;
        }

        protected T Poner<T>(T c, int margenAbajo = 10) where T : Control
        {
            c.Margin = new Padding(0, 0, 0, margenAbajo);
            // Lo que pedía anclarse a izquierda y derecha ocupa todo el ancho; la colocación la hace OnLayout
            if (c is Label l) { l.AutoSize = false; anchoCompleto.Add(c); }
            else if ((c.Anchor & (AnchorStyles.Left | AnchorStyles.Right)) == (AnchorStyles.Left | AnchorStyles.Right) && !c.AutoSize)
                anchoCompleto.Add(c);
            c.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            c.TextChanged += (s, e) => PerformLayout();
            Controls.Add(c);
            return c;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            if (!colocando)
            {
                colocando = true;
                try { Colocar(); }
                finally { colocando = false; }
            }
            base.OnLayout(e);
        }

        void Colocar()
        {
            int ancho = Math.Max(100, ClientSize.Width - Padding.Horizontal);
            int y0 = Padding.Top + AutoScrollPosition.Y, y = y0;
            foreach (Control c in Controls)
            {
                if (!c.Visible) continue;
                var m = c.Margin;
                int w = ancho - m.Left;
                Size tam;
                if (c is Label && c.Text.Length == 0)   // textos de estado vacíos no dejan hueco
                {
                    c.SetBounds(Padding.Left + m.Left, y, w, 0);
                    continue;
                }
                if (c is Label) tam = new Size(w, c.GetPreferredSize(new Size(w, 0)).Height);
                else if (anchoCompleto.Contains(c)) tam = new Size(w, c.Height);
                else if (c.AutoSize) tam = c.GetPreferredSize(Size.Empty);
                else tam = c.Size;
                c.SetBounds(Padding.Left + m.Left, y, tam.Width, tam.Height);
                y += tam.Height + m.Bottom;
            }
            AutoScrollMinSize = new Size(0, y - y0 + Padding.Bottom);
        }

        protected Label Texto(string texto, bool negrita = false, int margenAbajo = 10)
        {
            var l = new Label { Text = texto };
            if (negrita) l.Font = new Font("Segoe UI Semibold", 10.5F);
            return Poner(l, margenAbajo);
        }

        protected Button Boton(string texto)
        {
            var b = Tema.Boton(new Button { Text = texto, AutoSize = true, Padding = new Padding(10, 4, 10, 4) }, TipoBoton.Secundario);
            return Poner(b);
        }

        protected FlowLayoutPanel Fila(int margenAbajo = 10)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            return Poner(f, margenAbajo);
        }

        protected void Avisar() => A?.Refrescar();
    }

    // ======================================================================

    class PaginaBienvenida : Pagina
    {
        public override string Titulo => "Bienvenido a RemotoFácil";
        public override string Subtitulo => "Usa tu PC desde el móvil, la tablet, otro ordenador o la tele, estés donde estés";

        public PaginaBienvenida()
        {
            Texto("Este asistente deja tu PC listo en unos minutos:", margenAbajo: 6);
            Texto("•  Instala Tailscale, que crea una red privada solo para tus dispositivos. Así llegas al PC " +
                  "desde cualquier sitio, sin abrir puertos en el router.", margenAbajo: 4);
            Texto("•  Instala Sunshine, que transmite la pantalla del PC con muy poco retraso, y el driver " +
                  "que necesita para que el PC vea tus mandos.", margenAbajo: 4);
            Texto("•  Opcionalmente, apaga las pantallas que elijas mientras estás conectado.", margenAbajo: 4);
            Texto("•  Te ayuda a emparejar tus dispositivos con Moonlight.", margenAbajo: 18);
            Texto("Qué necesitas", negrita: true, margenAbajo: 6);
            Texto("•  Conexión a internet en el PC.", margenAbajo: 4);
            Texto("•  En el dispositivo desde el que te conectarás (móvil, tablet, portátil, tele con Android…), " +
                  "las apps gratuitas Tailscale y Moonlight. Puedes instalarlas ahora o cuando llegues a ese paso.", margenAbajo: 4);
            Texto("•  Una cuenta para Tailscale. Vale la de Google, Microsoft, Apple o GitHub.", margenAbajo: 18);
            desinstalar = new LinkLabel { Text = "¿Ya lo tenías instalado y quieres quitarlo? Desinstalar RemotoFácil", Visible = false };
            desinstalar.LinkClicked += (s, e) => A.Desinstalar();
            Poner(desinstalar);
        }

        readonly LinkLabel desinstalar;

        public override void AlMostrar() =>
            desinstalar.Visible = System.IO.File.Exists(Rutas.ExeInstalado) || Acciones.YaConfigurado;
    }

    // ======================================================================

    class PaginaInstalar : Pagina
    {
        public override string Titulo => "Instalando los programas";
        public override string Subtitulo => "Esto puede tardar unos minutos";
        public override bool PuedeSeguir => hecho;

        readonly Label estado;
        readonly ProgressBar barra = new ProgressBar { Height = 22, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        readonly TextBox registro = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 220,
            Anchor = AnchorStyles.Left | AnchorStyles.Right, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9F),
        };
        readonly Button reintentar;
        bool hecho, empezado;

        public PaginaInstalar()
        {
            estado = Texto("Preparando…");
            Poner(barra);
            Poner(registro);
            reintentar = Boton("Reintentar");
            reintentar.Visible = false;
            reintentar.Click += async (s, e) => await Instalar();
        }

        void Log(string t)
        {
            registro.AppendText(t + Environment.NewLine);
            Registro.Escribir(t);
        }

        public override async void AlMostrar()
        {
            if (!empezado) { empezado = true; await Instalar(); }
        }

        async Task Instalar()
        {
            reintentar.Visible = false;
            Ocupada = true; Avisar();
            var progreso = new Progress<int>(v => barra.Value = Math.Max(0, Math.Min(100, v)));
            try
            {
                estado.Text = "Copiando RemotoFácil en Archivos de programa…";
                await Task.Run(Acciones.CopiarseAProgramFiles);
                Log("RemotoFácil copiado en " + Rutas.CarpetaPrograma);

                if (Acciones.TailscaleInstalado) Log("Tailscale ya estaba instalado.");
                else
                {
                    estado.Text = "Descargando Tailscale…";
                    var msi = await Acciones.Descargar(Acciones.UrlTailscale, "tailscale.msi", progreso);
                    Log("Tailscale descargado.");
                    estado.Text = "Instalando Tailscale…";
                    barra.Style = ProgressBarStyle.Marquee;
                    await Task.Run(() => Acciones.InstalarMsi(msi, Acciones.OpcionesMsiTailscale));
                    barra.Style = ProgressBarStyle.Continuous;
                    Acciones.AnotarInstalado("tailscale");
                    Log("Tailscale instalado.");
                }

                if (Acciones.SunshineInstalado) Log("Sunshine ya estaba instalado.");
                else
                {
                    estado.Text = "Descargando Sunshine…";
                    barra.Value = 0;
                    var msi = await Acciones.Descargar(Acciones.UrlSunshine, "sunshine.msi", progreso);
                    Log("Sunshine descargado.");
                    estado.Text = "Instalando Sunshine…";
                    barra.Style = ProgressBarStyle.Marquee;
                    await Task.Run(() => Acciones.InstalarMsi(msi));
                    barra.Style = ProgressBarStyle.Continuous;
                    Acciones.AnotarInstalado("sunshine");
                    Log("Sunshine instalado.");
                }

                // Sunshine ya no trae driver de mandos: sin él, el PC no ve ningún mando al conectarse
                estado.Text = "Instalando el driver de mandos…";
                barra.Value = 0;
                bool pideReinicio = await Acciones.PrepararMandos(progreso, Log);
                if (pideReinicio) Log("Si los mandos no funcionan a la primera, reinicia el PC.");

                estado.Text = "Configurando el arranque automático y el cortafuegos…";
                barra.Style = ProgressBarStyle.Marquee;
                await Task.Run(Acciones.PrepararServicioSunshine);
                Log("Sunshine arrancará solo con Windows.");
                // Sunshine solo busca el driver de mandos al arrancar
                await Task.Run(Acciones.ReiniciarSunshine);
                barra.Style = ProgressBarStyle.Continuous;

                barra.Value = 100;
                estado.Text = "Todo instalado. Pulsa Siguiente.";
                hecho = true;
            }
            catch (Exception ex)
            {
                barra.Style = ProgressBarStyle.Continuous;
                estado.Text = "Algo ha fallado. Revisa tu conexión a internet y pulsa Reintentar.";
                Log("ERROR: " + ex.Message);
                reintentar.Visible = true;
            }
            finally { Ocupada = false; Avisar(); }
        }
    }

    // ======================================================================

    class PaginaTailscale : Pagina
    {
        public override string Titulo => "Conecta el PC a Tailscale";
        public override string Subtitulo => "La red privada entre tu PC y tus dispositivos";
        public override bool PuedeSeguir => A?.Datos.Ip != null;

        readonly Button entrar;
        readonly Label estado;
        readonly LinkLabel enlace;
        string url;

        public PaginaTailscale()
        {
            Texto("Pulsa el botón y se abrirá el navegador. Inicia sesión (por ejemplo, con tu cuenta de Google) " +
                  "y acepta conectar este equipo.");
            Texto("Importante: apunta qué cuenta usas. En los dispositivos desde los que te conectes tendrás que " +
                  "entrar en Tailscale con esa misma cuenta.", negrita: true, margenAbajo: 16);
            entrar = Boton("Iniciar sesión en Tailscale");
            entrar.Click += async (s, e) => await Entrar();
            estado = Texto("");
            enlace = new LinkLabel { Text = "¿No se ha abierto el navegador? Pulsa aquí.", Visible = false };
            enlace.LinkClicked += (s, e) => { if (url != null) Acciones.AbrirEnNavegador(url); };
            Poner(enlace);
        }

        public override async void AlMostrar()
        {
            if (A.Datos.Ip != null) return;
            var ip = await Task.Run(Acciones.IpTailscale);
            if (ip != null) { Conectado(ip, "Este PC ya está conectado a Tailscale."); return; }

            // Si Tailscale ya estaba instalado, puede que la persona inicie sesión desde su icono de la bandeja
            // en vez de con nuestro botón: lo detectamos solos mientras la página esté abierta.
            if (vigilar != null) return;
            vigilar = new System.Windows.Forms.Timer { Interval = 3000 };
            bool comprobando = false;
            vigilar.Tick += async (s, e) =>
            {
                if (!Visible || Ocupada || comprobando || A.Datos.Ip != null) return;
                comprobando = true;
                try
                {
                    var nueva = await Task.Run(Acciones.IpTailscale);
                    if (nueva != null && !Ocupada && A.Datos.Ip == null) Conectado(nueva, "¡Conectado!");
                }
                finally { comprobando = false; }
            };
            vigilar.Start();
        }

        System.Windows.Forms.Timer vigilar;

        void Conectado(string ip, string mensaje)
        {
            vigilar?.Stop();
            // El MSI ya lo deja así, pero si Tailscale estaba instalado de antes puede que no:
            // sin esto, tras reiniciar no se conecta hasta que alguien inicie sesión en Windows.
            Task.Run(() => Acciones.Ejecutar(Rutas.TailscaleExe, "set --unattended=true", 15000));
            // Lo instalamos sin abrir su ventana; ahora que ya hay sesión, que aparezca su icono en la bandeja
            Acciones.AbrirBandejaTailscale();
            A.Datos.Ip = ip;
            estado.Text = $"{mensaje}\nSu dirección en Tailscale es {ip}. Pulsa Siguiente.";
            Tema.Pintar(estado, Rol.Exito);
            entrar.Enabled = false;
            enlace.Visible = false;
            Avisar();
        }

        async Task Entrar()
        {
            entrar.Enabled = false;
            Ocupada = true; Avisar();
            Tema.Pintar(estado, Rol.Texto);
            estado.Text = "Conectando con Tailscale…";
            try
            {
                var (ok, salida) = await Acciones.IniciarSesionTailscale(u => BeginInvoke((Action)(() =>
                {
                    url = u;
                    estado.Text = "Esperando a que inicies sesión en el navegador…";
                    enlace.Visible = true;
                    Acciones.AbrirEnNavegador(u);
                })), A.AlCerrar.Token);

                var ip = ok ? Acciones.IpTailscale() : null;
                if (ip != null) Conectado(ip, "¡Conectado!");
                else
                {
                    Tema.Pintar(estado, Rol.Error);
                    estado.Text = "No se pudo conectar. Vuelve a intentarlo.\n" + salida;
                    entrar.Enabled = true;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Tema.Pintar(estado, Rol.Error);
                estado.Text = "Error: " + ex.Message;
                entrar.Enabled = true;
            }
            finally { Ocupada = false; Avisar(); }
        }
    }

    // ======================================================================

    class PaginaSunshine : Pagina
    {
        public override string Titulo => "Contraseña de Sunshine";
        public override string Subtitulo => "Protege el panel de control de Sunshine";

        readonly TextBox usuario = new TextBox { Width = 260 };
        readonly TextBox clave = new TextBox { Width = 260, UseSystemPasswordChar = true };
        readonly TextBox repetir = new TextBox { Width = 260, UseSystemPasswordChar = true };
        readonly CheckBox noSuspender = new CheckBox
        {
            Text = "No suspender el PC mientras esté enchufado (recomendado)",
            Checked = true, AutoSize = true,
        };

        public PaginaSunshine()
        {
            Texto("Sunshine tiene un panel web protegido con usuario y contraseña. RemotoFácil lo usa para emparejar " +
                  "tus dispositivos, y tú lo necesitarás si quieres cambiar ajustes más adelante. Apúntalos en algún sitio.",
                  margenAbajo: 16);

            var tabla = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
            tabla.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tabla.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            void Campo(string etiqueta, Control c)
            {
                tabla.Controls.Add(new Label { Text = etiqueta, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
                c.Margin = new Padding(0, 3, 0, 3);
                tabla.Controls.Add(c);
            }
            Campo("Usuario:", usuario);
            Campo("Contraseña:", clave);
            Campo("Repetir contraseña:", repetir);
            Poner(tabla, 4);

            var mostrar = new CheckBox { Text = "Mostrar contraseña", AutoSize = true };
            mostrar.CheckedChanged += (s, e) => clave.UseSystemPasswordChar = repetir.UseSystemPasswordChar = !mostrar.Checked;
            Poner(mostrar, 18);
            Poner(noSuspender, 2);
            Tema.Pintar(Texto("Si el PC se duerme, no podrás conectarte desde fuera."), Rol.Suave);

            usuario.Text = Environment.UserName;
        }

        public override async Task<bool> AlSiguiente()
        {
            var u = usuario.Text.Trim();
            var c = clave.Text;
            if (u.Length == 0) { A.Error("Escribe un nombre de usuario."); return false; }
            if (c.Length < 6) { A.Error("La contraseña debe tener al menos 6 caracteres."); return false; }
            if (c != repetir.Text) { A.Error("Las contraseñas no coinciden."); return false; }
            if (u.Contains(":")) { A.Error("El usuario no puede contener dos puntos (:)."); return false; }

            bool suspender = noSuspender.Checked;
            await Task.Run(() =>
            {
                Acciones.CredencialesSunshine(u, c);
                Acciones.GuardarCredenciales(u, c);
                if (suspender) Acciones.NoSuspenderEnchufado();
            });
            A.Datos.Usuario = u;
            A.Datos.Clave = c;
            return true;
        }
    }

    // ======================================================================

    class PaginaPantallas : Pagina
    {
        public override string Titulo => "¿Apagar pantallas al conectarte?";
        public override string Subtitulo => "Opcional: útil si tienes varias pantallas o una tele conectada";
        public override string TextoSiguiente => "Aplicar >";

        readonly RadioButton no = new RadioButton { Text = "No, dejar las pantallas como están", AutoSize = true, Checked = true };
        readonly RadioButton si = new RadioButton { Text = "Sí, apagar las pantallas que marque mientras estoy conectado", AutoSize = true };
        readonly CheckedListBox lista = new CheckedListBox
        {
            CheckOnClick = true, Height = 96, Anchor = AnchorStyles.Left | AnchorStyles.Right, IntegralHeight = false,
        };
        readonly Label aviso;
        readonly FlowLayoutPanel botones;
        List<Monitor> monitores = new List<Monitor>();

        public PaginaPantallas()
        {
            Texto("Mientras te conectas desde otro dispositivo, las pantallas que marques se apagarán, y se volverán a " +
                  "encender al terminar. Así ves justo la pantalla que quieres (por ejemplo, el monitor y no la tele).", margenAbajo: 6);
            var opciones = Poner(new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false }, 6);
            opciones.Controls.Add(no);
            opciones.Controls.Add(si);
            no.CheckedChanged += (s, e) => ActualizarHabilitado();

            Texto("Marca las pantallas que quieres APAGAR (al menos una tiene que quedarse encendida: esa es la que verás):", margenAbajo: 4);
            Poner(lista, 6);
            botones = Fila(6);
            var identificar = Tema.Boton(new Button { Text = "Identificar pantallas", AutoSize = true, Padding = new Padding(8, 2, 8, 2) }, TipoBoton.Secundario);
            var recargar = Tema.Boton(new Button { Text = "Volver a buscar", AutoSize = true, Padding = new Padding(8, 2, 8, 2) }, TipoBoton.Secundario);
            identificar.Click += (s, e) => Identificar();
            recargar.Click += (s, e) => Cargar();
            botones.Controls.Add(identificar);
            botones.Controls.Add(recargar);
            aviso = Texto("", margenAbajo: 0);
            Tema.Pintar(aviso, Rol.Suave);
        }

        public override void AlMostrar() => Cargar();

        void Cargar()
        {
            var marcadas = new HashSet<string>(lista.CheckedItems.Cast<Elemento>().Select(x => x.M.Ruta), StringComparer.OrdinalIgnoreCase);
            if (marcadas.Count == 0 && System.IO.File.Exists(Rutas.ArchivoPantallas))
                foreach (var l in System.IO.File.ReadAllLines(Rutas.ArchivoPantallas)) marcadas.Add(l.Split('\t')[0]);

            try { monitores = Pantallas.Listar(); }
            catch (Exception ex) { monitores = new List<Monitor>(); aviso.Text = "No se pudieron leer las pantallas: " + ex.Message; }

            lista.Items.Clear();
            for (int i = 0; i < monitores.Count; i++)
                lista.Items.Add(new Elemento(i + 1, monitores[i]), marcadas.Contains(monitores[i].Ruta));
            if (lista.CheckedItems.Count > 0) si.Checked = true;

            aviso.Text = monitores.Count < 2
                ? "Solo se ha detectado una pantalla encendida, así que este paso no hace falta. " +
                  "Si tienes más, enciéndelas todas y pulsa \"Volver a buscar\"."
                : "Las pantallas se vuelven a encender cuando cierras la sesión en Moonlight (mantén pulsado el PC → \"Quit app\"). " +
                  "Si alguna se queda apagada, usa el acceso directo \"Restaurar pantallas\" del escritorio o pulsa Win+P → Extender.";
            ActualizarHabilitado();
        }

        void ActualizarHabilitado()
        {
            si.Enabled = monitores.Count >= 2;
            if (!si.Enabled) no.Checked = true;
            lista.Enabled = botones.Enabled = si.Checked;
        }

        void Identificar()
        {
            var ventanas = new List<Form>();
            foreach (var scr in Screen.AllScreens)
            {
                int i = monitores.FindIndex(m => string.Equals(m.Gdi, scr.DeviceName, StringComparison.OrdinalIgnoreCase));
                var texto = i >= 0 ? $"{i + 1}\n{monitores[i].Nombre}" : scr.DeviceName;
                var f = new Form
                {
                    FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, TopMost = true,
                    ShowInTaskbar = false, BackColor = Asistente.Acento,
                    Bounds = new Rectangle(scr.Bounds.X + scr.Bounds.Width / 2 - 260, scr.Bounds.Y + scr.Bounds.Height / 2 - 130, 520, 260),
                };
                f.Controls.Add(new Label
                {
                    Text = texto, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 34F),
                });
                f.Show();
                ventanas.Add(f);
            }
            var t = new System.Windows.Forms.Timer { Interval = 3000 };
            t.Tick += (s, e) => { t.Dispose(); ventanas.ForEach(v => v.Close()); };
            t.Start();
        }

        public override async Task<bool> AlSiguiente()
        {
            var apagar = si.Checked ? lista.CheckedItems.Cast<Elemento>().Select(x => x.M).ToList() : new List<Monitor>();
            if (si.Checked && apagar.Count == 0) { A.Error("Marca al menos una pantalla para apagar, o elige \"No\"."); return false; }
            if (apagar.Count >= monitores.Count) { A.Error("Tiene que quedar al menos una pantalla encendida para poder verla."); return false; }

            bool activar = apagar.Count > 0;
            await Task.Run(() =>
            {
                Acciones.GuardarPantallasElegidas(apagar);
                Acciones.ConfigurarPantallasEnSunshine(activar);
                Acciones.CrearAccesosDirectos(restaurarPantallas: activar);
                Acciones.ReiniciarSunshine();
            });
            A.Datos.ApagarPantallas = activar;
            return true;
        }

        class Elemento
        {
            public readonly Monitor M;
            readonly int n;
            public Elemento(int n, Monitor m) { this.n = n; M = m; }
            public override string ToString() =>
                $"{n}.  {M.Nombre}  —  {M.Ancho}×{M.Alto}" + (M.Principal ? "  (principal)" : "");
        }
    }

    // ======================================================================

    class PaginaEmparejar : Pagina
    {
        public override string Titulo => "Empareja un dispositivo";
        public override string Subtitulo => "Solo hay que hacerlo una vez por dispositivo";

        readonly Label ip, solicitud, estado, masTarde;
        readonly TextBox pin = new TextBox { Width = 90, MaxLength = 4, Font = new Font("Segoe UI", 14F), TextAlign = HorizontalAlignment.Center };
        readonly TextBox nombre = new TextBox { Width = 200, Text = NombrePorDefecto };
        readonly Button emparejar = Tema.Boton(new Button { Text = "Emparejar", AutoSize = true, Padding = new Padding(10, 4, 10, 4) }, TipoBoton.Primario);
        readonly System.Windows.Forms.Timer vigilar = new System.Windows.Forms.Timer { Interval = 2000 };
        const string NombrePorDefecto = "Mi dispositivo";
        string idPendiente, idAnunciado;
        bool nombreEditado, consultando;

        public PaginaEmparejar()
        {
            Texto("1.  En el dispositivo, abre Tailscale, inicia sesión con la misma cuenta que en el PC y actívalo.", margenAbajo: 6);
            Texto("2.  Abre Moonlight. Si el PC no aparece solo, pulsa el botón + y escribe esta dirección:", margenAbajo: 4);
            var filaIp = Fila(6);
            ip = Tema.Pintar(new Label { AutoSize = true, Font = new Font("Consolas", 15F, FontStyle.Bold), Margin = new Padding(24, 2, 12, 0) }, Rol.Destacado);
            var copiar = Tema.Boton(new Button { Text = "Copiar", AutoSize = true, Padding = new Padding(6, 0, 6, 0) }, TipoBoton.Secundario);
            copiar.Click += (s, e) => { if (A.Datos.Ip != null) Clipboard.SetText(A.Datos.Ip); };
            filaIp.Controls.Add(ip);
            filaIp.Controls.Add(copiar);
            Texto("3.  Toca el PC en Moonlight. Te mostrará un PIN de 4 cifras: escríbelo aquí.", margenAbajo: 8);

            var filaPin = Fila(6);
            filaPin.Controls.Add(new Label { Text = "PIN:", AutoSize = true, Margin = new Padding(24, 10, 6, 0) });
            filaPin.Controls.Add(pin);
            filaPin.Controls.Add(new Label { Text = "Nombre:", AutoSize = true, Margin = new Padding(18, 10, 6, 0) });
            nombre.Margin = new Padding(0, 7, 12, 0);
            filaPin.Controls.Add(nombre);
            filaPin.Controls.Add(emparejar);
            emparejar.Margin = new Padding(0, 5, 0, 0);
            emparejar.Click += async (s, e) => await Emparejar();
            pin.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            nombre.KeyPress += (s, e) => nombreEditado = true;

            solicitud = Texto("", margenAbajo: 6);
            solicitud.Margin = new Padding(24, 0, 0, 6);
            estado = Texto("");
            masTarde = Texto("También puedes emparejar más tarde desde RemotoFácil (\"Agregar dispositivo\").");
            Tema.Pintar(masTarde, Rol.Suave);

            vigilar.Tick += async (s, e) => await BuscarSolicitud();
            VisibleChanged += (s, e) => { if (Visible) vigilar.Start(); else vigilar.Stop(); };
            Disposed += (s, e) => vigilar.Dispose();
        }

        public override void AlMostrar()
        {
            ip.Text = A.Datos.Ip ?? "(sin IP)";
            masTarde.Visible = !A.Suelto;   // desde el centro de control ya estás en "Agregar dispositivo"
            _ = BuscarSolicitud();
        }

        /// Mira si Moonlight ya está pidiendo emparejarse, para avisar en cuanto la persona toque el PC
        async Task BuscarSolicitud()
        {
            if (consultando || Ocupada || A?.Datos.Usuario == null) return;
            consultando = true;
            try
            {
                string u = A.Datos.Usuario, c = A.Datos.Clave;
                var lista = await Task.Run(() => Acciones.SolicitudesPendientes(u, c));
                var ultima = lista.LastOrDefault();
                idPendiente = ultima?.Id;
                if (ultima == null)
                {
                    Tema.Pintar(solicitud, Rol.Suave);
                    solicitud.Text = "Esperando a que toques el PC en Moonlight…";
                }
                else
                {
                    var quien = string.IsNullOrWhiteSpace(ultima.Nombre) ? "Un dispositivo" : $"\"{ultima.Nombre}\"";
                    var desde = string.IsNullOrWhiteSpace(ultima.Direccion) ? "" : $" ({ultima.Direccion})";
                    Tema.Pintar(solicitud, Rol.Exito);
                    solicitud.Text = $"{quien}{desde} está esperando: escribe el PIN que te muestra.";
                    if (ultima.Id != idAnunciado)
                    {
                        // Solo la primera vez que aparece cada solicitud, para no quitarle el foco a quien está escribiendo
                        idAnunciado = ultima.Id;
                        if (!nombreEditado && !string.IsNullOrWhiteSpace(ultima.Nombre)) nombre.Text = ultima.Nombre;
                        pin.Focus();
                    }
                }
            }
            catch { solicitud.Text = ""; }   // Sunshine reiniciándose, etc.: no molestar
            finally { consultando = false; }
        }

        async Task Emparejar()
        {
            if (pin.Text.Length != 4) { A.Error("El PIN tiene 4 cifras."); return; }
            if (A.Datos.Usuario == null) { A.Error("Falta la contraseña de Sunshine."); return; }
            var n = string.IsNullOrWhiteSpace(nombre.Text) ? NombrePorDefecto : nombre.Text.Trim();
            emparejar.Enabled = false;
            Ocupada = true; Avisar();
            Tema.Pintar(estado, Rol.Texto);
            estado.Text = "Emparejando… (puede tardar unos segundos)";
            try
            {
                string p = pin.Text, u = A.Datos.Usuario, c = A.Datos.Clave, id = idPendiente;
                bool ok = await Task.Run(() => Acciones.Emparejar(u, c, p, n, id));
                if (ok)
                {
                    Tema.Pintar(estado, Rol.Exito);
                    estado.Text = $"¡\"{n}\" emparejado! Ya puedes tocar \"Desktop\" en Moonlight para ver el PC. " +
                                  "Si tienes otro dispositivo, repite los pasos con él.";
                    pin.Text = "";
                    nombre.Text = NombrePorDefecto;
                    nombreEditado = false;
                }
                else
                {
                    Tema.Pintar(estado, Rol.Error);
                    estado.Text = "El PIN no es correcto o ha caducado. Vuelve a tocar el PC en Moonlight y escribe el PIN nuevo.";
                }
            }
            catch (Exception ex)
            {
                Tema.Pintar(estado, Rol.Error);
                estado.Text = ex.Message;
            }
            finally { emparejar.Enabled = true; Ocupada = false; Avisar(); }
            _ = BuscarSolicitud();
        }
    }

    // ======================================================================

    class PaginaFin : Pagina
    {
        public override string Titulo => "¡Listo!";
        public override string Subtitulo => "Tu PC ya está preparado";

        readonly Label resumen, pantallas;
        readonly CheckBox escritorio = new CheckBox { Text = "Crear un acceso directo a RemotoFácil en el escritorio", AutoSize = true, Checked = true };

        public PaginaFin()
        {
            Texto("Para conectarte desde cualquier sitio", negrita: true, margenAbajo: 4);
            resumen = Texto("", margenAbajo: 14);
            Texto("Consejos", negrita: true, margenAbajo: 4);
            Texto("•  Deja el PC encendido (o en pantalla de bloqueo). Si está apagado, no podrás conectarte.", margenAbajo: 4);
            Texto("•  Si puedes, conecta el PC al router por cable: la imagen irá más fluida.", margenAbajo: 4);
            pantallas = Texto("", margenAbajo: 4);
            Texto("•  Cuando vuelvas a abrir RemotoFácil (desde el escritorio o el menú Inicio) verás su centro de control: " +
                  "tu dirección, los dispositivos emparejados, y accesos a Sunshine y Tailscale.", margenAbajo: 14);
            Poner(escritorio, 0);

            // La primera vez se propone crearlo; al repetir la configuración, la casilla refleja si ya existe
            // (las páginas se crean al abrir el asistente, antes de marcar nada como configurado)
            if (Acciones.YaConfigurado) escritorio.Checked = System.IO.File.Exists(Acciones.AccesoEscritorioApp);
        }

        public override async Task<bool> AlSiguiente()
        {
            bool crear = escritorio.Checked;
            try { await Task.Run(() => Acciones.AccesoEnEscritorio(crear)); }
            catch (Exception ex) { Registro.Escribir("No se pudo cambiar el acceso del escritorio: " + ex.Message); }
            return true;
        }

        public override void AlMostrar()
        {
            try { Acciones.MarcarConfigurado(); }
            catch (Exception ex) { Registro.Escribir("No se pudo marcar como configurado: " + ex.Message); }

            resumen.Text = "1.  Activa Tailscale en el dispositivo.\n" +
                           "2.  Abre Moonlight y toca tu PC" + (A.Datos.Ip != null ? $" ({A.Datos.Ip})" : "") + ".\n" +
                           "3.  Elige \"Desktop\" para ver el escritorio, o \"Steam Big Picture\" para jugar.";
            pantallas.Text = A.Datos.ApagarPantallas
                ? "•  Para terminar, en Moonlight mantén pulsado el PC → \"Quit app\". Así se vuelven a encender las pantallas. " +
                  "Si alguna se queda apagada, usa \"Restaurar pantallas\" en el escritorio."
                : "•  Para terminar, en Moonlight mantén pulsado el PC → \"Quit app\".";
        }
    }
}
