using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using BF = Il2CppSystem.Reflection.BindingFlags;
using IType = Il2CppSystem.Type;
using IObj = Il2CppSystem.Object;

namespace CSTDebugMode;

[BepInPlugin("taral.cst.debugmode", "CST Debug Mode", "1.0.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        var h = new HarmonyLib.Harmony("taral.cst.debugmode");
        // Oyun, klasörde tanımadığı dosya (BepInEx) görünce kapanıyor; mod yüklüyken kapanmasın.
        foreach (var m in HarmonyLib.AccessTools.GetDeclaredMethods(typeof(FileIntegrityChecker)).Where(m => m.Name == "Quit"))
        {
            h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(Plugin), nameof(SkipQuit)));
            L.LogInfo("Patch: " + m);
        }
        // Debug ile değiştirilmiş oturum herkese açık sıralamayı kirletmesin.
        h.Patch(HarmonyLib.AccessTools.Method(typeof(Steamworks.SteamUserStats), "UploadLeaderboardScore"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(Plugin), nameof(SkipUpload)));
        Core.BindConfig(Config);
        AddComponent<DebugBehaviour>();
        L.LogInfo("CST Debug Mode yüklendi — F8 menü, F9 HUD, F10 uçma");
    }

    static bool SkipQuit() { L.LogWarning("FileIntegrityChecker.Quit engellendi (mod yüklü)"); return false; }
    static bool SkipUpload() { L.LogWarning("Leaderboard skor gönderimi engellendi (debug mod açık)"); return false; }
}

// IL2CPP'ye enjekte edilen ince kabuk; managed tipli metotlar Core'da kalsın diye.
public class DebugBehaviour : MonoBehaviour
{
    public DebugBehaviour(IntPtr ptr) : base(ptr) { }
    public void Update() => Core.Guard(Core.Update);
    public void LateUpdate() => Core.Guard(Core.LateUpdate);
    public void OnGUI() => Core.Guard(Core.OnGUI);
}

class Target
{
    public Component C;
    public string Label, Aqn, Nid, Key;
    public bool Open = true, ShowMethods;
}

class FieldEntry
{
    public Il2CppSystem.Reflection.FieldInfo F;
    public string Name;
    public bool IsNV;     // NetworkVariable<T>: Value üzerinden okunur/yazılır, host yazınca herkese senkronlanır
    public IType ValT;
    public bool Editable => ValT != null && (ValT.IsEnum || Core.Prims.Contains(ValT.FullName));
}

class NetSnap
{
    public NetworkObject No;
    public ulong Id, Owner;
    public string Name;
    public bool Player;
    public Vector3 Pos;
}

static class Core
{
    const string MsgName = "CSTDebug";
    internal static readonly HashSet<string> Prims = new()
        { "System.Int32", "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.Boolean", "System.String" };
    static readonly string[] Tabs = { "Genel", "Ağ", "Oyuncu", "İnceleyici", "Konsol" };
    static readonly string[] Presets = { "CarServiceManager", "JobDispatchManager", "ServiceRequestSystem", "CSTNetworkManager" };
    static readonly HashSet<string> SkipMethods = new()
        { "Awake", "Start", "Update", "LateUpdate", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy", "OnNetworkSpawn", "OnNetworkDespawn", "OnGUI" };
    const BF Decl = BF.Instance | BF.Public | BF.NonPublic | BF.DeclaredOnly;
    const BF Any = BF.Instance | BF.Public | BF.NonPublic;

    static bool inited, menu, hud = true, fly, allowClients, errorsOnly;
    static int tab, errorCount;
    static Vector2 scroll;
    static float smoothDt = 0.016f, flySpeed = 10f, snapTimer, toastUntil, timeScale = 1f;
    static string toast = "", hostMod = "-", objFilter = "", typeQuery = "", lastErr;
    static Vector3 flyPos;
    static readonly Vector3?[] slots = new Vector3?[3];
    static CursorLockMode prevLock;
    static bool prevVisible;
    static CustomMessagingManager registeredCmm;
    static CustomMessagingManager.HandleNamedMessageDelegate msgHandler;
    static Application.LogCallback logCb;
    static readonly List<(LogType type, string text)> logs = new();
    static readonly List<NetSnap> snap = new();
    static readonly List<Target> targets = new();
    static List<IType> typeCache;
    static readonly List<IType> typeResults = new();
    static readonly Dictionary<string, List<FieldEntry>> fieldCache = new();
    static readonly Dictionary<string, List<string>> methodCache = new();
    static readonly Dictionary<string, string> edits = new();
    static readonly Dictionary<string, string> vals = new();   // alan değerleri, OnGUI her olayda reflection yapmasın diye
    static IntPtr flyOwner;
    static int consoleCount;
    static bool hasHit;
    static RaycastHit hit;

    static NetworkManager NM => NetworkManager.Singleton;
    static bool Online => NM != null && NM.IsListening;
    static bool PureClient => Online && !NM.IsServer;

    internal static void Guard(Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            if (e.Message != lastErr) Plugin.L.LogError(e);   // her karede aynı hatayı basmasın
            lastErr = e.Message;
        }
    }

    // ───────────────────────── döngü ─────────────────────────

