using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RemotoFacil
{
    enum ModoTema { Automatico, Claro, Oscuro }

    /// Papel de un texto: el color concreto depende del tema
    enum Rol { Texto, Suave, Enlace, Exito, Error, Aviso, Destacado }

    /// Papel de un fondo
    enum RolFondo { Fondo, Superficie, Pie }

    enum TipoBoton { Primario, Secundario, Cabecera }

    /// <summary>
    /// Modo claro u oscuro. Por defecto sigue al de Windows ("modo de las aplicaciones"), y la persona
    /// puede fijar uno desde el botón "Tema". Los controles guardan en Tag qué papel cumplen
    /// (Tema.Pintar, Tema.Fondo, Tema.Boton) y Tema.Aplicar los colorea según el tema actual.
    /// </summary>
    static class Tema
    {
        /// Se lanza al cambiar el tema (por la persona o porque Windows cambió y estamos en automático)
        public static event Action Cambio;

        static ModoTema preferencia;
        public static bool Oscuro { get; private set; }

        static string Archivo => Path.Combine(Rutas.CarpetaUsuario, "tema.txt");

        static Tema()
        {
            try
            {
                if (File.Exists(Archivo) && Enum.TryParse(File.ReadAllText(Archivo).Trim(), out ModoTema m)) preferencia = m;
            }
            catch { }
            Oscuro = Calcular();
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (preferencia == ModoTema.Automatico && e.Category == UserPreferenceCategory.General) Recalcular();
            };
        }

        public static ModoTema Preferencia
        {
            get => preferencia;
            set
            {
                preferencia = value;
                try
                {
                    Directory.CreateDirectory(Rutas.CarpetaUsuario);
                    File.WriteAllText(Archivo, value.ToString());
                }
                catch { }
                Oscuro = Calcular();
                Cambio?.Invoke();   // siempre: aunque el aspecto no cambie, el botón tiene que mostrar la nueva elección
            }
        }

        static void Recalcular()
        {
            bool antes = Oscuro;
            Oscuro = Calcular();
            if (antes != Oscuro) Cambio?.Invoke();
        }

        static bool Calcular() =>
            preferencia == ModoTema.Oscuro || (preferencia == ModoTema.Automatico && WindowsOscuro());

        static bool WindowsOscuro()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        // ================== Paleta ==================

        static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);
        static Color Elegir(Color claro, Color oscuro) => Oscuro ? oscuro : claro;

        public static Color FondoVentana => Elegir(C(243, 245, 249), C(24, 26, 31));
        public static Color Superficie => Elegir(Color.White, C(34, 37, 43));
        public static Color Pie => Elegir(C(243, 243, 243), C(28, 30, 35));
        public static Color Borde => Elegir(C(222, 227, 234), C(54, 58, 66));
        public static Color Separador => Elegir(C(236, 239, 243), C(46, 50, 57));
        public static Color Entrada => Elegir(Color.White, C(43, 46, 53));
        public static Color Texto => Elegir(C(28, 33, 40), C(232, 234, 238));

        public static Color Acento => C(0, 103, 192);
        public static Color AcentoEncima => Elegir(C(0, 90, 170), C(20, 122, 214));
        public static Color CabeceraIzquierda => Elegir(C(0, 96, 184), C(12, 58, 108));
        public static Color CabeceraDerecha => Elegir(C(24, 132, 226), C(20, 88, 156));
        public static Color CabeceraSubtitulo => Elegir(C(220, 235, 250), C(190, 210, 232));
        public static Color AvatarFondo => Elegir(C(225, 238, 252), C(40, 62, 92));

        public static Color De(Rol r)
        {
            switch (r)
            {
                case Rol.Suave: return Elegir(C(96, 104, 116), C(160, 167, 178));
                case Rol.Enlace: return Elegir(C(0, 103, 192), C(110, 175, 255));
                case Rol.Destacado: return Elegir(C(0, 103, 192), C(110, 175, 255));
                case Rol.Exito: return Elegir(C(22, 128, 60), C(96, 204, 136));
                case Rol.Error: return Elegir(C(190, 40, 40), C(255, 122, 122));
                case Rol.Aviso: return Elegir(C(150, 95, 0), C(240, 190, 90));
                default: return Texto;
            }
        }

        /// Fondo suave a juego con un color de estado (para las "pastillas")
        public static Color FondoDe(Rol r)
        {
            switch (r)
            {
                case Rol.Exito: return Elegir(C(225, 245, 232), C(30, 60, 42));
                case Rol.Error: return Elegir(C(252, 231, 231), C(72, 34, 36));
                case Rol.Aviso: return Elegir(C(253, 242, 214), C(66, 52, 24));
                default: return Superficie;
            }
        }

        public static Color De(RolFondo f) => f == RolFondo.Fondo ? FondoVentana : f == RolFondo.Pie ? Pie : Superficie;

        // ================== Papeles de cada control ==================

        class Estilo
        {
            public Rol? Texto;
            public RolFondo? Fondo;
            public TipoBoton? Boton;
            public bool ScrollEnganchado;
        }

        static Estilo E(Control c)
        {
            if (!(c.Tag is Estilo e)) c.Tag = e = new Estilo();
            return e;
        }

        public static T Pintar<T>(T c, Rol r) where T : Control
        {
            E(c).Texto = r;
            AplicarUno(c);
            return c;
        }

        public static T Fondo<T>(T c, RolFondo f) where T : Control
        {
            E(c).Fondo = f;
            AplicarUno(c);
            return c;
        }

        public static T Boton<T>(T b, TipoBoton tipo) where T : ButtonBase
        {
            E(b).Boton = tipo;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            if (tipo != TipoBoton.Secundario || b.Font.Bold || b.Font.Name.EndsWith("Semibold"))
                b.Font = new Font("Segoe UI Semibold", b.Font.Size);
            AplicarUno(b);
            return b;
        }

        /// Colorea un control y todo lo que contiene según el tema actual
        public static void Aplicar(Control raiz)
        {
            // Lo que no tiene papel propio hereda el color de texto de la ventana
            if (raiz is Form) raiz.ForeColor = Texto;
            AplicarUno(raiz);
            foreach (Control c in raiz.Controls) Aplicar(c);
            raiz.Invalidate();
            if (raiz is Form f) BarraTitulo(f);
        }

        static void AplicarUno(Control c)
        {
            var e = c.Tag as Estilo;
            if (e?.Fondo != null) c.BackColor = De(e.Fondo.Value);

            if (e?.Boton != null && c is ButtonBase b)
            {
                Color fondo, texto, encima;
                switch (e.Boton.Value)
                {
                    case TipoBoton.Primario:
                        fondo = Acento; texto = Color.White; encima = AcentoEncima; break;
                    case TipoBoton.Cabecera:
                        fondo = Elegir(C(28, 122, 212), C(30, 78, 132)); texto = Color.White; encima = Elegir(C(52, 140, 226), C(44, 96, 156)); break;
                    default:
                        fondo = Elegir(C(234, 239, 246), C(48, 53, 62)); texto = Elegir(C(0, 80, 160), C(150, 200, 255)); encima = Elegir(C(218, 227, 240), C(60, 66, 77)); break;
                }
                b.BackColor = fondo;
                b.ForeColor = texto;
                b.FlatAppearance.BorderColor = fondo;   // sin el recuadro del botón predeterminado
                b.FlatAppearance.MouseOverBackColor = encima;
                b.FlatAppearance.MouseDownBackColor = encima;
            }
            else if (c is LinkLabel l)
            {
                l.LinkColor = De(e?.Texto ?? Rol.Enlace);
                l.ActiveLinkColor = l.VisitedLinkColor = l.LinkColor;
                l.ForeColor = Texto;
            }
            else if (e?.Texto != null) c.ForeColor = De(e.Texto.Value);
            else if (c is TextBoxBase || c is ListBox)
            {
                c.BackColor = Entrada;
                c.ForeColor = Texto;
                // El borde 3D clásico desentona sobre fondo oscuro; el simple queda bien en los dos temas
                if (c is TextBox t && t.BorderStyle == BorderStyle.Fixed3D) t.BorderStyle = BorderStyle.FixedSingle;
                if (c is ListBox lb && lb.BorderStyle == BorderStyle.Fixed3D) lb.BorderStyle = BorderStyle.FixedSingle;
            }

            // Barras de desplazamiento oscuras donde las hay
            if (c is TextBoxBase || c is ListBox || (c is ScrollableControl sc && sc.AutoScroll))
            {
                var est = E(c);
                if (c.IsHandleCreated) TemaVentana(c);
                if (!est.ScrollEnganchado)
                {
                    est.ScrollEnganchado = true;
                    c.HandleCreated += (s, a) => TemaVentana((Control)s);
                }
            }
        }

        // ================== Ventanas ==================

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int atributo, ref int valor, int tam);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr despuesDe, int x, int y, int w, int h, uint flags);

        static void TemaVentana(Control c)
        {
            try { SetWindowTheme(c.Handle, Oscuro ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        /// Barra de título oscura (Windows 10 1809 en adelante; en versiones anteriores no hace nada)
        public static void BarraTitulo(Form f)
        {
            if (!f.IsHandleCreated) return;
            try
            {
                int v = Oscuro ? 1 : 0;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE; 19 en Windows 10 anteriores a 20H1
                if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
                // Fuerza a redibujar el marco para que el cambio se vea sin tener que reactivar la ventana
                SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020);
            }
            catch { }
        }

        /// Deja una ventana atada al tema: la colorea ahora, al crear su ventana, y cada vez que cambie el tema
        public static void Seguir(Form f, Action extra = null)
        {
            void Todo() { extra?.Invoke(); Aplicar(f); }
            Action alCambiar = () =>
            {
                if (f.IsDisposed) return;
                if (f.InvokeRequired) f.BeginInvoke((Action)Todo); else Todo();
            };
            Cambio += alCambiar;
            f.FormClosed += (s, e) => Cambio -= alCambiar;
            f.HandleCreated += (s, e) => BarraTitulo(f);
            Todo();
        }

        // ================== Botón "Tema" ==================

        static string Nombre(ModoTema m) =>
            m == ModoTema.Claro ? "Claro" : m == ModoTema.Oscuro ? "Oscuro" : "Automático";

        /// Botón para la cabecera con un menú: Automático (como Windows) / Claro / Oscuro
        public static Button CrearBoton()
        {
            var b = Boton(new Button { Text = "Tema: " + Nombre(Preferencia), AutoSize = true, Height = 32, Padding = new Padding(8, 0, 8, 0) }, TipoBoton.Cabecera);
            b.Click += (s, e) =>
            {
                var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new ColoresMenu()), ShowImageMargin = false, ShowCheckMargin = true };
                menu.Font = new Font("Segoe UI", 9.5F);
                foreach (var m in new[] { ModoTema.Automatico, ModoTema.Claro, ModoTema.Oscuro })
                {
                    var texto = m == ModoTema.Automatico ? "Automático (como Windows)" : Nombre(m);
                    var item = new ToolStripMenuItem(texto) { Checked = Preferencia == m, ForeColor = Texto };
                    var modo = m;
                    item.Click += (s2, e2) => Preferencia = modo;
                    menu.Items.Add(item);
                }
                menu.Closed += (s2, e2) => b.BeginInvoke((Action)menu.Dispose);
                menu.Show(b, new Point(0, b.Height));
            };
            Action alCambiar = null;
            alCambiar = () =>
            {
                if (b.IsDisposed) { Cambio -= alCambiar; return; }
                if (b.InvokeRequired) b.BeginInvoke((Action)(() => b.Text = "Tema: " + Nombre(Preferencia)));
                else b.Text = "Tema: " + Nombre(Preferencia);
            };
            Cambio += alCambiar;
            return b;
        }

        class ColoresMenu : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Superficie;
            public override Color MenuBorder => Borde;
            public override Color MenuItemBorder => Acento;
            public override Color MenuItemSelected => Elegir(C(229, 240, 252), C(44, 62, 86));
            public override Color ImageMarginGradientBegin => Superficie;
            public override Color ImageMarginGradientMiddle => Superficie;
            public override Color ImageMarginGradientEnd => Superficie;
            public override Color CheckBackground => Elegir(C(204, 228, 250), C(30, 80, 140));
            public override Color CheckSelectedBackground => CheckBackground;
            public override Color CheckPressedBackground => CheckBackground;
        }
    }
}
