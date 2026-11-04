using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace RemotoFacil
{
    /// <summary>
    /// Lo que se abre cuando el PC ya está configurado: la dirección para conectarse, el estado de
    /// Tailscale y Sunshine, los dispositivos emparejados y accesos rápidos a todo lo demás.
    /// </summary>
    class CentroControl : Window
    {
        readonly TextBlock ip = new TextBlock { FontSize = 32, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock tsCuenta = new TextBlock(), tsEquipo = new TextBlock(), ssPantallas = new TextBlock();
        readonly Indicador estadoGeneral = new Indicador(conFondo: true), tsEstado = new Indicador(false), ssEstado = new Indicador(false);
        readonly Button copiar = new Button { Content = "Copiar", VerticalAlignment = VerticalAlignment.Center };
        readonly Button tsConectar = new Button { Content = "Conectar", IsVisible = false };
        readonly Button ssReiniciar = new Button { Content = "Reiniciar" };
        readonly StackPanel lista = new StackPanel();
        readonly DispatcherTimer temporizador = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        readonly CancellationTokenSource alCerrar = new CancellationTokenSource();

        Tailscale.Info tailscale;
        (string usuario, string clave)? credenciales;
        bool refrescando, tareaEnCurso;

        public CentroControl()
        {
            Title = "RemotoFácil";
            Icon = Iconos.Ventana();
            Width = 840; Height = 620;
            MinWidth = 720; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Tema.Fondo(this, "Fondo");

            // ---------- Cabecera ----------
            var cabecera = Tema.Fondo(new Border { Height = 80, Padding = new Thickness(24, 0, 20, 0) }, "Cabecera");
            var filaCab = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var logo = new Image { Source = Iconos.Logo(), Width = 46, Height = 46, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            textos.Children.Add(new TextBlock { Text = "RemotoFácil", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White });
            textos.Children.Add(Tema.Texto(new TextBlock { Text = "Centro de control de " + Environment.MachineName }, "CabeceraSubtitulo"));
            var derecha = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var actualizar = Tema.Fondo(new Button { Content = "Actualizar", Foreground = Brushes.White, Padding = new Thickness(12, 6) }, "BotonCabecera");
            actualizar.Click += async (s, e) => await RefrescarTodo();
            derecha.Children.Add(Tema.CrearBoton());
            derecha.Children.Add(actualizar);
            Grid.SetColumn(textos, 1); Grid.SetColumn(derecha, 2);
            filaCab.Children.Add(logo); filaCab.Children.Add(textos); filaCab.Children.Add(derecha);
            cabecera.Child = filaCab;

            // ---------- Dirección ----------
            var principal = Tarjeta();
            var gp = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
            gp.Children.Add(Tema.Texto(new TextBlock { Text = "Para conectarte desde Moonlight, usa esta dirección", FontSize = 13 }, "Suave"));
            var filaIp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 2, 0, 4) };
            filaIp.Children.Add(Tema.Texto(ip, "Texto"));
            filaIp.Children.Add(copiar);
            copiar.Click += async (s, e) => await Copiar();
            Grid.SetRow(filaIp, 1);
            gp.Children.Add(filaIp);
            var ayuda = Tema.Texto(new TextBlock
            {
                Text = "En el otro dispositivo: activa Tailscale con la misma cuenta, abre Moonlight y toca este PC.",
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            }, "Suave");
            Grid.SetRow(ayuda, 2); Grid.SetColumnSpan(ayuda, 2);
            gp.Children.Add(ayuda);
            estadoGeneral.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(estadoGeneral, 1); Grid.SetRowSpan(estadoGeneral, 2);
            gp.Children.Add(estadoGeneral);
            principal.Child = gp;

            // ---------- Tailscale y Sunshine ----------
            var tsAbrir = new Button { Content = "Abrir Tailscale" };
            ToolTip.SetTip(tsAbrir, "Abre el panel web de Tailscale, donde ves todos los dispositivos de tu cuenta");
            tsAbrir.Click += (s, e) => Sistema.AbrirUrl("https://login.tailscale.com/admin/machines");
            tsConectar.Classes.Add("accent");
            tsConectar.Click += async (s, e) => await ConectarTailscale();
            var ts = TarjetaServicio("Tailscale", tsEstado, new[] { tsCuenta, tsEquipo }, tsAbrir, tsConectar);

            var ssAbrir = new Button { Content = "Abrir Sunshine" };
            ToolTip.SetTip(ssAbrir, "Ajustes avanzados de Sunshine: calidad, aplicaciones, registros…");
            ssAbrir.Click += (s, e) => Sistema.AbrirUrl(Sunshine.UrlWeb);
            ssReiniciar.Click += async (s, e) => await ReiniciarSunshine();
            var ss = TarjetaServicio("Sunshine", ssEstado, new[] { new TextBlock { Text = "Panel web: " + Sunshine.UrlWeb }, ssPantallas }, ssAbrir, ssReiniciar);

            var servicios = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
            Grid.SetColumn(ss, 2);
            servicios.Children.Add(ts); servicios.Children.Add(ss);

            // ---------- Dispositivos ----------
            var disp = Tarjeta();
            var gd = new DockPanel();
            var cabDisp = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
            cabDisp.Children.Add(Tema.Texto(new TextBlock { Text = "Dispositivos emparejados", FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, "Texto"));
            var agregar = new Button { Content = "+  Agregar dispositivo" };
            agregar.Classes.Add("accent");
            agregar.Click += async (s, e) => await AgregarDispositivo();
            Grid.SetColumn(agregar, 1);
            cabDisp.Children.Add(agregar);
            DockPanel.SetDock(cabDisp, Dock.Top);
            gd.Children.Add(cabDisp);
            gd.Children.Add(new ScrollViewer { Content = lista, MaxHeight = 150 });
            disp.Child = gd;

            // ---------- Accesos de abajo ----------
            var abajo = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            void Acceso(string texto, string consejo, Func<Task> accion, bool visible = true)
            {
                var b = new Button { Content = texto, Margin = new Thickness(0, 0, 8, 6), IsVisible = visible };
                ToolTip.SetTip(b, consejo);
                b.Click += async (s, e) => await accion();
                abajo.Children.Add(b);
            }
            Acceso("Pantallas al conectarse", "Elegir qué pantallas se apagan mientras estás conectado", ElegirPantallas, Pantallas.Motor != null);
            Acceso("Restaurar pantallas", "Volver a encender las pantallas si alguna se quedó apagada", RestaurarPantallas, Pantallas.Motor != null);
            Acceso("Repetir la configuración", "Abrir de nuevo el asistente completo", RepetirConfiguracion);
            Acceso("Ver registro", "Abrir el archivo de registro de RemotoFácil (útil si algo falla)", VerRegistro);
            Acceso("Desinstalar", "Quitar RemotoFácil y, si quieres, Tailscale y Sunshine", Desinstalar);

            var cuerpo = new StackPanel { Margin = new Thickness(20, 16, 20, 12), Spacing = 12 };
            cuerpo.Children.Add(principal);
            cuerpo.Children.Add(servicios);
            cuerpo.Children.Add(disp);
            cuerpo.Children.Add(abajo);

            var raiz = new DockPanel();
            DockPanel.SetDock(cabecera, Dock.Top);
            raiz.Children.Add(cabecera);
            raiz.Children.Add(new ScrollViewer { Content = cuerpo });
            Content = raiz;

            temporizador.Tick += async (s, e) => await RefrescarEstado();
            Opened += async (s, e) =>
            {
                credenciales = Sunshine.LeerCredenciales();
                await RefrescarTodo();
                temporizador.Start();
            };
            Closed += (s, e) => { alCerrar.Cancel(); temporizador.Stop(); };
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
                var (info, activo, instalado, pantallas) = await Task.Run(() =>
                    (Tailscale.Estado(), Sunshine.Activo(), Sunshine.Instalado, Pantallas.Elegidas().Count));
                tailscale = info;
                MostrarTailscale(info);
                MostrarSunshine(instalado, activo, pantallas);
                if (info == null) estadoGeneral.Poner("Tailscale no responde", "Error");
                else if (info.Estado is "NeedsLogin" or "NoState") estadoGeneral.Poner("Falta iniciar sesión en Tailscale", "Error");
                else if (!info.Conectado) estadoGeneral.Poner("Tailscale está desactivado", "Aviso");
                else if (!activo) estadoGeneral.Poner("Sunshine no está funcionando", "Error");
                else estadoGeneral.Poner("Listo para conectarte", "Exito");
            }
            finally { refrescando = false; }
        }

        void MostrarTailscale(Tailscale.Info info)
        {
            ip.Text = info?.Ip ?? "Sin dirección";
            copiar.IsEnabled = info?.Ip != null;
            if (info == null) tsEstado.Poner("No responde", "Error");
            else if (info.Conectado) tsEstado.Poner("Conectado", "Exito");
            else if (info.Estado is "NeedsLogin" or "NoState") tsEstado.Poner("Sin iniciar sesión", "Error");
            else tsEstado.Poner("Desactivado", "Aviso");
            tsCuenta.Text = "Cuenta: " + (info?.Cuenta ?? "—");
            tsEquipo.Text = "Nombre del equipo: " + (info?.Equipo ?? Environment.MachineName.ToLowerInvariant());
            tsConectar.IsVisible = info != null && !info.Conectado;
            tsConectar.Content = info?.Estado == "Stopped" ? "Activar" : "Iniciar sesión";
        }

        void MostrarSunshine(bool instalado, bool activo, int pantallas)
        {
            if (!instalado) ssEstado.Poner("No instalado", "Error");
            else if (activo) ssEstado.Poner("Funcionando", "Exito");
            else ssEstado.Poner("Detenido", "Error");
            ssReiniciar.Content = activo ? "Reiniciar" : "Iniciar";
            ssReiniciar.IsEnabled = instalado && !tareaEnCurso;
            ssPantallas.Text = pantallas == 0
                ? "Al conectarte: no se apaga ninguna pantalla"
                : $"Al conectarte: se apaga{(pantallas == 1 ? "" : "n")} {pantallas} pantalla{(pantallas == 1 ? "" : "s")}";
        }

        // ================== Dispositivos ==================

        async Task RefrescarDispositivos()
        {
            if (credenciales == null)
            {
                MensajeLista("Escribe la contraseña de Sunshine para ver y gestionar los dispositivos.", "Escribir contraseña",
                             async () => { if (await PedirCredenciales()) await RefrescarDispositivos(); });
                return;
            }
            var (u, c) = credenciales.Value;
            try
            {
                var l = await Task.Run(() => Sunshine.Dispositivos(u, c));
                if (l.Count == 0) MensajeLista("Todavía no hay ningún dispositivo emparejado. Pulsa \"Agregar dispositivo\" para empezar.");
                else MostrarDispositivos(l);
            }
            catch (Sunshine.CredencialesIncorrectas)
            {
                Sunshine.BorrarCredenciales();
                credenciales = null;
                MensajeLista("La contraseña de Sunshine guardada ya no es válida.", "Escribir la nueva",
                             async () => { if (await PedirCredenciales()) await RefrescarDispositivos(); });
            }
            catch (Exception ex)
            {
                Registro.Escribir("No se pudo leer la lista de dispositivos: " + ex.Message);
                MensajeLista("Sunshine no responde ahora mismo, así que no se puede ver la lista.", "Reintentar", RefrescarDispositivos);
            }
        }

        void MensajeLista(string texto, string enlace = null, Func<Task> accion = null)
        {
            lista.Children.Clear();
            lista.Children.Add(Tema.Texto(new TextBlock { Text = texto, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) }, "Suave"));
            if (enlace != null) lista.Children.Add(Ui.Enlace(enlace, () => _ = accion()));
        }

        void MostrarDispositivos(List<Sunshine.Dispositivo> l)
        {
            lista.Children.Clear();
            foreach (var d in l)
            {
                var fila = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 38 };
                var inicial = new Grid { Width = 28, Height = 28, Margin = new Thickness(0, 0, 12, 0) };
                inicial.Children.Add(Tema.Enlazar(new Ellipse(), Shape.FillProperty, "AvatarFondo"));
                inicial.Children.Add(Tema.Texto(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(d.Nombre) ? "?" : d.Nombre.Trim().Substring(0, 1).ToUpper(),
                    FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                }, "Destacado"));
                var nombre = Tema.Texto(new TextBlock { Text = string.IsNullOrWhiteSpace(d.Nombre) ? "(sin nombre)" : d.Nombre, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, "Texto");
                var dispositivo = d;
                var quitar = Ui.Enlace("Quitar", () => _ = Quitar(dispositivo), "Error");
                quitar.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(nombre, 1); Grid.SetColumn(quitar, 2);
                fila.Children.Add(inicial); fila.Children.Add(nombre); fila.Children.Add(quitar);
                lista.Children.Add(fila);
                if (d != l.Last()) lista.Children.Add(Tema.Fondo(new Border { Height = 1 }, "Separador"));
            }
        }

        async Task Quitar(Sunshine.Dispositivo d)
        {
            if (!await Dialogo.Preguntar(this, $"¿Quitar \"{d.Nombre}\"?\n\nYa no podrá conectarse a este PC hasta que lo vuelvas a emparejar.")) return;
            var (u, c) = credenciales.Value;
            try
            {
                if (!await Task.Run(() => Sunshine.QuitarDispositivo(u, c, d.Uuid)))
                    await Dialogo.Avisar(this, "Sunshine no pudo quitar el dispositivo. Prueba desde el panel de Sunshine.");
            }
            catch (Exception ex) { await Dialogo.Avisar(this, ex.Message); }
            await RefrescarDispositivos();
        }

        async Task AgregarDispositivo()
        {
            if (credenciales == null && !await PedirCredenciales()) return;
            if (tailscale?.Ip == null)
            {
                await Dialogo.Avisar(this, "Este PC no está conectado a Tailscale ahora mismo, así que los dispositivos no podrán llegar a él.\n\n" +
                                           "Conéctalo desde la tarjeta de Tailscale y vuelve a intentarlo.");
                return;
            }
            var (u, c) = credenciales.Value;
            await new Asistente(new Estado { Ip = tailscale.Ip, Usuario = u, Clave = c }, new PaginaEmparejar()).ShowDialog(this);
            await RefrescarDispositivos();
        }

        async Task<bool> PedirCredenciales()
        {
            var d = new DialogoCredenciales();
            await d.ShowDialog(this);
            if (!d.Aceptado) return false;
            credenciales = (d.Usuario, d.Clave);
            Sunshine.GuardarCredenciales(d.Usuario, d.Clave);
            return true;
        }

        // ================== Acciones ==================

        async Task Copiar()
        {
            if (tailscale?.Ip == null) return;
            await Clipboard.SetTextAsync(tailscale.Ip);
            copiar.Content = "¡Copiada!";
            await Task.Delay(1500);
            copiar.Content = "Copiar";
        }

        async Task ConectarTailscale()
        {
            if (tareaEnCurso) return;
            tareaEnCurso = true;
            tsConectar.IsEnabled = false;
            tsEstado.Poner("Conectando…", "Aviso");
            try
            {
                var (ok, salida) = await Tailscale.IniciarSesion(url => Ui.EnUi(() => Sistema.AbrirUrl(url)), alCerrar.Token);
                if (!ok) await Dialogo.Avisar(this, "No se pudo conectar Tailscale.\n\n" + salida);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { await Dialogo.Avisar(this, ex.Message); }
            finally { tareaEnCurso = false; tsConectar.IsEnabled = true; }
            await RefrescarEstado();
        }

        async Task ReiniciarSunshine()
        {
            if (tareaEnCurso) return;
            tareaEnCurso = true;
            ssReiniciar.IsEnabled = false;
            ssEstado.Poner("Reiniciando…", "Aviso");
            try { await Task.Run(() => { if (!Sunshine.Activo()) Sunshine.ActivarServicio(); else Sunshine.Reiniciar(); }); }
            catch (Exception ex) { await Dialogo.Avisar(this, "No se pudo reiniciar Sunshine:\n" + ex.Message); }
            finally { tareaEnCurso = false; }
            await RefrescarTodo();
        }

        async Task ElegirPantallas()
        {
            await new Asistente(new Estado(), new PaginaPantallas()).ShowDialog(this);
            await RefrescarEstado();
        }

        async Task RestaurarPantallas()
        {
            try { await Task.Run(() => Pantallas.Restaurar(forzar: true)); }
            catch (Exception ex) { await Dialogo.Avisar(this, "No se pudieron restaurar las pantallas:\n" + ex.Message); }
        }

        async Task RepetirConfiguracion()
        {
            temporizador.Stop();
            try { await new Asistente().ShowDialog(this); }
            finally
            {
                credenciales = Sunshine.LeerCredenciales();
                temporizador.Start();
            }
            await RefrescarTodo();
        }

        async Task VerRegistro()
        {
            if (!File.Exists(Rutas.Registro)) { await Dialogo.Avisar(this, "Todavía no hay nada en el registro."); return; }
            Sistema.AbrirUrl(Rutas.Registro);
        }

        async Task Desinstalar()
        {
            temporizador.Stop();
            var pagina = new PaginaDesinstalar();
            await new Asistente(new Estado(), pagina).ShowDialog(this);
            if (pagina.Hecho) { Close(); return; }
            temporizador.Start();
            await RefrescarTodo();
        }

        // ================== Piezas visuales ==================

        static Border Tarjeta()
        {
            var b = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(20, 14) };
            Tema.Fondo(b, "Superficie");
            Tema.Enlazar(b, Border.BorderBrushProperty, "Borde");
            return b;
        }

        static Border TarjetaServicio(string titulo, Indicador estado, TextBlock[] lineas, params Button[] botones)
        {
            var t = Tarjeta();
            var p = new StackPanel { Spacing = 2 };
            var cab = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
            cab.Children.Add(Tema.Texto(new TextBlock { Text = titulo, FontSize = 16, FontWeight = FontWeight.SemiBold }, "Texto"));
            Grid.SetColumn(estado, 1);
            cab.Children.Add(estado);
            p.Children.Add(cab);
            foreach (var l in lineas) { l.FontSize = 13; l.TextTrimming = TextTrimming.CharacterEllipsis; p.Children.Add(Tema.Texto(l, "Suave")); }
            var fila = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
            foreach (var b in botones) fila.Children.Add(b);
            p.Children.Add(fila);
            t.Child = p;
            return t;
        }

        /// Un punto de color y un texto ("● Conectado"), opcionalmente sobre un fondo redondeado
        class Indicador : Border
        {
            readonly Ellipse punto = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            readonly TextBlock texto = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            readonly bool conFondo;

            public Indicador(bool conFondo)
            {
                this.conFondo = conFondo;
                CornerRadius = new CornerRadius(14);
                Padding = conFondo ? new Thickness(12, 5) : new Thickness(0);
                HorizontalAlignment = HorizontalAlignment.Right;
                var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                p.Children.Add(punto);
                p.Children.Add(texto);
                Child = p;
            }

            public void Poner(string t, string rol)
            {
                texto.Text = t;
                Tema.Texto(texto, rol);
                Tema.Enlazar(punto, Shape.FillProperty, rol);
                if (conFondo) Tema.Fondo(this, rol + "Fondo");
            }
        }
    }

    /// Ventanita para escribir el usuario y la contraseña del panel de Sunshine
    class DialogoCredenciales : Window
    {
        readonly TextBox usuario = new TextBox { Width = 240, Text = Sistema.Usuario };
        readonly TextBox clave = new TextBox { Width = 240, PasswordChar = '•' };
        readonly TextBlock error = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        readonly Button aceptar = new Button { Content = "Aceptar", MinWidth = 100, IsDefault = true };

        public bool Aceptado { get; private set; }
        public string Usuario => usuario.Text?.Trim() ?? "";
        public string Clave => clave.Text ?? "";

        public DialogoCredenciales()
        {
            Title = "Contraseña de Sunshine";
            Icon = Iconos.Ventana();
            Width = 440; SizeToContent = SizeToContent.Height; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            Tema.Fondo(this, "Superficie");

            var p = new StackPanel { Margin = new Thickness(24, 20), Spacing = 10 };
            p.Children.Add(new TextBlock { Text = "Es el usuario y la contraseña que elegiste para el panel de Sunshine al configurar RemotoFácil.", TextWrapping = TextWrapping.Wrap });
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 6, ColumnSpacing = 12 };
            var l1 = new TextBlock { Text = "Usuario:", VerticalAlignment = VerticalAlignment.Center };
            var l2 = new TextBlock { Text = "Contraseña:", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(usuario, 1); Grid.SetRow(l2, 1); Grid.SetRow(clave, 1); Grid.SetColumn(clave, 1);
            g.Children.Add(l1); g.Children.Add(usuario); g.Children.Add(l2); g.Children.Add(clave);
            p.Children.Add(g);
            p.Children.Add(error);
            var botones = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelar = new Button { Content = "Cancelar", MinWidth = 100, IsCancel = true };
            aceptar.Classes.Add("accent");
            botones.Children.Add(aceptar);
            botones.Children.Add(cancelar);
            p.Children.Add(botones);
            Content = p;

            cancelar.Click += (s, e) => Close();
            aceptar.Click += async (s, e) => await Comprobar();
            Opened += (s, e) => clave.Focus();
        }

        async Task Comprobar()
        {
            if (Usuario.Length == 0 || Clave.Length == 0) { Mostrar("Escribe el usuario y la contraseña.", "Error"); return; }
            aceptar.IsEnabled = false;
            Mostrar("Comprobando…", "Suave");
            try
            {
                string u = Usuario, c = Clave;
                if (await Task.Run(() => Sunshine.ComprobarCredenciales(u, c))) { Aceptado = true; Close(); return; }
                Mostrar("No son correctos. Si no los recuerdas, usa \"Repetir la configuración\".", "Error");
            }
            catch (Exception ex) { Mostrar("Sunshine no responde: " + ex.Message, "Error"); }
            finally { aceptar.IsEnabled = true; }
        }

        void Mostrar(string t, string rol)
        {
            error.Text = t;
            error.IsVisible = true;
            Tema.Texto(error, rol);
        }
    }
}