    internal static void Update()
    {
        if (!inited) Init();
        smoothDt = Mathf.Lerp(smoothDt, Time.unscaledDeltaTime, 0.05f);
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.f8Key.wasPressedThisFrame) ToggleMenu();
            if (kb.f9Key.wasPressedThisFrame) hud = !hud;
            if (kb.f10Key.wasPressedThisFrame) SetFly(!fly);
        }
        EnsureMessaging();
        if (!menu) return;
        targets.RemoveAll(t => t.C == null);   // OnGUI'de silmek IMGUI kontrol sayısını bozar
        if ((snapTimer -= Time.unscaledDeltaTime) <= 0f) { snapTimer = 0.5f; Snapshot(); RefreshValues(); }
        Raycast();
    }

    static void RefreshValues()
    {
        foreach (var t in targets)
            if (t.Open)
                foreach (var e in Fields(t.C)) vals[t.Key + e.Name] = Read(t.C, e);
    }

    internal static void LateUpdate()
    {
        if (menu) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }  // oyun her karede kilitliyor olabilir
        if (fly) FlyStep(Keyboard.current);
    }

    static void Init()
    {
        inited = true;
        logCb = DelegateSupport.ConvertDelegate<Application.LogCallback>(new Action<string, string, LogType>(OnLog));
        Application.add_logMessageReceived(logCb);
    }

    static void ToggleMenu()
    {
        menu = !menu;
        if (menu) { prevLock = Cursor.lockState; prevVisible = Cursor.visible; }
        else { Cursor.lockState = prevLock; Cursor.visible = prevVisible; }
    }

    static void OnLog(string cond, string stack, LogType type)
    {
        bool err = type is LogType.Error or LogType.Exception or LogType.Assert;
        if (err) errorCount++;
        string first = err && !string.IsNullOrEmpty(stack) ? "\n   " + stack.Split('\n')[0] : "";
        logs.Add((type, cond + first));
        if (logs.Count > 400) logs.RemoveAt(0);
    }

    static void Toast(string s) { toast = s; toastUntil = Time.unscaledTime + 3f; Plugin.L.LogInfo(s); }

    // ───────────────────────── multiplayer ─────────────────────────

    static void EnsureMessaging()
    {
        var cmm = Online ? NM.CustomMessagingManager : null;
        if (cmm == null) { registeredCmm = null; hostMod = "-"; return; }
        if (registeredCmm != null && registeredCmm.Pointer == cmm.Pointer) return;   // her oturumda yeni CMM oluşuyor
        msgHandler ??= DelegateSupport.ConvertDelegate<CustomMessagingManager.HandleNamedMessageDelegate>(
            new Action<ulong, FastBufferReader>(OnMsg));
        cmm.RegisterNamedMessageHandler(MsgName, msgHandler);
        registeredCmm = cmm;
        hostMod = NM.IsServer ? "-" : "yanıt bekleniyor (host'ta mod yoksa yanıt gelmez)";
        if (!NM.IsServer) Send(NetworkManager.ServerClientId, "ping");
    }

    static void Send(ulong to, string msg, bool all = false)
    {
        var cmm = NM?.CustomMessagingManager;
        if (cmm == null) return;
        var b = Encoding.UTF8.GetBytes(msg);
        var w = new FastBufferWriter(b.Length + 8, Allocator.Temp, -1);
        try
        {
            foreach (var x in b) w.WriteByteSafe(x);   // generic WriteValue IL2CPP'de stripped olabilir; byte byte güvenli
            if (all) cmm.SendNamedMessageToAll(MsgName, w, NetworkDelivery.ReliableSequenced);
            else cmm.SendNamedMessage(MsgName, to, w, NetworkDelivery.ReliableSequenced);
        }
        finally { w.Dispose(); }
    }

    static void OnMsg(ulong sender, FastBufferReader r)
    {
        try
        {
            var b = new byte[r.Length - r.Position];
            for (int i = 0; i < b.Length; i++) r.ReadByteSafe(out b[i]);
            Handle(sender, Encoding.UTF8.GetString(b).Split('\t'));
        }
        catch (Exception e) { Plugin.L.LogError("CSTDebug mesaj hatası: " + e); }
    }

    static void Handle(ulong sender, string[] p)
    {
        bool fromServer = sender == NetworkManager.ServerClientId;
        switch (p[0])
        {
            case "ping":
                if (!NM.IsServer) return;
                Send(sender, "pong\t" + (allowClients ? "1" : "0"));
                Send(sender, "ts\t" + Time.timeScale.ToString(CultureInfo.InvariantCulture));
                return;
            case "pong":
                if (fromServer) hostMod = p[1] == "1" ? "VAR — istemci komutlarına izin veriyor" : "VAR — istemci komutları KAPALI";
                return;
            case "ts":
                if (fromServer && !NM.IsServer) Time.timeScale = timeScale = F(p[1]);
                return;
            case "info":
                if (fromServer) Toast("[Host] " + p[1]);
                return;
        }
        if (!NM.IsServer) return;
        // güven sınırı: istemci komutları yalnızca host açıkça izin verirse çalışır
        if (!allowClients) { Send(sender, "info\tHost istemci debug komutlarını kapattı"); return; }
        string res;
        try { res = Exec(sender, p); } catch (Exception e) { res = "hata: " + e.Message; }
        Plugin.L.LogInfo($"[CSTDebug] client {sender}: {string.Join(' ', p)} -> {res}");
        Send(sender, "info\t" + res);
    }

    static string Exec(ulong sender, string[] p)
    {
        switch (p[0])
        {
            case "tp" when p.Length >= 4:
                var to = new Vector3(F(p[1]), F(p[2]), F(p[3]));
                if (!float.IsFinite(to.x) || !float.IsFinite(to.y) || !float.IsFinite(to.z)) return "geçersiz koordinat";
                var po = NM.SpawnManager.GetPlayerNetworkObject(sender);
                if (po == null) return "oyuncu nesnesi yok";
                MoveTo(po.gameObject, to);
                return "ışınlandı";
            case "set" when p.Length >= 5:
                { var c = Resolve(p[1], p[2]); return c == null ? "hedef bulunamadı" : SetField(c, p[3], p[4]); }
            case "call" when p.Length >= 4:
                { var c = Resolve(p[1], p[2]); return c == null ? "hedef bulunamadı" : Call(c, p[3]); }
        }
        return "bilinmeyen komut: " + p[0];
    }

    static Component Resolve(string aqn, string nid)
    {
        var t = IType.GetType(aqn);
        if (t == null) return null;
        if (ulong.TryParse(nid, out var id))   // ağ nesnesi: yalnız o ID; yoksa başka örneğe düşme
        {
            var spawned = NM?.SpawnManager?.SpawnedObjects;
            return spawned != null && spawned.ContainsKey(id) ? spawned[id].GetComponentInChildren(t, true) : null;
        }
        var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
        return arr.Length > 0 ? arr[0].TryCast<Component>() : null;
    }

    static string Clean(string s) => (s ?? "").Replace('\t', ' ');
    static float F(string s) => float.Parse(s.Trim().Replace(',', '.'), CultureInfo.InvariantCulture);
    static string V(Vector3 v) => $"{v.x:0.0}, {v.y:0.0}, {v.z:0.0}";

    static void SetTimeScale(float v)
    {
        if (PureClient) { Toast("Zaman ölçeği sadece host'ta değişir"); return; }
        Time.timeScale = timeScale = v;
        if (Online) Send(0, "ts\t" + v.ToString(CultureInfo.InvariantCulture), all: true);  // istemciler aynı hızda kalsın
    }

    // ───────────────────────── oyuncu ─────────────────────────

    static GameObject LocalPlayer()
    {
        var po = Online ? NM.SpawnManager.GetLocalPlayerObject() : null;
        if (po != null) return po.gameObject;
        var cam = Camera.main;
        return cam != null ? cam.transform.root.gameObject : null;
    }

    static void MoveTo(GameObject go, Vector3 p)
    {
        var cc = go.GetComponent<CharacterController>();
        bool ccOn = cc != null && cc.enabled;
        if (ccOn) cc.enabled = false;   // CC açıkken transform.position'ı ezer
        var nt = go.GetComponent<NetworkTransform>();
        if (nt != null && nt.CanCommitToTransform) nt.Teleport(p, go.transform.rotation, go.transform.localScale);
        else go.transform.position = p;
        var rb = go.GetComponent<Rigidbody>();
        if (rb != null) { rb.position = p; if (!rb.isKinematic) rb.linearVelocity = Vector3.zero; }
        if (ccOn) cc.enabled = true;
    }

    // Sahiplik modeli bilinmiyor: yerelde taşı (owner-auth) + host'tan da taşımasını iste (server-auth). İkisinden biri tutar.
    static void Teleport(Vector3 p)
    {
        var go = LocalPlayer();
        if (go == null) { Toast("Oyuncu bulunamadı"); return; }
        MoveTo(go, p);
        flyPos = p;
        if (PureClient) Send(NetworkManager.ServerClientId, $"tp\t{p.x.ToString(CultureInfo.InvariantCulture)}\t{p.y.ToString(CultureInfo.InvariantCulture)}\t{p.z.ToString(CultureInfo.InvariantCulture)}");
        Toast("Işınlandı: " + V(p));
    }

    static void SetFly(bool on)
    {
        var go = LocalPlayer();
        if (on && go == null) { Toast("Oyuncu bulunamadı"); return; }
        fly = on;
        if (on) { flyPos = go.transform.position; flyOwner = go.Pointer; }
        Toast(on ? "Uçma AÇIK (WASD, Space/Ctrl, Shift hızlı)" : "Uçma kapalı");
    }

    static void FlyStep(Keyboard kb)
    {
        var go = LocalPlayer();
        var cam = Camera.main;
        if (go == null || cam == null || kb == null) return;
        if (go.Pointer != flyOwner) { fly = false; Toast("Oyuncu değişti, uçma kapatıldı"); return; }   // sahne/oturum değişti: eski konuma kilitleme
        var t = cam.transform;
        var d = Vector3.zero;
        if (kb.wKey.isPressed) d += t.forward;
        if (kb.sKey.isPressed) d -= t.forward;
        if (kb.dKey.isPressed) d += t.right;
        if (kb.aKey.isPressed) d -= t.right;
        if (kb.spaceKey.isPressed) d += Vector3.up;
        if (kb.leftCtrlKey.isPressed) d -= Vector3.up;
        if (!menu) flyPos += d.normalized * flySpeed * (kb.leftShiftKey.isPressed ? 3f : 1f) * Time.unscaledDeltaTime;
        // CC'yi kapatmıyoruz (Move uyarısı spamler); her LateUpdate konumu zorlamak noclip etkisi verir
        go.transform.position = flyPos;
        var rb = go.GetComponent<Rigidbody>();
        if (rb != null && !rb.isKinematic) rb.linearVelocity = Vector3.zero;
        Physics.SyncTransforms();
    }

    static void Raycast()
    {
        hasHit = false;
        var cam = Camera.main;
        if (cam == null) return;
        var me = LocalPlayer();
        var hits = Physics.RaycastAll(cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)), 300f);
        float best = float.MaxValue;
        foreach (var h in hits)
        {
            if (h.collider == null || h.distance >= best) continue;
            if (me != null && h.collider.transform.IsChildOf(me.transform)) continue;   // kendi collider'ımızı atla
            best = h.distance; hit = h; hasHit = true;
        }
    }

    // ───────────────────────── ağ snapshot ─────────────────────────

    static void Snapshot()
    {
        snap.Clear();
        if (!Online) return;
        foreach (var no in NM.SpawnManager.SpawnedObjectsList)
        {
            if (no == null) continue;
            snap.Add(new NetSnap { No = no, Id = no.NetworkObjectId, Owner = no.OwnerClientId, Name = no.name, Player = no.IsPlayerObject, Pos = no.transform.position });
        }
    }

    static ulong Rtt(ulong clientId)
    {
        try { return NM.NetworkConfig.NetworkTransport.GetCurrentRtt(clientId); } catch { return 0; }
    }

    // ───────────────────────── reflection inceleyici ─────────────────────────

    static void AddTarget(Component c)
    {
        if (c == null || targets.Any(t => t.C.Pointer == c.Pointer)) return;
        var no = c.GetComponentInParent<NetworkObject>();
        var type = c.GetIl2CppType();
        targets.Add(new Target
        {
            C = c,
            Key = c.Pointer.ToString(),
            Aqn = type.AssemblyQualifiedName,
            Nid = no != null && no.IsSpawned ? no.NetworkObjectId.ToString() : "-",
            Label = $"{type.Name} @ {c.gameObject.name}" + (no != null ? $" [net {no.NetworkObjectId}]" : " [yerel]"),
        });
        tab = 3;
        snapTimer = 0f;   // değerler bir sonraki karede dolsun
    }

    static void InspectAll(GameObject root)
    {
        foreach (var c in root.GetComponentsInChildren<Component>(true))
        {
            var n = c.GetIl2CppType().FullName ?? "";
            if (!n.StartsWith("UnityEngine.") && !n.StartsWith("Unity.")) AddTarget(c);   // inceleyici yalnız oyun alanlarını gösteriyor
        }
    }

    static void FindInstances(IType t)
    {
        var arr = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
        int n = 0;
        foreach (var o in arr) { if (n++ >= 8) break; AddTarget(o.TryCast<Component>()); }
        Toast($"{t.Name}: {arr.Length} örnek bulundu");
    }

    static void SearchTypes()
    {
        if (typeCache == null)
        {
            typeCache = new List<IType>();
            var comp = Il2CppType.Of<Component>();
            foreach (var asm in Il2CppSystem.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Assembly-CSharp") continue;
                foreach (var t in asm.GetTypes()) if (t.IsSubclassOf(comp)) typeCache.Add(t);
            }
        }
        typeResults.Clear();
        typeResults.AddRange(typeCache.Where(t => t.Name.IndexOf(typeQuery, StringComparison.OrdinalIgnoreCase) >= 0).Take(40));
    }

    // Unity/Netcode taban sınıflarına gelince dur — oyunun kendi alanları yeter
    static IEnumerable<IType> GameHierarchy(IType t)
    {
        for (; t != null; t = t.BaseType)
        {
            var n = t.FullName ?? "";
            if (n.StartsWith("UnityEngine.") || n.StartsWith("Unity.")) yield break;
            yield return t;
        }
    }

    static FieldEntry Classify(Il2CppSystem.Reflection.FieldInfo f)
    {
        var ft = f.FieldType;
        if (ft.IsGenericType && ft.Name.StartsWith("NetworkVariable"))
        {
            var a = ft.GetGenericArguments();
            return new FieldEntry { F = f, Name = f.Name, IsNV = true, ValT = a.Length > 0 ? a[0] : null };
        }
        if (ft.IsEnum || Prims.Contains(ft.FullName)) return new FieldEntry { F = f, Name = f.Name, ValT = ft };
        return null;
    }

    static List<FieldEntry> Fields(Component c)
    {
        var type = c.GetIl2CppType();
        if (fieldCache.TryGetValue(type.FullName, out var list)) return list;
        list = new List<FieldEntry>();
        foreach (var t in GameHierarchy(type))
            foreach (var f in t.GetFields(Decl)) { var e = Classify(f); if (e != null) list.Add(e); }
        return fieldCache[type.FullName] = list;
    }

    static List<string> Methods(Component c)
    {
        var type = c.GetIl2CppType();
        if (methodCache.TryGetValue(type.FullName, out var list)) return list;
        list = new List<string>();
        foreach (var t in GameHierarchy(type))
            foreach (var m in t.GetMethods(Decl))
            {
                var n = m.Name;
                if (m.IsSpecialName || m.GetParameters().Length != 0 || n.StartsWith("__") || n.Contains('<') || SkipMethods.Contains(n)) continue;
                if (!list.Contains(n)) list.Add(n);
            }
        list.Sort();
        return methodCache[type.FullName] = list;
    }

    static string Read(Component c, FieldEntry e)
    {
        try
        {
            IObj v = e.F.GetValue(c);
            if (e.IsNV && v != null) v = v.GetIl2CppType().GetProperty("Value").GetValue(v, null);
            return v?.ToString() ?? "null";
        }
        catch (Exception ex) { return "ERR " + ex.Message; }
    }

    static IObj Box(IType t, string s)
    {
        if (t.IsEnum) return Il2CppSystem.Enum.Parse(t, s.Trim());
        var ci = CultureInfo.InvariantCulture;
        var n = s.Trim().Replace(',', '.');
        return t.FullName switch
        {
            "System.String" => new Il2CppSystem.String(IL2CPP.ManagedStringToIl2Cpp(s)),
            "System.Int32" => BoxV(int.Parse(n, ci)),
            "System.Int64" => BoxV(long.Parse(n, ci)),
            "System.UInt64" => BoxV(ulong.Parse(n, ci)),
            "System.Single" => BoxV(float.Parse(n, ci)),
            "System.Double" => BoxV(double.Parse(n, ci)),
            "System.Boolean" => BoxV(n.ToLowerInvariant() switch
            {
                "1" or "true" or "evet" => true,
                "0" or "false" or "hayır" => false,
                _ => throw new FormatException("bool bekleniyor: 1/0, true/false, evet/hayır"),
            }),
            _ => throw new NotSupportedException(t.FullName),
        };
    }

    // interop primitive struct'ları salt okunur; native il2cpp box en doğrudan yol
    static unsafe IObj BoxV<T>(T v) where T : struct
        => new IObj(IL2CPP.il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, (IntPtr)System.Runtime.CompilerServices.Unsafe.AsPointer(ref v)));

    static string SetField(Component c, string name, string val)
    {
        var f = c.GetIl2CppType().GetField(name, Any);
        var e = f == null ? null : Classify(f);
        if (e == null || !e.Editable) return $"{name}: desteklenmeyen alan";
        var boxed = Box(e.ValT, val);
        if (e.IsNV)
        {
            var nv = f.GetValue(c);
            nv.GetIl2CppType().GetProperty("Value").SetValue(nv, boxed, null);
        }
        else f.SetValue(c, boxed);
        return $"{name} = {val}";
    }

    static string Call(Component c, string name)
    {
        foreach (var t in GameHierarchy(c.GetIl2CppType()))
            foreach (var m in t.GetMethods(Decl))
                if (m.Name == name && m.GetParameters().Length == 0)
                {
                    var r = m.Invoke(c, null);
                    return $"{name}() çağrıldı" + (r != null ? " → " + r.ToString() : "");
                }
        return name + " bulunamadı";
    }

    // Ağ nesnesiyse ve biz istemciysek host'ta çalıştır; değilse yerelde
    static void SetFieldUI(Target t, string field, string val)
    {
        if (PureClient && t.Nid != "-") { Send(NetworkManager.ServerClientId, $"set\t{t.Aqn}\t{t.Nid}\t{field}\t{Clean(val)}"); Toast("Host'a gönderildi"); }
        else Toast(SafeRun(() => SetField(t.C, field, val)));
    }

    static void CallUI(Target t, string method)
    {
        // *ServerRpc metotlarını yerelde çağırmak zaten oyunun kendi RPC'sini host'a yollar
        if (PureClient && t.Nid != "-" && !method.EndsWith("ServerRpc")) { Send(NetworkManager.ServerClientId, $"call\t{t.Aqn}\t{t.Nid}\t{method}"); Toast("Host'a gönderildi"); }
        else Toast(SafeRun(() => Call(t.C, method)));
    }

    static string SafeRun(Func<string> f) { try { return f(); } catch (Exception e) { return "hata: " + (e.InnerException?.Message ?? e.Message); } }

    // ───────────────────────── GUI ─────────────────────────
    // Oyunun build'inde GUILayout motoru stripped (BeginArea/Space/GetLast "unstripping failed").
    // Bu yüzden sağlam kalan Rect tabanlı GUI.* ile basit bir satır-satır yerleşim + DrawTexture ile çizilmiş tema.
    const float LineH = 24f, Gap = 5f, TitleH = 36f, Grip = 18f, MinW = 460f, MinH = 320f;
    static float lx, lw, cx, cy, rowH, contentH = 1000f, visTop = float.MinValue, visBot = float.MaxValue;
    static bool inRow;

    // tema
    static readonly Color CBg = new(0.075f, 0.082f, 0.10f, 0.97f), CTitle = new(0.11f, 0.12f, 0.15f, 1f),
        CCtl = new(0.17f, 0.18f, 0.22f, 1f), CCtlHover = new(0.23f, 0.25f, 0.30f, 1f), CField = new(0.05f, 0.055f, 0.07f, 1f),
        CAccent = new(1f, 0.55f, 0.12f, 1f), CText = new(0.90f, 0.91f, 0.93f, 1f), CMuted = new(0.60f, 0.63f, 0.68f, 1f),
        CShadow = new(0f, 0f, 0f, 0.45f);
    static Texture2D white;
    static GUIStyle sLabel, sCenter, sTitle, sSection, sField, sHud, sNone, sThumb;

    // pencere (1080p uzayında); konum/boyut BepInEx config'de saklanır
    static Rect win = new(16, 50, 700, 900);
    static int drag;   // 0 yok, 1 taşı, 2 boyutlandır
    static Vector2 dragOff;
    static BepInEx.Configuration.ConfigEntry<string> winCfg;

    internal static void BindConfig(BepInEx.Configuration.ConfigFile cfg)
    {
        winCfg = cfg.Bind("Menu", "Window", "16,50,700,900", "Menü konumu ve boyutu: x,y,genişlik,yükseklik (1080p ölçeğinde)");
        var p = winCfg.Value.Split(',');
        if (p.Length == 4 && p.All(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            win = new Rect(F(p[0]), F(p[1]), Mathf.Max(MinW, F(p[2])), Mathf.Max(MinH, F(p[3])));
    }

    static void SaveWin() =>
        winCfg.Value = string.Join(",", new[] { win.x, win.y, win.width, win.height }.Select(v => v.ToString("0", CultureInfo.InvariantCulture)));

    static GUIStyle Style(Color c, int size = 13, TextAnchor a = TextAnchor.MiddleLeft, bool bold = false)
    {
        var st = new GUIStyle { richText = true, wordWrap = false, fontSize = size, alignment = a, clipping = TextClipping.Clip };
        if (bold) st.fontStyle = FontStyle.Bold;
        st.normal = new GUIStyleState { textColor = c };
        st.hover = new GUIStyleState { textColor = c };
        st.active = new GUIStyleState { textColor = c };
        return st;
    }

    static void EnsureTheme()
    {
        if (white != null && sLabel != null) return;
        white = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };   // sahne değişiminde Unload edilmesin
        white.SetPixel(0, 0, Color.white);
        white.Apply();
        sLabel = Style(CText);
        sCenter = Style(CText, 13, TextAnchor.MiddleCenter);
        sTitle = Style(CText, 15, TextAnchor.MiddleLeft, true);
        sSection = Style(CAccent, 12, TextAnchor.MiddleLeft, true);
        sField = Style(CText);
        sHud = Style(CText, 13, TextAnchor.UpperLeft);
        sNone = new GUIStyle();
        sThumb = new GUIStyle { fixedWidth = 14, fixedHeight = 14 };
    }

    static void Fill(Rect r, Color c, float rad = 0f) =>
        GUI.DrawTexture(r, white, ScaleMode.StretchToFill, true, 0f, c, Vector4.zero, rad);
    static void Fill(Rect r, Color c, Vector4 radii) =>
        GUI.DrawTexture(r, white, ScaleMode.StretchToFill, true, 0f, c, Vector4.zero, radii);
    static bool Hover(Rect r) => r.Contains(Event.current.mousePosition);

    static Rect Next(float w, float h)
    {
        if (!inRow) { var full = new Rect(lx, cy, lw, h); cy += h + Gap; return full; }
        if (w <= 0) w = Mathf.Max(40f, lx + lw - cx);   // satırın kalanı
        var r = new Rect(cx, cy, w, h);
        cx += w + Gap;
        rowH = Mathf.Max(rowH, h);
        return r;
    }

    static bool Vis(Rect r) => r.yMax >= visTop && r.y <= visBot;   // kaydırma alanı dışını çizme
    // shortcut: metin genişliği kaba tahmin (CalcSize'a güvenmiyoruz), çok uzun buton yazılarında taşabilir
    static float TextW(string s) => 22f + 7.5f * System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "").Length;

    static bool Btn(string s) => Btn(s, inRow ? TextW(s) : 0);
    static bool Btn(string s, float w) => Pill(Next(w, LineH), s, false);

    static bool Pill(Rect r, string s, bool active)
    {
        if (!Vis(r)) return false;
        Fill(r, active ? CAccent : Hover(r) ? CCtlHover : CCtl, 6f);
        GUI.Label(r, active ? $"<b><color=#111111>{s}</color></b>" : s, sCenter);
        return GUI.Button(r, "", sNone);
    }

    static void Lbl(string s) => Lbl(s, 0);
    static void Lbl(string s, float w)
    {
        var r = Next(w, LineH * (1 + s.Count(c => c == '\n')));
        if (Vis(r)) GUI.Label(r, s, sLabel);
    }

    static void Sec(string s)
    {
        Space(6);
        var r = Next(0, 20);
        if (!Vis(r)) return;
        GUI.Label(r, s.ToUpperInvariant(), sSection);
        Fill(new Rect(r.x, r.yMax, r.width, 1), new Color(CAccent.r, CAccent.g, CAccent.b, 0.35f));
    }

    // GUI.TextField kullanılamıyor (DoTextField/TextEditor stripped); tıkla-odaklan + Event KeyDown ile basit giriş.
    static string focusKey;

    static string Txt(string s, float w, string key)
    {
        s ??= "";
        var r = Next(w, LineH);
        var e = Event.current;
        if (e.type == EventType.MouseDown && Vis(r) && Hover(r)) { focusKey = key; e.Use(); }
        bool focused = focusKey == key;
        if (focused && e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Backspace) { if (s.Length > 0) s = s[..^1]; e.Use(); }
            else if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter or KeyCode.Escape or KeyCode.Tab) { focusKey = null; e.Use(); }
            else if (e.character >= ' ' && e.character != '\t') { s += e.character; e.Use(); }
        }
        if (!Vis(r)) return s;
        Fill(r, CField, 5f);
        Fill(new Rect(r.x, r.yMax - 2, r.width, 2), focused ? CAccent : Hover(r) ? CCtlHover : CCtl, new Vector4(0, 0, 5, 5));
        bool caret = focused && (int)(Time.unscaledTime * 2) % 2 == 0;
        GUI.Label(new Rect(r.x + 6, r.y, r.width - 12, r.height), s.Replace("<", "‹") + (caret ? "|" : ""), sField);
        return s;
    }

    static bool Tgl(bool v, string s)
    {
        var r = Next(inRow ? TextW(s) + 26 : 0, LineH);
        if (!Vis(r)) return v;
        var box = new Rect(r.x + 2, r.y + 4, 16, 16);
        Fill(box, v ? CAccent : Hover(r) ? CCtlHover : CCtl, 4f);
        if (v) Fill(new Rect(box.x + 5, box.y + 5, 6, 6), new Color(0.07f, 0.07f, 0.08f, 1f), 2f);
        GUI.Label(new Rect(r.x + 24, r.y, r.width - 24, r.height), s, sLabel);
        return GUI.Button(r, "", sNone) ? !v : v;
    }

    static float Sld(float v, float a, float b, float w = 0)
    {
        var r = Next(w, LineH);
        if (!Vis(r)) return v;
        float t = Mathf.InverseLerp(a, b, v);
        var track = new Rect(r.x, r.y + r.height / 2 - 3, r.width, 6);
        Fill(track, CCtl, 3f);
        Fill(new Rect(track.x, track.y, track.width * t, track.height), CAccent, 3f);
        Fill(new Rect(track.x + track.width * t - 7, r.y + r.height / 2 - 7, 14, 14), CText, 7f);
        return GUI.HorizontalSlider(r, v, a, b, sNone, sThumb);
    }

    static void H() { inRow = true; cx = lx; rowH = LineH; }
    static void EH() { inRow = false; cy += rowH + Gap; }
    static void Space(float f) => cy += f;

    // Başlık çubuğundan taşı, sağ alt köşeden boyutlandır. Kontrollerden önce çalışır ki olayı onlar yutmasın.
    static void WindowInput(Rect title, Rect grip, Rect close, float sw, float sh)
    {
        var e = Event.current;
        var m = e.mousePosition;
        switch (e.type)
        {
            case EventType.MouseDown when grip.Contains(m):
                drag = 2; dragOff = new Vector2(win.xMax - m.x, win.yMax - m.y); e.Use(); break;
            case EventType.MouseDown when title.Contains(m) && !close.Contains(m):
                drag = 1; dragOff = m - win.position; e.Use(); break;
            case EventType.MouseDrag when drag == 1:
                win.position = new Vector2(Mathf.Clamp(m.x - dragOff.x, -win.width + 120, sw - 120), Mathf.Clamp(m.y - dragOff.y, 0, sh - TitleH));
                e.Use(); break;
            case EventType.MouseDrag when drag == 2:
                win.width = Mathf.Clamp(m.x + dragOff.x - win.x, MinW, sw - win.x);
                win.height = Mathf.Clamp(m.y + dragOff.y - win.y, MinH, sh - win.y);
                e.Use(); break;
            case EventType.MouseUp when drag != 0:
                drag = 0; SaveWin(); e.Use(); break;
        }
    }

    internal static void OnGUI()
    {
        float s = Screen.height / 1080f;   // 1440p'de okunur boyut
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        float sw = Screen.width / s, sh = 1080f;
        EnsureTheme();
        visTop = float.MinValue; visBot = float.MaxValue;
        if (Event.current.type == EventType.MouseDown) focusKey = null;   // boş yere tıklamak odağı bırakır; alan tekrar alır

        if (Time.unscaledTime < toastUntil)
        {
            var tr = new Rect(sw / 2 - 300, 14, 600, 32);
            Fill(tr, CBg, 8f);
            Fill(new Rect(tr.x, tr.y, 4, tr.height), CAccent, new Vector4(8, 0, 0, 8));
            GUI.Label(tr, toast, sCenter);
        }
        if (hud) DrawHud(sw);
        if (!menu) return;

        GUI.Label(new Rect(sw / 2 - 10, 540 - 12, 20, 24), "<b>+</b>", sCenter);

        var titleR = new Rect(win.x, win.y, win.width, TitleH);
        var closeR = new Rect(win.xMax - 34, win.y + 6, 26, 24);
        var gripR = new Rect(win.xMax - Grip, win.yMax - Grip, Grip, Grip);
        WindowInput(titleR, gripR, closeR, sw, sh);

        // çerçeve
        Fill(new Rect(win.x + 4, win.y + 6, win.width, win.height), CShadow, 12f);
        Fill(win, CBg, 10f);
        Fill(titleR, CTitle, new Vector4(10, 10, 0, 0));
        Fill(new Rect(win.x, win.y + TitleH - 2, win.width, 2), CAccent);
        GUI.Label(new Rect(win.x + 14, win.y, 300, TitleH), "CST DEBUG MODE", sTitle);
        GUI.Label(new Rect(win.x + 170, win.y, win.width - 220, TitleH),
            $"<color=#9aa0a6>{Role} • {1f / smoothDt:0} FPS • F8 kapat</color>", sLabel);
        if (Pill(closeR, "X", false)) ToggleMenu();

        // sekmeler
        lx = win.x + 12; lw = win.width - 24; cy = win.y + TitleH + 10; inRow = false;
        H();
        for (int i = 0; i < Tabs.Length; i++)
            if (Pill(Next(TextW(Tabs[i]) + 10, 28), Tabs[i], i == tab)) { tab = i; scroll = Vector2.zero; }
        EH();

        // içerik
        var view = new Rect(lx, cy + 2, lw, win.yMax - cy - Grip - 6);
        scroll = GUI.BeginScrollView(view, scroll, new Rect(0, 0, view.width - 20, Mathf.Max(contentH, view.height)));
        lx = 0; lw = view.width - 22; cy = 0;
        visTop = scroll.y - 60; visBot = scroll.y + view.height + 60;
        try
        {
            switch (tab)
            {
                case 0: DrawGeneral(); break;
                case 1: DrawNet(); break;
                case 2: DrawPlayer(); break;
                case 3: DrawInspector(); break;
                case 4: DrawConsole(); break;
            }
            contentH = cy + 8;   // bir sonraki karede kaydırma yüksekliği
        }
        finally { GUI.EndScrollView(); }   // sekme hata verse de GUIClip dengesi bozulmasın (yoksa her karede Unity hatası)

        // boyutlandırma tutamacı
        var gc = drag == 2 || Hover(gripR) ? CAccent : CMuted;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j <= i; j++)
                Fill(new Rect(gripR.xMax - 5 - (i - j) * 5, gripR.yMax - 5 - j * 5, 3, 3), gc, 1f);
    }

    static string Role => !Online ? "Çevrimdışı" : NM.IsHost ? "HOST" : NM.IsServer ? "SERVER" : "CLIENT";

    static void DrawHud(float sw)
    {
        var sb = new StringBuilder();
        sb.Append($"<b><size=15>{1f / smoothDt:0} FPS</size></b>  <color=#9aa0a6>{smoothDt * 1000f:0.0} ms • {SystemInfo.graphicsDeviceType}</color>\n");
        sb.Append($"<color=#ff8c1f>{Role}</color>");
        if (Online) sb.Append($"  id {NM.LocalClientId}  ping {(NM.IsServer ? 0 : Rtt(NetworkManager.ServerClientId))} ms");
        var p = LocalPlayer();
        if (p != null) sb.Append($"\n<color=#9aa0a6>pos</color> {V(p.transform.position)}");
        if (Math.Abs(Time.timeScale - 1f) > 0.001f) sb.Append($"\n<color=yellow>timeScale {Time.timeScale:0.##}</color>");
        if (fly) sb.Append("\n<color=cyan>UÇMA</color>");
        if (errorCount > 0) sb.Append($"\n<color=#ff6060>{errorCount} hata</color> <color=#9aa0a6>(Konsol)</color>");
        var text = sb.ToString();
        var r = new Rect(sw - 316, 16, 300, 16 + (1 + text.Count(c => c == '\n')) * 20);
        Fill(r, CBg, 8f);
        Fill(new Rect(r.x, r.y, 3, r.height), CAccent, new Vector4(8, 0, 0, 8));
        GUI.Label(new Rect(r.x + 12, r.y + 8, r.width - 20, r.height - 10), text, sHud);
    }


    static void DrawGeneral()
    {
        Sec("Grafik");
        Lbl($"API: <b>{SystemInfo.graphicsDeviceType}</b>   GPU: {SystemInfo.graphicsDeviceName}   VRAM: {SystemInfo.graphicsMemorySize} MB");
        Lbl($"Çözünürlük: {Screen.width}x{Screen.height}   Sahne: {SceneManager.GetActiveScene().name}");
        Lbl($"Bellek (Unity): {UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576} MB   FPS: {1f / smoothDt:0}");
        H();
        if (Btn(QualitySettings.vSyncCount > 0 ? "VSync: AÇIK" : "VSync: KAPALI")) QualitySettings.vSyncCount = QualitySettings.vSyncCount > 0 ? 0 : 1;
        Lbl($"FPS limiti: {(Application.targetFrameRate <= 0 ? "yok" : Application.targetFrameRate.ToString())}", 120);
        foreach (var f in new[] { 30, 60, 144, -1 }) if (Btn(f < 0 ? "Yok" : f.ToString(), 50)) Application.targetFrameRate = f;
        EH();

        Space(8);
        Sec("Zaman ölçeği"); Lbl($"Şu an: {Time.timeScale:0.00}" + (PureClient ? "  (yalnız host değiştirir)" : "  (istemcilere de uygulanır)"));
        H();
        timeScale = Sld(timeScale, 0f, 5f, 420);
        if (Btn("Uygula", 70)) SetTimeScale(timeScale);
        EH();
        H();
        foreach (var v in new[] { 0f, 0.25f, 0.5f, 1f, 2f, 5f }) if (Btn(v + "x", 60)) SetTimeScale(timeScale = v);
        EH();
    }

    static void DrawNet()
    {
        if (!Online) { Lbl("Ağ oturumu yok (menüdesin)."); return; }
        var cfg = NM.NetworkConfig;
        Lbl($"Rol: <b>{Role}</b>   LocalClientId: {NM.LocalClientId}   Tick: {cfg.TickRate} Hz");
        Lbl($"Transport: {cfg.NetworkTransport.GetIl2CppType().Name}   Spawned: {snap.Count}");
        if (NM.IsServer)
        {
            allowClients = Tgl(allowClients, " İstemcilerin debug komutlarına izin ver (sadece güvendiğin kişilerle!)");
        }
        else
        {
            Lbl($"Host ping: {Rtt(NetworkManager.ServerClientId)} ms");
            Lbl($"Host'ta debug mod: <b>{hostMod}</b>");
            if (Btn("Host'u tekrar yokla")) Send(NetworkManager.ServerClientId, "ping");
        }

        Space(6);
        Sec("Oyuncular");
        var me = LocalPlayer();
        foreach (var n in snap.Where(x => x.Player))
        {
            H();
            Lbl($"client {n.Owner}{(n.Owner == NM.LocalClientId ? " (sen)" : "")}  {n.Name}", 300);
            Lbl(NM.IsServer ? $"{Rtt(n.Owner)} ms" : "", 60);
            Lbl(me != null ? $"{Vector3.Distance(me.transform.position, n.Pos):0} m" : "", 70);
            if (n.Owner != NM.LocalClientId && Btn("Git", 50)) Teleport(n.Pos + Vector3.up);
            EH();
        }

        Space(6);
        Sec("Ağ nesneleri"); H(); Lbl("Filtre:", 60); objFilter = Txt(objFilter, 200, "objFilter"); EH();
        int shown = 0;
        foreach (var n in snap)
        {
            if (objFilter.Length > 0 && n.Name.IndexOf(objFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (++shown > 60) { Lbl("… (filtrele)"); break; }
            H();
            Lbl($"#{n.Id} {n.Name}", 330);
            Lbl($"owner {n.Owner}", 80);
            if (Btn("İncele", 60) && n.No != null) InspectAll(n.No.gameObject);
            if (Btn("Git", 40)) Teleport(n.Pos + Vector3.up * 2f);
            EH();
        }
    }

    static void DrawPlayer()
    {
        var p = LocalPlayer();
        Lbl(p == null ? "Oyuncu yok" : $"Oyuncu: {p.name}   Konum: {V(p.transform.position)}");
        if (PureClient) Lbl("<i>İstemcide ışınlanma hem yerelde hem host üzerinden uygulanır.</i>");
        H();
        if (Btn(fly ? "Uçma: AÇIK (F10)" : "Uçma: KAPALI (F10)")) SetFly(!fly);
        Lbl($"Hız {flySpeed:0}", 70);
        flySpeed = Sld(flySpeed, 1f, 60f);
        EH();

        Space(6);
        Sec("Konum slotları");
        for (int i = 0; i < slots.Length; i++)
        {
            H();
            Lbl($"Slot {i + 1}: {(slots[i].HasValue ? V(slots[i].Value) : "boş")}", 260);
            if (Btn("Kaydet", 70) && p != null) slots[i] = p.transform.position;
            if (Btn("Git", 50) && slots[i].HasValue) Teleport(slots[i].Value);
            EH();
        }

        Space(6);
        Sec("Nişangah (+) altındaki nesne");
        if (!hasHit || hit.collider == null) { Lbl("— hiçbir şey —"); return; }
        var go = hit.collider.gameObject;
        var no = go.GetComponentInParent<NetworkObject>();
        Lbl($"{go.name}   {hit.distance:0.0} m   nokta {V(hit.point)}");
        Lbl($"Yol: {PathOf(go.transform)}");
        Lbl(no != null ? $"NetworkObject #{no.NetworkObjectId}  owner {no.OwnerClientId}  {no.name}" : "Ağ nesnesi değil (yerel)");
        H();
        if (Btn("Buraya ışınlan")) Teleport(hit.point + hit.normal * 0.5f + Vector3.up * 0.2f);
        if (Btn("Tüm bileşenleri incele")) InspectAll(no != null ? no.gameObject : go);
        EH();
        Lbl("Bileşenler:");
        foreach (var c in go.GetComponents<Component>())
        {
            H(); Lbl("  " + c.GetIl2CppType().FullName, 460); if (Btn("İncele", 70)) AddTarget(c); EH();
        }
    }

    static string PathOf(Transform t)
    {
        var parts = new List<string>();
        for (; t != null && parts.Count < 8; t = t.parent) parts.Add(t.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    static void DrawInspector()
    {
        Sec("Hızlı bul");
        H();
        foreach (var n in Presets)
            if (Btn(n))
            {
                var t = IType.GetType(n + ", Assembly-CSharp");
                if (t == null) Toast(n + " tipi yok"); else FindInstances(t);
            }
        EH();
        H(); Lbl("Tip ara:", 70); typeQuery = Txt(typeQuery, 260, "typeQuery"); if (Btn("Ara", 60)) SearchTypes(); if (Btn("Temizle", 70)) { targets.Clear(); typeResults.Clear(); } EH();
        foreach (var t in typeResults) { if (Btn(t.FullName)) FindInstances(t); }

        if (PureClient) Lbl("<i>İstemcisin: ağ nesnelerindeki değişiklikler host'a gönderilir (host'ta mod + izin gerekir). NetworkVariable alanları herkese senkronlanır.</i>");
        else Lbl("<i>Host'sun: değişiklikler doğrudan uygulanır. NetworkVariable alanları istemcilere senkronlanır.</i>");

        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            Space(4);
            H();
            t.Open = Tgl(t.Open, $" <b>{t.Label}</b>");
            if (Btn("X", 30)) { targets.RemoveAt(i--); EH(); continue; }
            EH();
            if (!t.Open) continue;

            foreach (var e in Fields(t.C))
            {
                H();
                Lbl($"  {e.Name}{(e.IsNV ? " <color=cyan>[net]</color>" : "")}", 250);
                Lbl(vals.TryGetValue(t.Key + e.Name, out var v) ? v : "…", 160);
                if (e.Editable)
                {
                    var k = t.Key + e.Name;
                    edits.TryGetValue(k, out var cur);
                    edits[k] = Txt(cur, 110, k);
                    if (Btn("Ayarla", 60)) SetFieldUI(t, e.Name, edits[k]);
                }
                EH();
            }

            t.ShowMethods = Tgl(t.ShowMethods, "  Parametresiz metotlar (dikkat: oyun mantığını doğrudan çağırır)");
            if (!t.ShowMethods) continue;
            var ms = Methods(t.C);
            for (int j = 0; j < ms.Count; j += 3)
            {
                H();
                for (int k = j; k < Math.Min(j + 3, ms.Count); k++) if (Btn(ms[k], 210)) CallUI(t, ms[k]);
                EH();
            }
        }
    }

    static void DrawConsole()
    {
        H();
        errorsOnly = Tgl(errorsOnly, " Sadece hatalar");
        if (Btn("Temizle", 80)) { logs.Clear(); errorCount = 0; }
        if (Btn("Dosyaya kaydet", 120))
        {
            var path = Path.Combine(Paths.BepInExRootPath, "cstdebug_log.txt");
            File.WriteAllLines(path, logs.Select(l => $"[{l.type}] {l.text}"));
            Toast("Kaydedildi: " + path);
        }
        EH();
        // Layout ile Repaint arasında gelen log kontrol sayısını değiştirmesin
        if (Event.current.type == EventType.Layout) consoleCount = logs.Count;
        for (int i = Math.Min(consoleCount, logs.Count) - 1; i >= 0; i--)   // en yeni üstte
        {
            var (type, text) = logs[i];
            bool err = type is LogType.Error or LogType.Exception or LogType.Assert;
            if (errorsOnly && !err) continue;
            var col = err ? "#ff6060" : type == LogType.Warning ? "#ffd060" : "#dddddd";
            Lbl($"<color={col}>{text.Replace('<', '‹')}</color>");   // log içindeki etiketler rich text'i bozmasın
        }
    }
}
