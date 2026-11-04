using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace RemotoFacil
{
    static class Ui
    {
        public static void EnUi(Action a) => Dispatcher.UIThread.Post(a);

        /// Botón con aspecto de enlace
        public static Button Enlace(string texto, Action alPulsar, string color = "Enlace")
        {
            var b = new Button
            {
                Content = new TextBlock { Text = texto, TextDecorations = TextDecorations.Underline, TextWrapping = TextWrapping.Wrap },
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Cursor = new Cursor(StandardCursorType.Hand),
            };
            Tema.Texto(b, color);
            b.Click += (s, e) => alPulsar();
            return b;
        }

        public static string NombreEscritorio => Distro.Escritorio switch
        {
            Escritorio.KDE => "KDE Plasma",
            Escritorio.GNOME => "GNOME",
            Escritorio.Hyprland => "Hyprland",
            Escritorio.Sway => "Sway",
            Escritorio.X11 => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") is string d && d.Length > 0 ? d : "escritorio") + " (X11)",
            _ => Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "desconocido",
        };
    }

    // ======================================================================

    class PaginaBienvenida : Pagina
    {
        public override string Titulo => "Bienvenido a RemotoFácil";
        public override string Subtitulo => "Usa tu PC desde el móvil, la tablet, otro ordenador o la tele, estés donde estés";

        readonly Button desinstalar;

        public PaginaBienvenida()
        {
            Texto("Este asistente deja tu PC listo en unos minutos:", abajo: 6);
            Texto("•  Instala Tailscale, que crea una red privada solo para tus dispositivos. Así llegas al PC " +
                  "desde cualquier sitio, sin abrir puertos en el router.", abajo: 4);
            Texto("•  Instala Sunshine, que transmite la pantalla del PC con muy poco retraso.", abajo: 4);
            if (Pantallas.Motor != null)
                Texto("•  Opcionalmente, apaga las pantallas que elijas mientras estás conectado.", abajo: 4);
            Texto("•  Te ayuda a emparejar tus dispositivos con Moonlight.", abajo: 16);
            Texto("Qué necesitas", negrita: true, abajo: 6);
            Texto("•  Conexión a internet y la contraseña de administrador de este PC (se pide una sola vez).", abajo: 4);
            Texto("•  En el dispositivo desde el que te conectarás (móvil, tablet, portátil, tele con Android…), " +
                  "las apps gratuitas Tailscale y Moonlight.", abajo: 4);
            Texto("•  Una cuenta para Tailscale. Vale la de Google, Microsoft, Apple o GitHub.", abajo: 14);

            Suave($"Detectado: {Distro.Actual.Nombre} · {Ui.NombreEscritorio}", abajo: 6);
            if (Distro.Actual.Familia == Familia.Desconocida && !Sistema.Existe("flatpak"))
                Tema.Texto(Texto("No reconozco esta distribución y no tiene Flatpak: puede que la instalación de Sunshine no funcione.", abajo: 6), "Aviso");
            if (Distro.Actual.Familia == Familia.SteamOS)
                Tema.Texto(Texto("En SteamOS el usuario \"deck\" viene sin contraseña, y hace falta una para instalar. " +
                                 "Si no la has puesto, abre Konsole y escribe: passwd", abajo: 6), "Aviso");
            if (!Sistema.Existe("pkexec"))
                Tema.Texto(Texto("Falta pkexec (polkit): RemotoFácil te dirá qué comandos ejecutar tú en una terminal.", abajo: 6), "Aviso");

            desinstalar = Poner(Ui.Enlace("¿Ya lo tenías instalado y quieres quitarlo? Desinstalar RemotoFácil", () => _ = A.Desinstalar()), 0);
            desinstalar.Margin = new Thickness(0, 8, 0, 0);
        }

        public override void AlMostrar() =>
            desinstalar.IsVisible = File.Exists(Rutas.EjecutableInstalado) || File.Exists(Rutas.MarcaConfigurado);
    }

    // ======================================================================

    class PaginaInstalar : Pagina
    {
        public override string Titulo => "Instalando los programas";
        public override string Subtitulo => "Esto puede tardar unos minutos";
        public override bool PuedeSeguir => hecho;

        readonly TextBlock estado;
        readonly ProgressBar barra = new ProgressBar { IsIndeterminate = true, Height = 6 };
        readonly TextBox registro = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 210,
            FontFamily = new FontFamily("monospace"), FontSize = 12,
        };
        readonly Button reintentar;
        bool hecho, empezado;

        public PaginaInstalar()
        {
            Suave($"Tailscale: {(Tailscale.Instalado ? "ya está instalado" : "se instalará con " + Instalador.MetodoTailscale)}.   " +
                  $"Sunshine: {(Sunshine.Instalado ? "ya está instalado" : "se instalará con " + Instalador.MetodoSunshine)}.", abajo: 8);
            estado = Texto("Preparando…");
            Poner(barra);
            Poner(registro);
            reintentar = Boton("Reintentar");
            reintentar.IsVisible = false;
            reintentar.Click += async (s, e) => await Instalar();
        }

        void Log(string t) => Ui.EnUi(() =>
        {
            // Las líneas "### …" del script son los pasos: se muestran arriba
            if (t.StartsWith("### ")) estado.Text = t.Substring(4) + "…";
            registro.Text += t + "\n";
            registro.CaretIndex = registro.Text.Length;
            Registro.Escribir(t);
        });

        public override async void AlMostrar()
        {
            if (!empezado) { empezado = true; await Instalar(); }
        }

        async Task Instalar()
        {
            reintentar.IsVisible = false;
            barra.IsVisible = true;
            Ocupada = true; Avisar();
            Tema.Texto(estado, "Texto");
            var progreso = new Progress<string>(Log);
            try
            {
                estado.Text = "Copiando RemotoFácil…";
                await Task.Run(Integracion.Instalar);
                Log("RemotoFácil copiado en " + Rutas.CarpetaApp);

                estado.Text = "Descargando lo necesario…";
                var plan = await Instalador.Preparar(progreso, A.AlCerrar.Token);

                estado.Text = "Escribe tu contraseña de administrador en la ventana que se ha abierto…";
                int cod = await Sistema.ComoAdministrador(plan.Script.ToString(), Log);
                if (cod == 126 || cod == 127)
                    throw new Exception("No se dio la contraseña de administrador (o se canceló). Pulsa Reintentar.");
                if (cod != 0)
                    throw new Exception($"La instalación falló (código {cod}). Revisa el registro de arriba y pulsa Reintentar.");

                estado.Text = "Terminando…";
                await Task.Run(() => Instalador.DespuesDelAdministrador(plan, progreso));
                Estado(estado, "Todo instalado. Pulsa Siguiente.", "Exito");
                hecho = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Estado(estado, ex.Message.Length < 200 ? ex.Message : "Algo ha fallado. Revisa el registro y pulsa Reintentar.", "Error");
                Log("ERROR: " + ex.Message);
                reintentar.IsVisible = true;
            }
            finally { barra.IsVisible = false; Ocupada = false; Avisar(); }
        }
    }

    // ======================================================================

    class PaginaTailscale : Pagina
    {
        public override string Titulo => "Conecta el PC a Tailscale";
        public override string Subtitulo => "La red privada entre tu PC y tus dispositivos";
        public override bool PuedeSeguir => A?.Datos.Ip != null;

        readonly Button entrar, enlace;
        readonly TextBlock estado;
        readonly DispatcherTimer vigilar = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        string url;
        bool comprobando;

        public PaginaTailscale()
        {
            Texto("Pulsa el botón y se abrirá el navegador. Inicia sesión (por ejemplo, con tu cuenta de Google) " +
                  "y acepta conectar este equipo.");
            Texto("Importante: apunta qué cuenta usas. En los dispositivos desde los que te conectes tendrás que " +
                  "entrar en Tailscale con esa misma cuenta.", negrita: true, abajo: 16);
            entrar = Boton("Iniciar sesión en Tailscale", principal: true);
            entrar.Click += async (s, e) => await Entrar();
            estado = Texto("");
            estado.IsVisible = false;
            enlace = Poner(Ui.Enlace("¿No se ha abierto el navegador? Pulsa aquí.", () => { if (url != null) Sistema.AbrirUrl(url); }));
            enlace.IsVisible = false;

            // Si ya hay sesión (o se inicia desde otro sitio), se detecta solo
            vigilar.Tick += async (s, e) =>
            {
                if (comprobando || Ocupada || A?.Datos.Ip != null) return;
                comprobando = true;
                try
                {
                    var ip = await Task.Run(Tailscale.Ip);
                    if (ip != null && !Ocupada && A.Datos.Ip == null) Conectado(ip, "¡Conectado!");
                }
                finally { comprobando = false; }
            };
            AttachedToVisualTree += (s, e) => vigilar.Start();
            DetachedFromVisualTree += (s, e) => vigilar.Stop();
        }

        public override async void AlMostrar()
        {
            if (A.Datos.Ip != null) return;
            var ip = await Task.Run(Tailscale.Ip);
            if (ip != null) Conectado(ip, "Este PC ya está conectado a Tailscale.");
        }

        void Conectado(string ip, string mensaje)
        {
            vigilar.Stop();
            A.Datos.Ip = ip;
            Estado(estado, $"{mensaje}\nSu dirección en Tailscale es {ip}. Pulsa Siguiente.", "Exito");
            entrar.IsEnabled = false;
            enlace.IsVisible = false;
            Avisar();
        }

        async Task Entrar()
        {
            entrar.IsEnabled = false;
            Ocupada = true; Avisar();
            Estado(estado, "Conectando con Tailscale…", "Texto");
            try
            {
                var (ok, salida) = await Tailscale.IniciarSesion(u => Ui.EnUi(() =>
                {
                    url = u;
                    Estado(estado, "Esperando a que inicies sesión en el navegador…", "Texto");
                    enlace.IsVisible = true;
                    Sistema.AbrirUrl(u);
                }), A.AlCerrar.Token);
                var ip = ok ? await Task.Run(Tailscale.Ip) : null;
                if (ip != null) Conectado(ip, "¡Conectado!");
                else
                {
                    Estado(estado, "No se pudo conectar. Vuelve a intentarlo.\n" + salida, "Error");
                    entrar.IsEnabled = true;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Estado(estado, "Error: " + ex.Message, "Error");
                entrar.IsEnabled = true;
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
        readonly TextBox clave = new TextBox { Width = 260, PasswordChar = '•' };
        readonly TextBox repetir = new TextBox { Width = 260, PasswordChar = '•' };
        readonly CheckBox noSuspender = new CheckBox { Content = "No suspender el PC mientras esté enchufado (recomendado)", IsChecked = true };

        public PaginaSunshine()
        {
            Texto("Sunshine tiene un panel web protegido con usuario y contraseña. RemotoFácil lo usa para emparejar " +
                  "tus dispositivos, y tú lo necesitarás si quieres cambiar ajustes más adelante. Apúntalos en algún sitio.", abajo: 14);

            var tabla = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 6, ColumnSpacing = 12 };
            void Campo(int fila, string etiqueta, Control c)
            {
                var l = new TextBlock { Text = etiqueta, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(l, fila); Grid.SetRow(c, fila); Grid.SetColumn(c, 1);
                tabla.Children.Add(l); tabla.Children.Add(c);
            }
            Campo(0, "Usuario:", usuario);
            Campo(1, "Contraseña:", clave);
            Campo(2, "Repetir contraseña:", repetir);
            Poner(tabla, 4);

            var mostrar = Poner(new CheckBox { Content = "Mostrar contraseña" }, 14);
            mostrar.IsCheckedChanged += (s, e) => clave.PasswordChar = repetir.PasswordChar = mostrar.IsChecked == true ? '\0' : '•';
            if (Energia.Disponible)
            {
                Poner(noSuspender, 0);
                Suave("Si el PC se duerme, no podrás conectarte desde fuera.", abajo: 10);
            }
            Suave("En Linux, Sunshine solo funciona con tu sesión iniciada. Si quieres conectarte nada más encender el PC, " +
                  "activa el inicio de sesión automático en la configuración de usuarios del sistema.");
            usuario.Text = Sistema.Usuario;
        }

        public override async Task<bool> AlSiguiente()
        {
            var u = usuario.Text?.Trim() ?? "";
            var c = clave.Text ?? "";
            if (u.Length == 0) { await A.Error("Escribe un nombre de usuario."); return false; }
            if (c.Length < 6) { await A.Error("La contraseña debe tener al menos 6 caracteres."); return false; }
            if (c != repetir.Text) { await A.Error("Las contraseñas no coinciden."); return false; }
            if (u.Contains(':')) { await A.Error("El usuario no puede contener dos puntos (:)."); return false; }

            bool suspender = Energia.Disponible && noSuspender.IsChecked == true;
            await Task.Run(() =>
            {
                Sunshine.PonerCredenciales(u, c);
                Sunshine.GuardarCredenciales(u, c);
                if (suspender) Energia.NoSuspender();
                // Sunshine solo lee la contraseña al arrancar
                Sunshine.Reiniciar();
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
        public override string TextoSiguiente => disponible ? "Aplicar" : TextoPorDefecto;

        readonly RadioButton no = new RadioButton { Content = "No, dejar las pantallas como están", IsChecked = true, GroupName = "pantallas" };
        readonly RadioButton si = new RadioButton { Content = "Sí, apagar las pantallas que marque mientras estoy conectado", GroupName = "pantallas" };
        readonly StackPanel lista = new StackPanel { Spacing = 2 };
        readonly TextBlock aviso, sinSoporte;
        readonly Control[] controles;
        List<Monitor> monitores = new();
        bool disponible;

        public PaginaPantallas()
        {
            sinSoporte = Texto("");
            Texto("Mientras te conectas desde otro dispositivo, las pantallas que marques se apagarán, y se volverán a " +
                  "encender al terminar. Así ves justo la pantalla que quieres (por ejemplo, el monitor y no la tele).", abajo: 6);
            var opciones = Poner(new StackPanel { Spacing = 2 }, 6);
            opciones.Children.Add(no);
            opciones.Children.Add(si);
            no.IsCheckedChanged += (s, e) => ActualizarHabilitado();
            var marca = Texto("Marca las pantallas que quieres APAGAR (al menos una tiene que quedarse encendida: esa es la que verás):", abajo: 4);
            var marco = Poner(Tema.Enlazar(new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6), Child = lista },
                                         Border.BorderBrushProperty, "Borde"), 6);
            var recargar = Boton("Volver a buscar");
            recargar.Click += (s, e) => Cargar();
            aviso = Suave("", abajo: 0);
            controles = new Control[] { opciones, marca, marco, recargar, aviso };
        }

        public override void AlMostrar() => Cargar();

        void Cargar()
        {
            var marcadas = lista.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => ((Monitor)c.Tag).Id).ToHashSet();
            if (marcadas.Count == 0) marcadas = Pantallas.Elegidas();
            string error = null;
            try { monitores = Pantallas.Listar(); }
            catch (Exception ex) { monitores = new(); error = ex.Message; }

            disponible = Pantallas.Motor != null && error == null;
            foreach (var c in controles) c.IsVisible = disponible;
            sinSoporte.IsVisible = !disponible;
            sinSoporte.Text = error != null
                ? "No se pudieron leer las pantallas, así que este paso se salta: " + error
                : $"En este escritorio ({Ui.NombreEscritorio}) RemotoFácil no sabe apagar pantallas, así que este paso se salta. Pulsa Siguiente.";

            lista.Children.Clear();
            for (int i = 0; i < monitores.Count; i++)
            {
                var m = monitores[i];
                lista.Children.Add(new CheckBox
                {
                    Content = $"{i + 1}.  {m.Nombre}  —  {m.Ancho}×{m.Alto}" + (m.Principal ? "  (principal)" : ""),
                    IsChecked = marcadas.Contains(m.Id), Tag = m,
                });
            }
            if (marcadas.Any(id => monitores.Any(m => m.Id == id))) si.IsChecked = true;
            aviso.Text = monitores.Count < 2
                ? "Solo se ha detectado una pantalla encendida, así que este paso no hace falta. Si tienes más, enciéndelas y pulsa \"Volver a buscar\"."
                : "Las pantallas se vuelven a encender cuando cierras la sesión en Moonlight (mantén pulsado el PC → \"Quit app\"). " +
                  "Si alguna se queda apagada, usa \"Restaurar pantallas\" en RemotoFácil.";
            ActualizarHabilitado();
            Avisar();
        }

        void ActualizarHabilitado()
        {
            si.IsEnabled = monitores.Count >= 2;
            if (!si.IsEnabled) no.IsChecked = true;
            lista.IsEnabled = si.IsChecked == true;
        }

        public override async Task<bool> AlSiguiente()
        {
            if (!disponible) return true;
            var apagar = si.IsChecked == true ? lista.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (Monitor)c.Tag).ToList() : new List<Monitor>();
            if (si.IsChecked == true && apagar.Count == 0) { await A.Error("Marca al menos una pantalla para apagar, o elige \"No\"."); return false; }
            if (apagar.Count >= monitores.Count) { await A.Error("Tiene que quedar al menos una pantalla encendida para poder verla."); return false; }

            bool activar = apagar.Count > 0;
            await Task.Run(() =>
            {
                Pantallas.GuardarElegidas(apagar);
                Sunshine.ConfigurarPantallas(activar);
                Sunshine.Reiniciar();
            });
            A.Datos.ApagarPantallas = activar;
            return true;
        }
    }

    // ======================================================================

    class PaginaEmparejar : Pagina
    {
        public override string Titulo => "Empareja un dispositivo";
        public override string Subtitulo => "Solo hay que hacerlo una vez por dispositivo";

        readonly TextBlock ip, solicitud, estado, masTarde;
        readonly TextBox pin = new TextBox { Width = 90, MaxLength = 4, FontSize = 20, HorizontalContentAlignment = HorizontalAlignment.Center };
        readonly TextBox nombre = new TextBox { Width = 200, Text = NombrePorDefecto };
        readonly Button emparejar = new Button { Content = "Emparejar" };
        readonly DispatcherTimer vigilar = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        const string NombrePorDefecto = "Mi dispositivo";
        string idPendiente, idAnunciado;
        bool nombreEditado, consultando;

        public PaginaEmparejar()
        {
            Texto("1.  En el dispositivo, abre Tailscale, inicia sesión con la misma cuenta que en el PC y actívalo.", abajo: 6);
            Texto("2.  Abre Moonlight. Si el PC no aparece solo, pulsa el botón + y escribe esta dirección:", abajo: 4);
            var filaIp = Fila(8);
            filaIp.Margin = new Thickness(24, 0, 0, 8);
            ip = Tema.Texto(new TextBlock { FontFamily = new FontFamily("monospace"), FontSize = 20, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }, "Destacado");
            var copiar = new Button { Content = "Copiar" };
            copiar.Click += async (s, e) => { if (A.Datos.Ip != null) await TopLevel.GetTopLevel(this).Clipboard.SetTextAsync(A.Datos.Ip); };
            filaIp.Children.Add(ip);
            filaIp.Children.Add(copiar);
            Texto("3.  Toca el PC en Moonlight. Te mostrará un PIN de 4 cifras: escríbelo aquí.", abajo: 8);

            var filaPin = Fila(6);
            filaPin.Margin = new Thickness(24, 0, 0, 6);
            filaPin.Children.Add(new TextBlock { Text = "PIN:", VerticalAlignment = VerticalAlignment.Center });
            filaPin.Children.Add(pin);
            filaPin.Children.Add(new TextBlock { Text = "Nombre:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            filaPin.Children.Add(nombre);
            emparejar.Classes.Add("accent");
            emparejar.VerticalAlignment = VerticalAlignment.Center;
            filaPin.Children.Add(emparejar);
            emparejar.Click += async (s, e) => await Emparejar();
            pin.TextChanging += (s, e) => { var t = new string((pin.Text ?? "").Where(char.IsDigit).ToArray()); if (t != pin.Text) pin.Text = t; };
            nombre.KeyDown += (s, e) => nombreEditado = true;

            solicitud = Texto("", abajo: 6);
            solicitud.Margin = new Thickness(24, 0, 0, 6);
            estado = Texto("");
            masTarde = Suave("También puedes emparejar más tarde desde RemotoFácil (\"Agregar dispositivo\").");

            vigilar.Tick += async (s, e) => await BuscarSolicitud();
            AttachedToVisualTree += (s, e) => vigilar.Start();
            DetachedFromVisualTree += (s, e) => vigilar.Stop();
        }

        public override void AlMostrar()
        {
            ip.Text = A.Datos.Ip ?? "(sin dirección)";
            masTarde.IsVisible = !A.Suelto;
            _ = BuscarSolicitud();
        }

        /// Avisa en cuanto Moonlight pide emparejarse
        async Task BuscarSolicitud()
        {
            if (consultando || Ocupada || A?.Datos.Usuario == null) return;
            consultando = true;
            try
            {
                string u = A.Datos.Usuario, c = A.Datos.Clave;
                var ultima = (await Task.Run(() => Sunshine.SolicitudesPendientes(u, c))).LastOrDefault();
                idPendiente = ultima?.Id;
                if (ultima == null) Estado(solicitud, "Esperando a que toques el PC en Moonlight…", "Suave");
                else
                {
                    var quien = string.IsNullOrWhiteSpace(ultima.Nombre) ? "Un dispositivo" : $"\"{ultima.Nombre}\"";
                    var desde = string.IsNullOrWhiteSpace(ultima.Direccion) ? "" : $" ({ultima.Direccion})";
                    Estado(solicitud, $"{quien}{desde} está esperando: escribe el PIN que te muestra.", "Exito");
                    if (ultima.Id != idAnunciado)
                    {
                        idAnunciado = ultima.Id;
                        if (!nombreEditado && !string.IsNullOrWhiteSpace(ultima.Nombre)) nombre.Text = ultima.Nombre;
                        pin.Focus();
                    }
                }
            }
            catch { solicitud.IsVisible = false; }
            finally { consultando = false; }
        }

        async Task Emparejar()
        {
            if ((pin.Text ?? "").Length != 4) { await A.Error("El PIN tiene 4 cifras."); return; }
            if (A.Datos.Usuario == null) { await A.Error("Falta la contraseña de Sunshine."); return; }
            var n = string.IsNullOrWhiteSpace(nombre.Text) ? NombrePorDefecto : nombre.Text.Trim();
            emparejar.IsEnabled = false;
            Ocupada = true; Avisar();
            Estado(estado, "Emparejando… (puede tardar unos segundos)", "Texto");
            try
            {
                string p = pin.Text, u = A.Datos.Usuario, c = A.Datos.Clave, id = idPendiente;
                if (await Task.Run(() => Sunshine.Emparejar(u, c, p, n, id)))
                {
                    Estado(estado, $"¡\"{n}\" emparejado! Ya puedes tocar \"Desktop\" en Moonlight para ver el PC. " +
                                   "Si tienes otro dispositivo, repite los pasos con él.", "Exito");
                    pin.Text = "";
                    nombre.Text = NombrePorDefecto;
                    nombreEditado = false;
                }
                else Estado(estado, "El PIN no es correcto o ha caducado. Vuelve a tocar el PC en Moonlight y escribe el PIN nuevo.", "Error");
            }
            catch (Exception ex) { Estado(estado, ex.Message, "Error"); }
            finally { emparejar.IsEnabled = true; Ocupada = false; Avisar(); }
            _ = BuscarSolicitud();
        }
    }

    // ======================================================================

    class PaginaFin : Pagina
    {
        public override string Titulo => "¡Listo!";
        public override string Subtitulo => "Tu PC ya está preparado";

        readonly TextBlock resumen, pantallas;
        readonly CheckBox escritorio = new CheckBox { Content = "Crear un acceso directo a RemotoFácil en el escritorio", IsChecked = true };

        public PaginaFin()
        {
            Texto("Para conectarte desde cualquier sitio", negrita: true, abajo: 4);
            resumen = Texto("", abajo: 14);
            Texto("Consejos", negrita: true, abajo: 4);
            Texto("•  Deja el PC encendido y con tu sesión iniciada (puede estar bloqueada). Si está apagado, no podrás conectarte.", abajo: 4);
            Texto("•  Si puedes, conecta el PC al router por cable: la imagen irá más fluida.", abajo: 4);
            pantallas = Texto("", abajo: 4);
            Texto("•  Cuando vuelvas a abrir RemotoFácil (desde el escritorio o el menú de aplicaciones) verás su centro de control: " +
                  "tu dirección, los dispositivos emparejados, y accesos a Sunshine y Tailscale.", abajo: 14);
            Poner(escritorio, 0);
            // La primera vez se propone; al repetir la configuración, refleja si ya existe
            if (Integracion.YaConfigurado) escritorio.IsChecked = File.Exists(Integracion.AccesoEscritorio);
        }

        public override void AlMostrar()
        {
            try { Integracion.MarcarConfigurado(); }
            catch (Exception ex) { Registro.Escribir("No se pudo marcar como configurado: " + ex.Message); }
            resumen.Text = "1.  Activa Tailscale en el dispositivo.\n" +
                           "2.  Abre Moonlight y toca tu PC" + (A.Datos.Ip != null ? $" ({A.Datos.Ip})" : "") + ".\n" +
                           "3.  Elige \"Desktop\" para ver el escritorio, o \"Steam Big Picture\" para jugar.";
            pantallas.Text = A.Datos.ApagarPantallas
                ? "•  Para terminar, en Moonlight mantén pulsado el PC → \"Quit app\". Así se vuelven a encender las pantallas."
                : "•  Para terminar, en Moonlight mantén pulsado el PC → \"Quit app\".";
        }

        public override async Task<bool> AlSiguiente()
        {
            bool crear = escritorio.IsChecked == true;
            try { await Task.Run(() => Integracion.AccesoEnEscritorio(crear)); }
            catch (Exception ex) { Registro.Escribir("No se pudo cambiar el acceso del escritorio: " + ex.Message); }
            return true;
        }
    }
}
