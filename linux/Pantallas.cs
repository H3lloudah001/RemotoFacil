using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace RemotoFacil
{
    class Monitor
    {
        public string Id;       // conector (DP-1, HDMI-A-1…): identifica la pantalla aunque cambie el orden
        public string Nombre;
        public int Ancho, Alto, X, Y;
        public bool Principal;
    }

    /// Cada escritorio tiene su forma de encender y apagar pantallas
    interface IMotorPantallas
    {
        string Nombre { get; }
        List<Monitor> Listar();
        /// Foto del estado actual, para poder volver a él
        JsonNode Capturar();
        void Apagar(HashSet<string> ids, JsonNode estado);
        void Restaurar(JsonNode estado);
        /// Encender todo lo conectado (cuando no hay nada guardado)
        void EncenderTodo();
    }

    /// <summary>
    /// Apaga y vuelve a encender pantallas. Lo llama Sunshine al empezar y terminar cada sesión
    /// (con --apagar-pantallas / --restaurar-pantallas). Es el equivalente del script modo-cine.
    /// </summary>
    static class Pantallas
    {
        static IMotorPantallas motor;
        static bool buscado;

        /// El motor para este escritorio, o null si aquí no se puede (la opción se oculta)
        public static IMotorPantallas Motor
        {
            get
            {
                if (buscado) return motor;
                buscado = true;
                try
                {
                    motor = Distro.Escritorio switch
                    {
                        Escritorio.KDE when Sistema.Existe("kscreen-doctor") => new MotorKde(),
                        Escritorio.GNOME when MotorGnome.Disponible() => new MotorGnome(),
                        Escritorio.Hyprland when Sistema.Existe("hyprctl") => new MotorHyprland(),
                        Escritorio.Sway when Sistema.Existe("swaymsg") => new MotorSway(),
                        _ when Environment.GetEnvironmentVariable("DISPLAY") != null && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") == null
                               && Sistema.Existe("xrandr") => new MotorXrandr(),
                        _ => null,
                    };
                }
                catch (Exception ex) { Registro.Escribir("No se pudo preparar el control de pantallas: " + ex.Message); }
                return motor;
            }
        }

        public static List<Monitor> Listar() => Motor?.Listar() ?? new List<Monitor>();

        public static HashSet<string> Elegidas() => File.Exists(Rutas.PantallasElegidas)
            ? new HashSet<string>(File.ReadAllLines(Rutas.PantallasElegidas).Select(l => l.Split('\t')[0].Trim()).Where(l => l.Length > 0))
            : new HashSet<string>();

        public static void GuardarElegidas(IEnumerable<Monitor> apagar)
        {
            var l = apagar.ToList();
            if (l.Count == 0) { if (File.Exists(Rutas.PantallasElegidas)) File.Delete(Rutas.PantallasElegidas); return; }
            Directory.CreateDirectory(Rutas.Config);
            File.WriteAllLines(Rutas.PantallasElegidas, l.Select(m => m.Id + "\t" + m.Nombre));
        }

        public static void ApagarSegunConfiguracion()
        {
            var m = Motor ?? throw new Exception("Este escritorio no permite apagar pantallas desde RemotoFácil.");
            var ids = Elegidas();
            if (ids.Count == 0) { Registro.Escribir("No hay pantallas configuradas para apagar."); return; }
            var encendidas = m.Listar();
            var apagar = new HashSet<string>(encendidas.Where(x => ids.Contains(x.Id)).Select(x => x.Id));
            if (apagar.Count == 0) { Registro.Escribir("Las pantallas elegidas ya están apagadas o no están conectadas."); return; }
            if (apagar.Count >= encendidas.Count) { Registro.Escribir("No se apaga nada: se quedarían todas las pantallas apagadas."); return; }

            // Si quedó un estado de una sesión que no se cerró bien, se conserva (es el bueno)
            var estado = File.Exists(Rutas.EstadoPantallas) ? JsonNode.Parse(File.ReadAllText(Rutas.EstadoPantallas)) : null;
            if (estado == null)
            {
                estado = new JsonObject { ["motor"] = m.Nombre, ["datos"] = m.Capturar() };
                Directory.CreateDirectory(Rutas.Estado);
                File.WriteAllText(Rutas.EstadoPantallas, estado.ToJsonString());
            }
            m.Apagar(apagar, m.Capturar());
            Registro.Escribir($"Pantallas apagadas: {string.Join(", ", apagar)}");
        }

        public static void Restaurar(bool forzar)
        {
            var m = Motor ?? throw new Exception("Este escritorio no permite controlar pantallas desde RemotoFácil.");
            if (File.Exists(Rutas.EstadoPantallas))
            {
                try
                {
                    var estado = JsonNode.Parse(File.ReadAllText(Rutas.EstadoPantallas));
                    if ((string)estado["motor"] == m.Nombre)
                    {
                        m.Restaurar(estado["datos"]);
                        File.Delete(Rutas.EstadoPantallas);
                        Registro.Escribir("Pantallas restauradas.");
                        return;
                    }
                }
                catch (Exception e) { Registro.Escribir("No se pudo aplicar el estado guardado: " + e.Message + ". Se encienden todas."); }
            }
            else if (!forzar) { Registro.Escribir("No había nada que restaurar."); return; }

            m.EncenderTodo();
            try { File.Delete(Rutas.EstadoPantallas); } catch { }
            Registro.Escribir("Pantallas encendidas (todas).");
        }

        internal static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        internal static string Txt(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        internal static void Comprobar((int codigo, string salida) r, string que)
        {
            if (r.codigo != 0) throw new Exception($"{que} falló: {r.salida}");
        }
    }

    // ======================================================================
    // KDE Plasma: kscreen-doctor (Wayland y X11)
    // ======================================================================

    class MotorKde : IMotorPantallas
    {
        public string Nombre => "kde";

        static JsonNode Leer()
        {
            var (cod, salida) = Sistema.Ejecutar("kscreen-doctor", new[] { "-j" }, 15000);
            int i = salida.IndexOf('{');
            if (i < 0) throw new Exception("kscreen-doctor no devolvió nada: " + salida);
            return JsonNode.Parse(salida.Substring(i));
        }

        static IEnumerable<JsonNode> Salidas(JsonNode d) => d["outputs"]?.AsArray().Where(o => o != null) ?? Enumerable.Empty<JsonNode>();

        public List<Monitor> Listar() => Salidas(Leer())
            .Where(o => (bool?)o["enabled"] == true && (bool?)o["connected"] == true)
            .Select(o =>
            {
                var modo = o["modes"]?.AsArray().FirstOrDefault(md => (string)md?["id"] == (string)o["currentModeId"]);
                return new Monitor
                {
                    Id = (string)o["name"],
                    Nombre = $"{(string)o["name"]}",
                    Ancho = (int?)modo?["size"]?["width"] ?? 0,
                    Alto = (int?)modo?["size"]?["height"] ?? 0,
                    X = (int?)o["pos"]?["x"] ?? 0,
                    Y = (int?)o["pos"]?["y"] ?? 0,
                    Principal = (int?)o["priority"] == 1,
                };
            }).OrderBy(m => m.X).ThenBy(m => m.Y).ToList();

        public JsonNode Capturar() => Leer();

        public void Apagar(HashSet<string> ids, JsonNode estado)
        {
            var args = new List<string>();
            var quedan = Salidas(estado).Where(o => (bool?)o["enabled"] == true && !ids.Contains((string)o["name"])).ToList();
            foreach (var id in ids) args.Add($"output.{id}.disable");
            // Que una de las que quedan sea la principal (la que verá Moonlight)
            var principal = quedan.FirstOrDefault(o => (int?)o["priority"] == 1) ?? quedan.First();
            args.Add($"output.{(string)principal["name"]}.priority.1");
            Pantallas.Comprobar(Sistema.Ejecutar("kscreen-doctor", args, 30000), "kscreen-doctor");
        }

        public void Restaurar(JsonNode estado)
        {
            var args = new List<string>();
            foreach (var o in Salidas(estado).Where(o => (bool?)o["enabled"] == true && (bool?)o["connected"] == true))
            {
                var n = (string)o["name"];
                args.Add($"output.{n}.enable");
                if ((string)o["currentModeId"] is string modo && modo.Length > 0) args.Add($"output.{n}.mode.{modo}");
                if (o["scale"] is JsonNode esc) args.Add($"output.{n}.scale.{Pantallas.Txt((double)esc)}");
                args.Add($"output.{n}.position.{(int?)o["pos"]?["x"] ?? 0},{(int?)o["pos"]?["y"] ?? 0}");
                if ((int?)o["priority"] is int p && p > 0) args.Add($"output.{n}.priority.{p}");
            }
            if (args.Count > 0) Pantallas.Comprobar(Sistema.Ejecutar("kscreen-doctor", args, 30000), "kscreen-doctor");
        }

        public void EncenderTodo()
        {
            var args = Salidas(Leer()).Where(o => (bool?)o["connected"] == true).Select(o => $"output.{(string)o["name"]}.enable").ToList();
            if (args.Count > 0) Pantallas.Comprobar(Sistema.Ejecutar("kscreen-doctor", args, 30000), "kscreen-doctor");
        }
    }

    // ======================================================================
    // X11 genérico: xrandr (Cinnamon, XFCE, MATE, KDE o GNOME en X11…)
    // ======================================================================

    class MotorXrandr : IMotorPantallas
    {
        public string Nombre => "xrandr";

        class Salida { public string Id, Rotacion = "normal"; public bool Conectada, Principal, Activa; public int W, H, X, Y; public string Hz; }

        static List<Salida> Leer()
        {
            var (cod, texto) = Sistema.Ejecutar("xrandr", new[] { "--query" }, 15000);
            if (cod != 0) throw new Exception("xrandr falló: " + texto);
            var lista = new List<Salida>();
            Salida actual = null;
            foreach (var l in texto.Split('\n'))
            {
                var m = Regex.Match(l, @"^(\S+) (connected|disconnected)( primary)?(?: (\d+)x(\d+)\+(-?\d+)\+(-?\d+))?(?: (normal|left|inverted|right))?");
                if (m.Success)
                {
                    actual = new Salida { Id = m.Groups[1].Value, Conectada = m.Groups[2].Value == "connected", Principal = m.Groups[3].Success, Activa = m.Groups[4].Success };
                    if (actual.Activa)
                    {
                        actual.W = int.Parse(m.Groups[4].Value); actual.H = int.Parse(m.Groups[5].Value);
                        actual.X = int.Parse(m.Groups[6].Value); actual.Y = int.Parse(m.Groups[7].Value);
                        if (m.Groups[8].Success) actual.Rotacion = m.Groups[8].Value;
                    }
                    lista.Add(actual);
                    continue;
                }
                // Línea de modo: "   1920x1080     60.00*+  50.00" (el * marca la frecuencia actual)
                if (actual != null && l.StartsWith("   ") && l.Contains('*'))
                {
                    var hz = Regex.Match(l, @"([\d.]+)\*");
                    if (hz.Success) actual.Hz = hz.Groups[1].Value;
                }
            }
            return lista;
        }

        public List<Monitor> Listar() => Leer().Where(s => s.Activa)
            .Select(s => new Monitor { Id = s.Id, Nombre = s.Id, Ancho = s.W, Alto = s.H, X = s.X, Y = s.Y, Principal = s.Principal })
            .OrderBy(m => m.X).ToList();

        public JsonNode Capturar() => new JsonArray(Leer().Where(s => s.Activa).Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Id, ["w"] = s.W, ["h"] = s.H, ["x"] = s.X, ["y"] = s.Y, ["hz"] = s.Hz, ["rot"] = s.Rotacion, ["principal"] = s.Principal,
        }).ToArray());

        public void Apagar(HashSet<string> ids, JsonNode estado)
        {
            var args = new List<string>();
            foreach (var id in ids) { args.Add("--output"); args.Add(id); args.Add("--off"); }
            Pantallas.Comprobar(Sistema.Ejecutar("xrandr", args, 30000), "xrandr");
        }

        public void Restaurar(JsonNode estado)
        {
            var args = new List<string>();
            foreach (var s in estado.AsArray())
            {
                args.AddRange(new[] { "--output", (string)s["id"], "--mode", $"{(int)s["w"]}x{(int)s["h"]}", "--pos", $"{(int)s["x"]}x{(int)s["y"]}", "--rotate", (string)s["rot"] ?? "normal" });
                if ((string)s["hz"] is string hz && hz.Length > 0) { args.Add("--rate"); args.Add(hz); }
                if ((bool?)s["principal"] == true) args.Add("--primary");
            }
            if (args.Count > 0) Pantallas.Comprobar(Sistema.Ejecutar("xrandr", args, 30000), "xrandr");
        }

        public void EncenderTodo()
        {
            var args = new List<string>();
            foreach (var s in Leer().Where(s => s.Conectada)) { args.Add("--output"); args.Add(s.Id); args.Add("--auto"); }
            if (args.Count > 0) Pantallas.Comprobar(Sistema.Ejecutar("xrandr", args, 30000), "xrandr");
        }
    }

    // ======================================================================
    // Hyprland: hyprctl
    // ======================================================================

    class MotorHyprland : IMotorPantallas
    {
        public string Nombre => "hyprland";

        static JsonArray Leer()
        {
            var (cod, texto) = Sistema.Ejecutar("hyprctl", new[] { "-j", "monitors", "all" }, 15000);
            if (cod != 0) throw new Exception("hyprctl falló: " + texto);
            return JsonNode.Parse(texto).AsArray();
        }

        public List<Monitor> Listar() => Leer().Where(o => (bool?)o["disabled"] != true)
            .Select(o => new Monitor
            {
                Id = (string)o["name"], Nombre = $"{(string)o["name"]} ({(string)o["make"]} {(string)o["model"]})".Replace("( )", "").Trim(),
                Ancho = (int)o["width"], Alto = (int)o["height"], X = (int)o["x"], Y = (int)o["y"], Principal = (int)o["x"] == 0 && (int)o["y"] == 0,
            }).OrderBy(m => m.X).ToList();

        public JsonNode Capturar() => Leer();

        static void Monitor(string regla) => Pantallas.Comprobar(Sistema.Ejecutar("hyprctl", new[] { "keyword", "monitor", regla }, 15000), "hyprctl");

        public void Apagar(HashSet<string> ids, JsonNode estado)
        {
            foreach (var id in ids) Monitor($"{id},disable");
        }

        public void Restaurar(JsonNode estado)
        {
            foreach (var o in estado.AsArray().Where(o => (bool?)o["disabled"] != true))
                Monitor($"{(string)o["name"]},{(int)o["width"]}x{(int)o["height"]}@{Pantallas.Txt((double)o["refreshRate"])}," +
                        $"{(int)o["x"]}x{(int)o["y"]},{Pantallas.Txt((double)o["scale"])},transform,{(int?)o["transform"] ?? 0}");
        }

        public void EncenderTodo()
        {
            foreach (var o in Leer()) Monitor($"{(string)o["name"]},preferred,auto,1");
        }
    }

    // ======================================================================
    // Sway: swaymsg
    // ======================================================================

    class MotorSway : IMotorPantallas
    {
        public string Nombre => "sway";

        static JsonArray Leer()
        {
            var (cod, texto) = Sistema.Ejecutar("swaymsg", new[] { "-t", "get_outputs", "-r" }, 15000);
            if (cod != 0) throw new Exception("swaymsg falló: " + texto);
            return JsonNode.Parse(texto).AsArray();
        }

        public List<Monitor> Listar() => Leer().Where(o => (bool?)o["active"] == true)
            .Select(o => new Monitor
            {
                Id = (string)o["name"], Nombre = $"{(string)o["name"]} ({(string)o["make"]} {(string)o["model"]})",
                Ancho = (int?)o["current_mode"]?["width"] ?? 0, Alto = (int?)o["current_mode"]?["height"] ?? 0,
                X = (int?)o["rect"]?["x"] ?? 0, Y = (int?)o["rect"]?["y"] ?? 0, Principal = (bool?)o["focused"] == true,
            }).OrderBy(m => m.X).ToList();

        public JsonNode Capturar() => Leer();

        static void Salida(params string[] args) =>
            Pantallas.Comprobar(Sistema.Ejecutar("swaymsg", new[] { "output" }.Concat(args), 15000), "swaymsg");

        public void Apagar(HashSet<string> ids, JsonNode estado)
        {
            foreach (var id in ids) Salida(id, "disable");
        }

        public void Restaurar(JsonNode estado)
        {
            foreach (var o in estado.AsArray().Where(o => (bool?)o["active"] == true))
            {
                var modo = o["current_mode"];
                var hz = ((int?)modo?["refresh"] ?? 60000) / 1000.0;
                Salida((string)o["name"], "enable", "mode", $"{(int)modo["width"]}x{(int)modo["height"]}@{Pantallas.Txt(hz)}Hz",
                       "pos", ((int?)o["rect"]?["x"] ?? 0).ToString(), ((int?)o["rect"]?["y"] ?? 0).ToString(),
                       "scale", Pantallas.Txt((double?)o["scale"] ?? 1), "transform", (string)o["transform"] ?? "normal");
            }
        }

        public void EncenderTodo()
        {
            foreach (var o in Leer()) Salida((string)o["name"], "enable");
        }
    }

    // ======================================================================
    // GNOME (Mutter): interfaz D-Bus org.gnome.Mutter.DisplayConfig. Funciona en Wayland y X11.
    // ======================================================================

    class MotorGnome : IMotorPantallas
    {
        public string Nombre => "gnome";
        const string Servicio = "org.gnome.Mutter.DisplayConfig", Ruta = "/org/gnome/Mutter/DisplayConfig";

        class Modo { public string Id; public int W, H; public double Hz; public bool Actual, Preferido; }
        class Fisico { public string Conector, Fabricante, Producto, Serie, Nombre; public List<Modo> Modos = new(); }
        class Logico { public int X, Y; public double Escala; public uint Giro; public bool Principal; public List<string> Conectores = new(); }
        class EstadoMutter { public uint Serie; public List<Fisico> Fisicos = new(); public List<Logico> Logicos = new(); public uint Disposicion = 1; }

        public static bool Disponible()
        {
            try { return Task.Run(() => Connection.Session.ListServicesAsync()).GetAwaiter().GetResult().Contains(Servicio); }
            catch { return false; }
        }

        // MessageWriter no puede vivir dentro de un método async: los mensajes se arman aparte
        static MessageBuffer MensajeLeer(Connection c)
        {
            using var w = c.GetMessageWriter();
            w.WriteMethodCallHeader(destination: Servicio, path: Ruta, @interface: Servicio, member: "GetCurrentState");
            return w.CreateMessage();
        }

        static EstadoMutter Leer()
        {
            return Task.Run(async () =>
            {
                var c = Connection.Session;
                return await c.CallMethodAsync(MensajeLeer(c), (Message m, object _) =>
                {
                    var r = m.GetBodyReader();
                    var e = new EstadoMutter { Serie = r.ReadUInt32() };
                    // monitores: a((ssss)a(siiddada{sv})a{sv})
                    var finM = r.ReadArrayStart(DBusType.Struct);
                    while (r.HasNext(finM))
                    {
                        r.AlignStruct();
                        r.AlignStruct();
                        var f = new Fisico { Conector = r.ReadString(), Fabricante = r.ReadString(), Producto = r.ReadString(), Serie = r.ReadString() };
                        var finModos = r.ReadArrayStart(DBusType.Struct);
                        while (r.HasNext(finModos))
                        {
                            r.AlignStruct();
                            var modo = new Modo { Id = r.ReadString(), W = r.ReadInt32(), H = r.ReadInt32(), Hz = r.ReadDouble() };
                            r.ReadDouble();             // escala preferida
                            r.ReadArrayOfDouble();      // escalas posibles
                            var props = r.ReadDictionaryOfStringToVariantValue();
                            modo.Actual = props.TryGetValue("is-current", out var a) && a.GetBool();
                            modo.Preferido = props.TryGetValue("is-preferred", out var p) && p.GetBool();
                            f.Modos.Add(modo);
                        }
                        var propsMon = r.ReadDictionaryOfStringToVariantValue();
                        f.Nombre = propsMon.TryGetValue("display-name", out var dn) ? dn.GetString() : f.Conector;
                        e.Fisicos.Add(f);
                    }
                    // monitores lógicos: a(iiduba(ssss)a{sv})
                    var finL = r.ReadArrayStart(DBusType.Struct);
                    while (r.HasNext(finL))
                    {
                        r.AlignStruct();
                        var l = new Logico { X = r.ReadInt32(), Y = r.ReadInt32(), Escala = r.ReadDouble(), Giro = r.ReadUInt32(), Principal = r.ReadBool() };
                        var finC = r.ReadArrayStart(DBusType.Struct);
                        while (r.HasNext(finC))
                        {
                            r.AlignStruct();
                            l.Conectores.Add(r.ReadString()); r.ReadString(); r.ReadString(); r.ReadString();
                        }
                        r.ReadDictionaryOfStringToVariantValue();
                        e.Logicos.Add(l);
                    }
                    var propsGen = r.ReadDictionaryOfStringToVariantValue();
                    if (propsGen.TryGetValue("layout-mode", out var lm)) e.Disposicion = lm.GetUInt32();
                    return e;
                }, null);
            }).GetAwaiter().GetResult();
        }

        /// ApplyMonitorsConfig con método 1 (temporal): si algo sale mal, al reiniciar vuelve lo guardado de siempre
        static void Aplicar(uint serie, List<(int x, int y, double escala, uint giro, bool principal, List<(string conector, string modo)> monitores)> logicos)
        {
            Task.Run(async () =>
            {
                var c = Connection.Session;
                await c.CallMethodAsync(MensajeAplicar(c, serie, logicos));
            }).GetAwaiter().GetResult();
        }

        static MessageBuffer MensajeAplicar(Connection c, uint serie, List<(int x, int y, double escala, uint giro, bool principal, List<(string conector, string modo)> monitores)> logicos)
        {
            using var w = c.GetMessageWriter();
            w.WriteMethodCallHeader(destination: Servicio, path: Ruta, @interface: Servicio, member: "ApplyMonitorsConfig",
                                    signature: "uua(iiduba(ssa{sv}))a{sv}");
            w.WriteUInt32(serie);
            w.WriteUInt32(1);
            var a = w.WriteArrayStart(DBusType.Struct);
            foreach (var l in logicos)
            {
                w.WriteStructureStart();
                w.WriteInt32(l.x); w.WriteInt32(l.y); w.WriteDouble(l.escala); w.WriteUInt32(l.giro); w.WriteBool(l.principal);
                var am = w.WriteArrayStart(DBusType.Struct);
                foreach (var (conector, modo) in l.monitores)
                {
                    w.WriteStructureStart();
                    w.WriteString(conector); w.WriteString(modo);
                    w.WriteDictionary(new Dictionary<string, VariantValue>());
                }
                w.WriteArrayEnd(am);
            }
            w.WriteArrayEnd(a);
            w.WriteDictionary(new Dictionary<string, VariantValue>());
            return w.CreateMessage();
        }

        static string ModoActual(EstadoMutter e, string conector) =>
            e.Fisicos.FirstOrDefault(f => f.Conector == conector)?.Modos.FirstOrDefault(m => m.Actual)?.Id;

        public List<Monitor> Listar()
        {
            var e = Leer();
            return e.Logicos.SelectMany(l => l.Conectores.Select(c =>
            {
                var f = e.Fisicos.First(x => x.Conector == c);
                var m = f.Modos.FirstOrDefault(x => x.Actual);
                return new Monitor { Id = c, Nombre = $"{f.Nombre} ({c})", Ancho = m?.W ?? 0, Alto = m?.H ?? 0, X = l.X, Y = l.Y, Principal = l.Principal };
            })).OrderBy(m => m.X).ToList();
        }

        public JsonNode Capturar()
        {
            var e = Leer();
            return new JsonArray(e.Logicos.Select(l => (JsonNode)new JsonObject
            {
                ["x"] = l.X, ["y"] = l.Y, ["escala"] = l.Escala, ["giro"] = l.Giro, ["principal"] = l.Principal,
                ["monitores"] = new JsonArray(l.Conectores.Select(c => (JsonNode)new JsonObject { ["conector"] = c, ["modo"] = ModoActual(e, c) }).ToArray()),
            }).ToArray());
        }

        public void Apagar(HashSet<string> ids, JsonNode _)
        {
            var e = Leer();
            var quedan = e.Logicos.Where(l => l.Conectores.Any(c => !ids.Contains(c))).OrderBy(l => l.X).ThenBy(l => l.Y).ToList();
            if (quedan.Count == 0) return;
            // Mutter exige que los monitores lógicos se toquen: los que quedan se ponen en fila desde (0,0)
            bool hayPrincipal = quedan.Any(l => l.Principal);
            int x = 0;
            var nuevos = new List<(int, int, double, uint, bool, List<(string, string)>)>();
            foreach (var l in quedan)
            {
                var monitores = l.Conectores.Where(c => !ids.Contains(c)).Select(c => (c, ModoActual(e, c))).ToList();
                var modo = e.Fisicos.First(f => f.Conector == monitores[0].Item1).Modos.First(m => m.Actual);
                bool girado = l.Giro % 2 == 1;
                int ancho = girado ? modo.H : modo.W;
                // En disposición "lógica" las coordenadas van divididas por la escala
                if (e.Disposicion == 1) ancho = (int)Math.Round(ancho / l.Escala);
                nuevos.Add((x, 0, l.Escala, l.Giro, hayPrincipal ? l.Principal : nuevos.Count == 0, monitores));
                x += ancho;
            }
            Aplicar(e.Serie, nuevos);
        }

        public void Restaurar(JsonNode estado)
        {
            var e = Leer();
            var conectados = new HashSet<string>(e.Fisicos.Select(f => f.Conector));
            var logicos = estado.AsArray().Select(l => ((int)l["x"], (int)l["y"], (double)l["escala"], (uint)l["giro"], (bool)l["principal"],
                    l["monitores"].AsArray().Select(m => ((string)m["conector"], (string)m["modo"])).Where(m => conectados.Contains(m.Item1)).ToList()))
                .Where(l => l.Item6.Count > 0).ToList();
            if (logicos.Count > 0) Aplicar(e.Serie, logicos);
        }

        public void EncenderTodo()
        {
            var e = Leer();
            int x = 0;
            var logicos = new List<(int, int, double, uint, bool, List<(string, string)>)>();
            foreach (var f in e.Fisicos)
            {
                var modo = f.Modos.FirstOrDefault(m => m.Preferido) ?? f.Modos.FirstOrDefault();
                if (modo == null) continue;
                logicos.Add((x, 0, 1.0, 0u, logicos.Count == 0, new List<(string, string)> { (f.Conector, modo.Id) }));
                x += modo.W;
            }
            if (logicos.Count > 0) Aplicar(e.Serie, logicos);
        }
    }
}
