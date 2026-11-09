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
        L.LogInfo("CST Debug Mode yüklendi — F8 menü, F10 uçma");
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
    static readonly string[] Tabs = { "Test", "Genel", "Ağ", "Oyuncu", "İnceleyici", "Konsol" };
    static readonly string[] Presets = { "CarServiceManager", "JobDispatchManager", "ServiceRequestSystem", "CSTNetworkManager" };
    static readonly HashSet<string> SkipMethods = new()
        { "Awake", "Start", "Update", "LateUpdate", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy", "OnNetworkSpawn", "OnNetworkDespawn", "OnGUI" };
    const BF Decl = BF.Instance | BF.Public | BF.NonPublic | BF.DeclaredOnly;
    const BF Any = BF.Instance | BF.Public | BF.NonPublic;

    static bool inited, menu, hud, fly, allowClients, errorsOnly;
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
        if (pendingDeliver.Count > 0) PumpDeliveries();
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.f8Key.wasPressedThisFrame) ToggleMenu();
            if (kb.f10Key.wasPressedThisFrame) SetFly(!fly);
        }
        EnsureMessaging();
        if (!menu) return;
        BlockGameInput();
        targets.RemoveAll(t => t.C == null);   // OnGUI'de silmek IMGUI kontrol sayısını bozar
        if ((snapTimer -= Time.unscaledDeltaTime) <= 0f) { snapTimer = 0.5f; Snapshot(); RefreshValues(); if (tab == 0) RefreshWorld(); }
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
        SetPlayerInput(!menu);
        if (!menu) RestoreGameInput();
    }

    // Menü açıkken oyunun bütün InputAction'ları kapalı: yürüme/bakış, etkileşim tuşları, arkadaki oyun butonları.
    // F8/F10 klavye cihazından doğrudan okunduğu için etkilenmez; IMGUI olayları da action'lardan bağımsız.
    static bool inputLocked;
    static readonly List<UnityEngine.InputSystem.InputAction> disabledActions = new();

    static void BlockGameInput()   // her karede: oyun bir action'ı yeniden açarsa tekrar kapat
    {
        try
        {
            var list = UnityEngine.InputSystem.InputSystem.ListEnabledActions();
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a == null) continue;
                a.Disable();
                if (!disabledActions.Any(x => x.Pointer == a.Pointer)) disabledActions.Add(a);
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Girdi kapatma: " + e.Message); }
    }

    static void RestoreGameInput()
    {
        foreach (var a in disabledActions) { try { if (a != null && !a.enabled) a.Enable(); } catch { } }
        disabledActions.Clear();
    }

    static void SetPlayerInput(bool active)
    {
        var sp = LocalPlayer()?.GetComponentInChildren<SinglePlayer>(true);
        if (sp == null) { inputLocked = false; return; }
        sp.SetMovementInputActive(active, !active);
        inputLocked = !active;
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
            case "game" when p.Length >= 3:
                return GameCmd(p[1], p[2]);
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
        tab = 4;
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
    static BepInEx.Configuration.ConfigEntry<bool> hudCfg;

    internal static void BindConfig(BepInEx.Configuration.ConfigFile cfg)
    {
        hudCfg = cfg.Bind("Menu", "HUD", false, "Sağ üstte FPS/ping/konum paneli (menüden açılıp kapatılır)");
        hud = hudCfg.Value;
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
        else if (cx > lx && cx + w > lx + lw) { cy += rowH + Gap; cx = lx; rowH = LineH; }   // sığmazsa alt satıra sar
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
                case 0: DrawTest(); break;
                case 1: DrawGeneral(); break;
                case 2: DrawNet(); break;
                case 3: DrawPlayer(); break;
                case 4: DrawInspector(); break;
                case 5: DrawConsole(); break;
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


    // ───────────────────────── oyun testleri ─────────────────────────
    // Oyunun kendi server metotları; host'ta çalışır, istemci "game" komutuyla host'a yollar.

    static PlayersInventory invCache;
    static DayNightCycle clockCache;
    static List<ScriptableCarPartItemBase> items;
    static string moneyIn = "", repIn = "", itemQuery = "";
    static int itemCount = 1;

    static T FindFirst<T>(ref T cache) where T : UnityEngine.Object
    {
        if (cache != null) return cache;
        var a = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<T>(), FindObjectsSortMode.None);
        return cache = a.Length > 0 ? a[0].TryCast<T>() : null;
    }

    static PlayersInventory Inv => FindFirst(ref invCache);
    static DayNightCycle Clock => FindFirst(ref clockCache);

    static List<ScriptableCarPartItemBase> Items()
    {
        if (items is { Count: > 0 }) return items;
        items = new List<ScriptableCarPartItemBase>();
        itemHash.Clear();
        var all = ScriptableCarPartItemBase.All;
        if (all != null)
            foreach (var key in all.Keys)   // anahtar = InventoryItem.partHashes'e yazılan eşya hash'i
            {
                var v = all[key];
                if (v == null) continue;
                items.Add(v);
                itemHash[v.Pointer] = key;
            }
        items.Sort((a, b) => string.Compare(a.GetName, b.GetName, StringComparison.CurrentCultureIgnoreCase));
        return items;
    }

    // Parça sınıfı adı → envanter kategorisi (ScriptableCarPartBattery → CarPartNames.Battery); adları farklı olanlar elle.
    static readonly Dictionary<string, string> PartAlias = new()
        { ["CoolingWater"] = "CoolingRadiatorWater", ["BrakeFluid"] = "BrakeSystemFluid", ["WindshieldWiperFluid"] = "WindscreenWasherFluid" };

    static CarPartNames? PartOf(ScriptableCarPartItemBase item)
    {
        var n = item.GetIl2CppType().Name.Replace("ScriptableCarPart", "");
        if (PartAlias.TryGetValue(n, out var a)) n = a;
        return Enum.TryParse<CarPartNames>(n, true, out var pn) ? pn : null;   // Fuse gibi birden çok kategoriye düşenler: null
    }

    static long Num(string s) => long.Parse(s.Trim().Replace(".", "").Replace(",", "").Replace(" ", ""), CultureInfo.InvariantCulture);

    static string GameCmd(string op, string arg)
    {
        var inv = Inv;
        switch (op)
        {
            case "addmoney" or "setmoney" or "addrep" or "setrep" when inv == null:
                return "PlayersInventory yok (oyun sahnesinde olmalısın)";
            case "addmoney": inv.AddMoney(Num(arg)); return $"Para: {inv.GetMoney:N0}";
            case "setmoney": inv.SetMoney(Num(arg)); return $"Para: {inv.GetMoney:N0}";
            case "addrep": inv.IncreaseReputationPoint((int)Num(arg)); return $"İtibar: {inv.GetReputationPoints}";
            case "setrep": inv.SetReputationPoint((int)Num(arg)); return $"İtibar: {inv.GetReputationPoints}";
            case "spawn":
                var jd = JobDispatchManager.Instance;
                if (jd == null) return "JobDispatchManager yok";
                jd.SpawnVehicleRequest();
                return "Yeni araç isteği oluşturuldu";
            case "time":
            {
                var c = Clock;
                if (c == null) return "DayNightCycle yok";
                var t = arg.Split(',');
                c.SetCurrentDay((int)Num(t[0]), F(t[1]));
                return $"Gün {t[0]}, saat {t[1]}:00";
            }
            case "item":
            {
                if (inv == null) return "PlayersInventory yok";
                var t = arg.Split(',');
                int idx = (int)Num(t[0]), n = Math.Clamp((int)Num(t[1]), 1, 50);
                var item = Items().FirstOrDefault(i => i.ItemArrayIndex == idx);
                if (item == null) return "parça bulunamadı: " + idx;
                var part = PartOf(item);
                if (part == null) return item.GetName + ": kategori eşlenemedi";
                for (int i = 0; i < n; i++) inv.AddItemToInventory(part.Value, item);
                return $"{n}x {item.GetName} envantere eklendi";
            }
            case "repair":
            {
                // arg: "id" (hepsi) veya "id;0,2,999" (seçilenler)
                var parts = arg.Split(';');
                var car = CarById(parts[0]);
                HashSet<int> only = null;
                if (parts.Length > 1) only = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => (int)Num(s)).ToHashSet();
                return car == null ? "araç bulunamadı" : $"{CarLabel(car)}: {RepairCar(car, only)}";
            }
            case "dump":
            {
                var car = CarById(arg);
                return car == null ? "araç bulunamadı" : DumpCar(car);
            }
            case "fastcargo":
                return SetFastCargo(arg == "1");
            case "deliver":
            {
                var car = CarById(arg);
                return car == null ? "araç bulunamadı" : $"{CarLabel(car)}: {Deliver(car)}";
            }
            case "removecar":
            {
                var car = CarById(arg);
                return car == null ? "araç bulunamadı" : RemoveCar(car);
            }
        }
        return "bilinmeyen test komutu: " + op;
    }

    static void Game(string op, string arg)
    {
        if (PureClient) { Send(NetworkManager.ServerClientId, $"game\t{op}\t{Clean(arg)}"); Toast("Host'a gönderildi"); }
        else Toast(SafeRun(() => GameCmd(op, arg)));
    }

    // ── araçlar ──
    static readonly List<Car> cars = new();
    static readonly List<ABuildingSlot> slots2 = new();
    static readonly HashSet<ulong> carOpen = new();
    // shortcut: seçim IssueTargets sırasına göre indeks; host ve istemcide "ek arızalar" seçeneği farklıysa sıra kayabilir
    static readonly Dictionary<ulong, HashSet<int>> repairSel = new();

    static void RefreshWorld()
    {
        cars.Clear();
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<Car>(), FindObjectsSortMode.None)) { var c = o.TryCast<Car>(); if (c != null) cars.Add(c); }
        carDone.Clear();
        foreach (var c in cars) if (IsCarDone(c)) carDone.Add(c.NetworkObjectId);
        slots2.Clear();
        foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppType.Of<ABuildingSlot>(), FindObjectsSortMode.None)) { var s = o.TryCast<ABuildingSlot>(); if (s != null) slots2.Add(s); }
    }

    static IEnumerable<(ACarPartNodeProcess Proc, CarPartNodeController Ctrl)> Controllers(Car car)
    {
        foreach (var proc in car.GetComponentsInChildren<ACarPartNodeProcess>(true))
        {
            var ctrls = proc.GetCarPartControllers();
            if (ctrls == null) continue;
            foreach (var c in ctrls) if (c != null) yield return (proc, c);
        }
    }

    static readonly Dictionary<IntPtr, int> itemHash = new();

    static int PartCount(CarPartNodeController ctrl)
    {
        var seen = new HashSet<IntPtr>();
        for (int i = 0; i < 16; i++)
        {
            CarPart p;
            try { p = ctrl.GetCarPartByIndex(i); } catch { break; }
            if (p == null || !seen.Add(p.Pointer)) break;   // index taşınca aynı parçayı döndürüyor
        }
        return Math.Max(1, seen.Count);
    }

    // Parça değişimiyle çözülmeyenler (kaporta/yıkama ayrı ele alınır; pas çözücü sarf malzemesi)
    static readonly HashSet<CarPartNames> NotSwappable = new()
        { CarPartNames.NotImportant, CarPartNames.BodyWork, CarPartNames.CarWash, CarPartNames.BoltLoosener, CarPartNames.Engine };

    // Senaryodaki arızalar → yalnızca arızanın affectedPart türündeki kontrol noktaları.
    // (issue.GetControllers bütün zinciri döndürüyor: tekerde göbek/balata/disk… Hepsine dokunmak "ekstra parça" bedeli yazar.)
    // Müşteri kuralları (tablette "EKSTRA TAMİRAT YOK" vb.). Onar parça seçimini ve ek arızaları bunlara göre yapar.
    static HashSet<ServiceRequestCustomerRules> RulesOf(ScriptableCarServiceScenario scen)
    {
        var set = new HashSet<ServiceRequestCustomerRules>();
        try { if (scen?.customerRules != null) foreach (var r in scen.customerRules) if (r != null) set.Add(r.rule); } catch { }
        return set;
    }
    static HashSet<ServiceRequestCustomerRules> curRules;   // RepairCar süresince Candidates'in uyguladığı kurallar

    static bool repairExtras;   // senaryonun müşteri istemediği ek arızalarını da onar
    static readonly HashSet<CarPartNames> Liquids = new() { CarPartNames.EngineOil, CarPartNames.BrakeSystemFluid, CarPartNames.PowerSteeringFluid, CarPartNames.CoolingRadiatorWater, CarPartNames.WindscreenWasherFluid, CarPartNames.GearOil };

    static List<(CarPartNodeController Ctrl, CarPartNames Part, string Issue, bool Change, ScriptableCarIssue Src, AScriptableServiceRequest Req)> IssueTargets(Car car, ScriptableCarServiceScenario scen)
    {
        var res = new List<(CarPartNodeController, CarPartNames, string, bool, ScriptableCarIssue, AScriptableServiceRequest)>();
        var seen = new HashSet<IntPtr>();
        void AddIssue(ScriptableCarIssue issue, bool change, AScriptableServiceRequest req)
        {
            if (issue == null) return;
            CarPartNames ap;
            try { ap = issue.affectedPart; } catch { return; }
            if (NotSwappable.Contains(ap)) return;
            Il2CppSystem.Collections.Generic.List<CarPartNodeController> cs = null;
            try { cs = issue.GetControllers(car); } catch { }
            if (cs == null) return;
            foreach (var c in cs)
            {
                if (c == null || !seen.Add(c.Pointer)) continue;
                CarPartNames pn;
                try { pn = c.GetPrimaryCarPart().GetCarPartName(); } catch { continue; }
                // lastik arızası (affectedPart=Tire) teker noktasında: jant+lastik tek eşya (Döküm: RightBackTireIssue → wheelset:Wheel)
                if (pn == ap || (ap == CarPartNames.Tire && pn == CarPartNames.Wheel)) res.Add((c, pn, issue.name, change && !Liquids.Contains(pn), issue, req));   // sıvı her zaman doldurulur, değiştirilmez
            }
        }
        if (scen.serviceRequests != null)
            foreach (var req in scen.serviceRequests)
                {
                    if (req?.carIssues == null) continue;
                    bool change = true;
                    try { change = req.NeedChangeCarPart(); } catch { }   // şarj/dolum isteklerinde parça değiştirmek "yanlış işlem"
                    foreach (var issue in req.carIssues) AddIssue(issue, change, req);
                }
        bool called = false; try { called = car.GetIsCustomerCalled; } catch { }
        bool noExtras = RulesOf(scen).Contains(ServiceRequestCustomerRules.NoExtraFixes);   // müşteri ek tamir istemiyor → bedel yazılır
        if ((repairExtras || called) && !noExtras && scen.extraIssues != null) foreach (var issue in scen.extraIssues) AddIssue(issue, !(issue?.name ?? "").Contains("Charge", StringComparison.OrdinalIgnoreCase), null);   // isteği olmayan ek arıza: "Charge…" şarj, gerisi değişim
        return res;
    }

    static InventoryItem CurrentItem(CarPartNodeController ctrl)
    {
        try { return ctrl.GetPrimaryCarPart().carPartItem.Value; } catch { return null; }
    }

    static int CurrentHash(CarPartNodeController ctrl)
    {
        var it = CurrentItem(ctrl);
        try { return it != null ? InventoryItemExtensions.GetHashSafe(it) : 0; } catch { return 0; }
    }

    // Oyunun kendi ölçümü: isteğin kalite skoru (AScriptableServiceRequest.Validate, 0..1).
    // (Fixed() ValueTuple döndürüyor; interop onu bozuk okuyor.)
    static float? ReqScore(Car car, AScriptableServiceRequest r)
    {
        try { return r.Validate(car, car.CarServiceScenario?.customerRules); } catch { return null; }
    }

    static string ReqStatus(Car car, AScriptableServiceRequest r)
    {
        var s = ReqScore(car, r);
        if (s == null) return "<color=#9aa0a6>?</color>";
        var col = s >= 0.999f ? "#5fd068" : s > 0f ? "#ffd060" : "#ff6060";
        return $"<color={col}>%{s * 100:0}</color>";
    }

    static string ReqSummary(Car car, ScriptableCarServiceScenario scen)
    {
        if (scen.serviceRequests == null) return "istek yok";
        var parts = new List<string>();
        foreach (var r in scen.serviceRequests)
            if (r != null) { var s = ReqScore(car, r); parts.Add($"{r.name} {(s == null ? "?" : $"%{s * 100:0}")}"); }
        return string.Join(", ", parts);
    }

    static string ItemLabel(CarPartNodeController ctrl)
    {
        var it = CurrentItem(ctrl);
        if (it == null) return "boş";
        int h; float d;
        try { h = InventoryItemExtensions.GetHashSafe(it); d = InventoryItemExtensions.GetNormalizedDurability(it); } catch { return "?"; }
        Items();
        var item = items.FirstOrDefault(i => itemHash.TryGetValue(i.Pointer, out var x) && x == h);
        return $"{(item != null ? item.GetName : (h == 0 ? "boş" : "#" + h))}  %{d * 100:0}";
    }

    // Kontrol noktasına uyan eşyalar: en yüksek nadirlik, sonra en pahalı önce
    static List<ScriptableCarPartItemBase> Candidates(CarPartNodeController ctrl, CarPartNames pn, int oldHash = 0)
    {
        var ids = ctrl.compatibilityIds;
        bool fuse = pn is CarPartNames.FuseSmall or CarPartNames.FuseMid or CarPartNames.FuseLarge;   // katalogda tek "Fuse" sınıfı; boyu uyumluluk seçer
        var list = Items().Where(i => (fuse ? i.GetIl2CppType().Name == "ScriptableCarPartFuse" : PartOf(i) == pn) && itemHash.ContainsKey(i.Pointer)
                && (ids == null || ids.Count == 0 || ids.Contains(i.compatibilityId)))
            .OrderByDescending(i => (int)i.rarity).ThenByDescending(i => i.GetBuyPrice).ToList();
        if (curRules != null)
        {
            bool Orig(ScriptableCarPartItemBase i) { try { return i.brandData != null && i.brandData.GetIsOriginal; } catch { return false; } }
            IEnumerable<ScriptableCarPartItemBase> f = list;
            if (curRules.Contains(ServiceRequestCustomerRules.OriginalOnly)) f = f.Where(Orig);
            if (curRules.Contains(ServiceRequestCustomerRules.AftermarketOnly)) f = f.Where(i => !Orig(i));
            if (curRules.Contains(ServiceRequestCustomerRules.SameAsRemovedPart) && oldHash != 0) f = f.Where(i => itemHash[i.Pointer] == oldHash);
            var fl = f.ToList();
            if (fl.Count > 0) list = fl;
            else Plugin.L.LogWarning($"{pn}: müşteri kuralına ({string.Join(",", curRules)}) uyan eşya bulunamadı, kural dışı seçildi");
        }
        // Eski parçayla aynı uyumluluk + aynı özellik (ör. 70A akü, 15A sigorta) olanlar öncelikli; sigortada röle≠sigorta
        var oldDef = oldHash == 0 ? null : list.Concat(Items()).FirstOrDefault(i => itemHash.TryGetValue(i.Pointer, out var h) && h == oldHash);
        if (oldDef != null)
        {
            string spec(ScriptableCarPartItemBase i) { try { return i.GetIdentical ?? ""; } catch { return ""; } }
            var same = list.Where(i => i.compatibilityId == oldDef.compatibilityId && spec(i) == spec(oldDef)).ToList();
            if (same.Count == 0) same = list.Where(i => i.compatibilityId == oldDef.compatibilityId).ToList();
            if (same.Count > 0) return same;
        }
        return list;
    }

    static bool ValidItem(InventoryItem it)
    {
        try { return it != null && it.ItemType != CarPartNames.NotImportant && InventoryItemExtensions.GetHashSafe(it) != -1; }
        catch { return false; }
    }

    // UYARI: NetworkVariable<InventoryItem>.Value'ya interop üzerinden YAZMAK belleği bozuyor (Döküm: çöp owner/sbi).
    // Bu yüzden eşya verisi yalnızca oyunun kendi metotlarıyla yazılır (InitializeWithData, envanter metotları).
    static void Install(CarPartNodeController ctrl, CarPartNames pn, ScriptableCarPartItemBase item, int count)
    {
        var p = ctrl.GetPrimaryCarPart();
        InventoryItem old = null;
        try { old = p.carPartItem.Value; } catch { }
        int hash = itemHash[item.Pointer];
        var pinv = Inv;

        // 1) Yeni eşyayı kendimiz kur. (Envanter listesinden List<InventoryItem>[i] ile okumak interop'ta çöp veri
        //    döndürüyor: Döküm'de tür NotImportant, rastgele dayanıklılık → oyun skoru her basışta zıplıyordu.)
        var neu = InventoryItem.empty;
        neu.ItemType = pn;
        try { neu.ItemOwner = OwnerName(); } catch { }
        neu.sessionBasedIndex = nextSbi++;
        for (int i = 0; i < count; i++) InventoryItemExtensions.AddPart(ref neu, hash, ScriptableCarPartItemBase.MaxDurability);

        // 2) "son sökülen" = eski parça — oyunun kendi yazımıyla (setLastRemoved=true geçilen eşyayı last yapar)
        if (ValidItem(old)) ctrl.InitializeWithData(old, true);
        // 3) yeni parçayı tak (last'a dokunma)
        ctrl.InitializeWithData(neu, false);
        // 5) sökülen eski parça envantere
        if (ValidItem(old) && pinv != null)
        {
            try { pinv.AddCarPartItemWithCarPartName(pn, old, OwnerName()); Plugin.L.LogInfo($"Envantere: sökülen {pn}"); }
            catch (Exception e) { Plugin.L.LogWarning("Envantere ekleme: " + e.Message); }
        }
        try { if (!p.isInstalled.Value) p.isInstalled.Value = true; } catch (Exception e) { Plugin.L.LogWarning("isInstalled: " + e.Message); }
        try { p.OnInstalledServer(); } catch (Exception e) { Plugin.L.LogWarning("OnInstalledServer: " + e.Message); }
        try { ctrl.ItemInstalled(); } catch (Exception e) { Plugin.L.LogWarning("ItemInstalled: " + e.Message); }
    }

    static int nextSbi = 900000 + new System.Random().Next(50000);

    static List<int> BrokenSlots(ScriptableCarIssue iss)
    {
        var res = new List<int>();
        try
        {
            var l = iss?.TryCast<ScriptableFuseSmallIssue>()?.brokenIndexes ?? iss?.TryCast<ScriptableFuseMidIssue>()?.brokenIndexes ?? iss?.TryCast<ScriptableFuseLargeIssue>()?.brokenIndexes;
            if (l != null) for (int i = 0; i < l.Count; i++) res.Add(l[i]);
        }
        catch { }
        return res;
    }

    static Func<ScriptableCarPartItemBase, bool> ReqFilter(AScriptableServiceRequest req)
    {
        if (req == null) return null;
        try
        {
            var w = req.TryCast<ScriptableServiceRequestSpacerWidthX>();
            if (w != null) { int n = w.needSpacerWidth; return i => i.TryCast<ScriptableCarPartSpacer>()?.GetSpacerWidth == n; }
            var c = req.TryCast<ScriptableServiceRequestSpacerCountX>();
            if (c != null) { int n = c.needSpacerCount; return i => i.TryCast<ScriptableCarPartSpacer>()?.GetSpacerCount == n; }
            var sp = req.TryCast<ScriptableServiceRequestSpringTypeX>();
            if (sp != null) { var t = sp.springType; return i => i.TryCast<ScriptableCarPartSpring>()?.springType == t; }
            var tt = req.TryCast<ScriptableServiceRequestChangeTireType>();
            if (tt != null) { var t = tt.needTireType; return i => i.TryCast<ScriptableCarPartTire>()?.tireType == t; }
            var rm = req.TryCast<ScriptableServiceRequestIncreaseRarityMinX>();
            if (rm != null) { var r = rm.needMinRarity; return i => (int)i.rarity >= (int)r; }
        }
        catch { }
        return null;
    }

    // Boya isteği: istenen renk istekte saklı değil (yalnızca parçalar); oyunun kendi Validate'i ile renk aranır.
    // Önce addaki renk tahmini, sonra ton/doygunluk/parlaklık taraması. Bulunamazsa eski renk geri yüklenir.
    static readonly (string Name, float H, float S, float V)[] ColorGuess =
    {
        ("Red", 0f, 1f, .8f), ("Orange", .08f, 1f, .9f), ("Yellow", .16f, 1f, .9f), ("Green", .33f, 1f, .7f), ("Cyan", .5f, 1f, .8f),
        ("Blue", .63f, 1f, .7f), ("Purple", .77f, .8f, .6f), ("Pink", .9f, .6f, .9f), ("White", 0f, 0f, 1f), ("Black", 0f, 0f, .05f),
        ("Grey", 0f, 0f, .5f), ("Gray", 0f, 0f, .5f), ("Silver", 0f, 0f, .75f), ("Brown", .07f, .7f, .4f),
    };
    static string PaintRequest(Car car, CarBodyLayerController layer, ScriptablePaintBodyPartServiceRequest pr)
    {
        if ((ReqScore(car, pr) ?? 0f) >= 0.999f) return null;
        BodyPartTypes parts = BodyPartTypes.None;
        try { parts = pr.GetRequiredPaint(layer.GetCarVisualData); } catch { }
        if (parts == BodyPartTypes.None) try { parts = pr.requiredPaint; } catch { }
        var orig = layer.GetPaintData();
        Plugin.L.LogInfo($"Boya: {pr.name} parçalar={parts} mevcut hsv=({orig.hue:0.###},{orig.saturation:0.###},{orig.brightness:0.###})");
        layer.SetRustyParts(layer.GetRustyParts & ~parts);
        layer.AddGrindParts(parts);
        layer.AddPaintedParts(parts);
        float scale = orig.hue > 1.01f ? 360f : 1f;   // ton 0-1 mi 0-360 mı, mevcut değerden anla
        bool Try(float h, float sat, float v)
        {
            var pd = orig; pd.hue = h * scale; pd.saturation = sat; pd.brightness = v;
            layer.SetPaintData(pd);
            return (ReqScore(car, pr) ?? 0f) >= 0.999f;
        }
        if ((ReqScore(car, pr) ?? 0f) >= 0.999f) return $"{parts} boyandı (mevcut renk uydu)";
        foreach (var g in ColorGuess)
            if (pr.name.Contains(g.Name, StringComparison.OrdinalIgnoreCase) && Try(g.H, g.S, g.V)) return $"{parts} boyandı ({g.Name})";
        foreach (var v in new[] { .9f, .7f, .5f, .3f, 1f, .1f })
            foreach (var sat in new[] { 1f, .75f, .5f, .25f, 0f })
                for (int h = 0; h < 48; h++)
                    if (Try(h / 48f, sat, v)) return $"{parts} boyandı (hsv {h / 48f:0.##},{sat},{v})";
        layer.SetPaintData(orig);
        throw new Exception($"{pr.name}: uyan renk bulunamadı, eski renk geri yüklendi");
    }

    static string B(Func<bool> f) { try { return f() ? "T" : "f"; } catch { return "?"; } }

    // null = canlı liste okunamadı
    static List<int> LiveBrokenFuses(CarPartNodeControllerFuse fc, CarPartNames pn)
    {
        try
        {
            var pr = fc?.fuseboxProcess;
            if (pr == null) return null;
            var nl = pn == CarPartNames.FuseSmall ? pr.BrokenSmallFuseIndexes : pn == CarPartNames.FuseMid ? pr.BrokenMidFuseIndexes : pr.BrokenLargeFuseIndexes;
            if (nl == null) return null;
            var res = new List<int>();
            for (int i = 0; i < nl.Count; i++) res.Add(nl[i]);
            return res;
        }
        catch { return null; }
    }

    // Çok yuvalı noktalar (sigorta kutusu vb.): bütün noktayı başlatmak tutmazsa en düşük dayanıklı yuvayı tek değiştir.
    static (int Slot, InventoryItem Item, float Dur) WorstSlot(CarPartNodeController ctrl, CarPartNames? type = null)
    {
        int n = PartCount(ctrl), worst = -1;
        float worstDur = 2f;
        InventoryItem worstItem = null;
        for (int i = 0; i < n; i++)
        {
            try
            {
                var it = ctrl.GetCarPartByIndex(i).carPartItem.Value;
                if (!ValidItem(it)) continue;
                if (type.HasValue && it.ItemType != type.Value) continue;   // sigorta kutusunda röle/diğer boy yuvaları atla
                float d = InventoryItemExtensions.GetNormalizedDurability(it);
                if (d < worstDur) { worstDur = d; worst = i; worstItem = it; }
            }
            catch { }
        }
        return (worst, worstItem, worstDur);
    }

    static bool InstallBrokenSlot(CarPartNodeController ctrl, CarPartNames pn, ScriptableCarPartItemBase item)
    {
        var (worst, worstItem, worstDur) = WorstSlot(ctrl);
        if (worst < 0 || worstDur >= 0.999f) return false;
        InstallSlot(ctrl, pn, item, worst, worstItem);
        return true;
    }

    // Tekerlek: eşyadaki parçaları (jant, lastik) hash'ten tanı; değişimde lastiği en iyi uyumlu lastikle değiştir, jantı koru.
    // Değişim istenmiyorsa (ör. hava basıncı/onarım) sadece dayanıklılığı doldur. Hiçbir şey gerekmiyorsa null döner.
    static string RepairWheel(CarPartNodeController ctrl, bool change, Func<ScriptableCarPartItemBase, bool> need = null)
    {
        var old = CurrentItem(ctrl);
        if (!ValidItem(old)) return null;
        var hashes = new List<int>();
        for (int i = 0; i < 8; i++)
        {
            int h;
            try { h = InventoryItemExtensions.GetHashSafe(old, i); } catch { break; }
            if (h == 0 || h == -1) break;
            hashes.Add(h);
        }
        if (hashes.Count == 0) return null;
        float dur = 1f;
        try { dur = InventoryItemExtensions.GetNormalizedDurability(old); } catch { }
        Items();
        ScriptableCarPartItemBase Def(int h) => items.FirstOrDefault(i => itemHash.TryGetValue(i.Pointer, out var x) && x == h);
        int tireIdx = hashes.FindIndex(h => Def(h)?.GetIl2CppType().Name == "ScriptableCarPartTire");

        if (!change || tireIdx < 0)
        {
            if (dur >= 0.999f) return null;
            for (int i = 0; i < hashes.Count; i++) ctrl.ApplyDurabilityIssue(CarPartNames.Wheel, ScriptableCarPartItemBase.MaxDurability, false, i);
            return $"Wheel dayanıklılığı dolduruldu (%{dur * 100:0} → %100)";
        }

        var oldTire = Def(hashes[tireIdx]);
        var tires = Items().Where(i => i.GetIl2CppType().Name == "ScriptableCarPartTire" && itemHash.ContainsKey(i.Pointer)
                && (oldTire == null || i.compatibilityId == oldTire.compatibilityId)).ToList();
        if (need != null)
        {
            tires = Items().Where(i => i.GetIl2CppType().Name == "ScriptableCarPartTire" && itemHash.ContainsKey(i.Pointer) && need(i)).ToList();
            if (tires.Count == 0) throw new Exception("isteğe uyan lastik yok");
        }
        if (curRules != null)
        {
            bool Orig(ScriptableCarPartItemBase i) { try { return i.brandData != null && i.brandData.GetIsOriginal; } catch { return false; } }
            IEnumerable<ScriptableCarPartItemBase> f = tires;
            if (curRules.Contains(ServiceRequestCustomerRules.OriginalOnly)) f = f.Where(Orig);
            if (curRules.Contains(ServiceRequestCustomerRules.AftermarketOnly)) f = f.Where(i => !Orig(i));
            if (curRules.Contains(ServiceRequestCustomerRules.SameAsRemovedPart)) f = f.Where(i => itemHash[i.Pointer] == hashes[tireIdx]);
            var fl = f.ToList();
            if (fl.Count > 0) tires = fl;
        }
        var best = tires.OrderByDescending(i => (int)i.rarity).ThenByDescending(i => i.GetBuyPrice).FirstOrDefault();
        if (best == null) throw new Exception("uygun lastik yok");
        if (hashes[tireIdx] == itemHash[best.Pointer] && dur >= 0.999f) return null;

        var neu = InventoryItem.empty;
        neu.ItemType = CarPartNames.Wheel;
        try { neu.ItemOwner = OwnerName(); } catch { }
        neu.sessionBasedIndex = nextSbi++;
        for (int i = 0; i < hashes.Count; i++)
            InventoryItemExtensions.AddPart(ref neu, i == tireIdx ? itemHash[best.Pointer] : hashes[i], ScriptableCarPartItemBase.MaxDurability);
        ctrl.InitializeWithData(old, true);    // last = eski tekerlek (oyunun kendi yazımı)
        ctrl.InitializeWithData(neu, false);
        try { Inv?.AddCarPartItemWithCarPartName(CarPartNames.Wheel, old, OwnerName()); } catch { }
        return $"Wheel lastiği ← {best.GetName} ({best.rarity}), jant korundu";
    }

    static void InstallSlot(CarPartNodeController ctrl, CarPartNames pn, ScriptableCarPartItemBase item, int worst, InventoryItem worstItem)
    {
        var neu = InventoryItem.empty;
        neu.ItemType = pn;
        try { neu.ItemOwner = OwnerName(); } catch { }
        neu.sessionBasedIndex = nextSbi++;
        InventoryItemExtensions.AddPart(ref neu, itemHash[item.Pointer], ScriptableCarPartItemBase.MaxDurability);
        if (ValidItem(worstItem)) ctrl.InitializeWithDataAndIndex(worst, worstItem, true);   // last = eski bozuk (oyunun kendi yazımı)
        ctrl.InitializeWithDataAndIndex(worst, neu, false);
        try { if (ValidItem(worstItem)) Inv?.AddCarPartItemWithCarPartName(pn, worstItem, OwnerName()); } catch { }
    }

    // Önceki sürümlerin bıraktığı bozuk izi temizler: "takılı" bayrağı ve çöp/boş "son sökülen".
    // Yalnızca oyunun kendi yazım yolları kullanılır.
    static bool FixTrace(CarPartNodeController ctrl)
    {
        var p = ctrl.GetPrimaryCarPart();
        bool changed = false;
        try { if (!p.isInstalled.Value) { p.isInstalled.Value = true; changed = true; } } catch { }
        try
        {
            if (!ValidItem(p.lastRemovedCarPartItem.Value))
            {
                var cur = p.carPartItem.Value;
                if (ValidItem(cur)) { ctrl.InitializeWithData(cur, true); changed = true; Plugin.L.LogInfo("Bozuk 'son sökülen' kaydı temizlendi"); }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("FixTrace: " + e.Message); }
        return changed;
    }

    static string OwnerName()
    {
        try { return Steamworks.SteamFriends.GetPersonaName(); } catch { return "Player"; }
    }

    // Arızalı noktaların oyunun tuttuğu bütün izlerini log'a yazar: elle değişim ile Onar'ı karşılaştırmak için.
    static string DumpCar(Car car)
    {
        var scen = car.CarServiceScenario;
        if (scen == null) return "senaryo yok";
        string It(InventoryItem it)
        {
            if (it == null) return "null";
            try { return $"hash={InventoryItemExtensions.GetHashSafe(it)} dur={InventoryItemExtensions.GetNormalizedDurability(it):0.###} owner='{it.ItemOwner?.ToString()}' sbi={it.sessionBasedIndex} type={it.ItemType}"; }
            catch (Exception e) { return "? " + e.Message; }
        }
        Plugin.L.LogInfo($"── DÖKÜM {CarLabel(car)} (#{car.NetworkObjectId}) • müşteri arandı={B(() => car.GetIsCustomerCalled)} • kurallar={string.Join(",", RulesOf(scen))}");
        if (scen.serviceRequests != null)
            foreach (var r in scen.serviceRequests)
            {
                if (r == null) continue;
                Plugin.L.LogInfo($"   istek {r.name}: change={B(() => r.NeedChangeCarPart())} validate={ReqScore(car, r)}");
                if (r.carIssues == null) continue;
                foreach (var iss in r.carIssues)
                {
                    if (iss == null) continue;
                    var names = new List<string>();
                    try
                    {
                        var cs = iss.GetControllers(car);
                        if (cs != null) foreach (var c in cs) { try { names.Add($"{c.name}:{c.GetPrimaryCarPart()?.GetCarPartName()}"); } catch { names.Add(c?.name ?? "null"); } }
                    }
                    catch (Exception e) { names.Add("hata " + e.Message); }
                    string ap = "?"; try { ap = iss.affectedPart.ToString(); } catch { }
                    var bi = BrokenSlots(iss);
                    Plugin.L.LogInfo($"      arıza {iss.name} ({iss.GetIl2CppType().Name}) affectedPart={ap}{(bi.Count > 0 ? " yanık yuva=" + string.Join(",", bi) : "")} → {string.Join(", ", names)}");
                }
            }
        foreach (var (ctrl, pn, issue, change, src, req) in IssueTargets(car, scen))
        {
            var p = ctrl.GetPrimaryCarPart();
            string cur = "?", last = "?", inst = "?", t = "?";
            try { cur = It(p.carPartItem.Value); } catch { }
            try { last = It(p.lastRemovedCarPartItem.Value); } catch { }
            try { inst = p.isInstalled.Value ? "T" : "f"; } catch { }
            try { t = p.lastProcessFinishTime.Value.ToString("0.0"); } catch { }
            int slots = PartCount(ctrl);
            if (slots > 1)
                for (int si = 0; si < slots; si++)
                {
                    try
                    {
                        var it = ctrl.GetCarPartByIndex(si).carPartItem.Value;
                        int hh = InventoryItemExtensions.GetHashSafe(it);
                        var def = Items().FirstOrDefault(x => itemHash.TryGetValue(x.Pointer, out var v) && v == hh);
                        var fz = ctrl.GetCarPartByIndex(si).TryCast<CarPartFuse>();
                        string fs = fz == null ? "" : $" #{fz.Index} need={(fz.GetNeededRepair() ? "T" : "f")} rep={(fz.GetRepaired() ? "T" : "f")}";
                        Plugin.L.LogInfo($"      yuva {si}{fs}: {it.ItemType} {(def != null ? def.GetName + " [" + def.GetIdentical + "]" : "#" + hh)} dur={InventoryItemExtensions.GetNormalizedDurability(it):0.###}");
                    }
                    catch (Exception e) { Plugin.L.LogInfo($"      yuva {si}: ? {e.Message}"); }
                }
            var lf = LiveBrokenFuses(ctrl.TryCast<CarPartNodeControllerFuse>(), pn);
            if (lf != null) Plugin.L.LogInfo($"      kutu: yanık sigortalar=[{string.Join(",", lf)}]");
            Plugin.L.LogInfo($"   {pn} [{issue}] change={change} need={B(() => ServiceRequestSystem.GetNeededRepair(ctrl))} rep={B(() => ServiceRequestSystem.GetRepaired(ctrl))} "
                + $"liqNeed={B(() => ServiceRequestSystem.GetLiquidNeededRepair(ctrl))} liqRep={B(() => ServiceRequestSystem.GetLiquidRepaired(ctrl))} installed={inst} lastT={t}\n      cur: {cur}\n      last: {last}");
        }
        return "Döküm log'a yazıldı";
    }

    // Senaryodaki arızalı parçaları uyumlu en yüksek nadirlikteki eşyayla değiştirir. Aynı eşya zaten takılıysa dokunmaz
    // (tekrar basmak güvenli). Host'ta çalışır.
    // only: IssueTargets sırasındaki seçili indeksler (null = hepsi). BodyIdx = kaporta/yıkama.
    const int BodyIdx = 999;

    static string RepairCar(Car car, HashSet<int> only = null)
    {
        var scen = car.CarServiceScenario;
        if (scen == null) return "aracın senaryosu yok";
        Items();
        curRules = RulesOf(scen);
        try { return RepairCarInner(car, scen, only); }
        finally { curRules = null; }
    }

    static string RepairCarInner(Car car, ScriptableCarServiceScenario scen, HashSet<int> only)
    {
        int fixedN = 0, already = 0, idx = -1;
        var failed = new List<string>();
        foreach (var (ctrl, pn, issue, change, src, req) in IssueTargets(car, scen))
        {
            if (only != null && !only.Contains(++idx)) continue;
            if (!change)
            {
                // şarj / sıvı dolumu: mevcut parçayı koru, sadece dayanıklılığını (doluluğunu) tam yap
                float cur = 0f;
                try { cur = InventoryItemExtensions.GetNormalizedDurability(CurrentItem(ctrl)); } catch { }
                // sıvı haznesinde eşya okunamıyor (NaN); dolu olduğunu oyunun kendi "sıvı tamam" cevabından anla
                bool liqDone = false;
                if (Liquids.Contains(pn)) { try { liqDone = ServiceRequestSystem.GetLiquidRepaired(ctrl); } catch { } }
                if (cur >= 0.999f || liqDone) { already++; continue; }
                try
                {
                    int n = PartCount(ctrl);
                    for (int i = 0; i < n; i++) ctrl.ApplyDurabilityIssue(pn, ScriptableCarPartItemBase.MaxDurability, false, i);
                    fixedN++;
                    Plugin.L.LogInfo($"Onarım: {pn} dolduruldu/şarj edildi (%{cur * 100:0} → %100) [{issue}]");
                }
                catch (Exception e) { failed.Add($"{pn} ({e.Message})"); }
                continue;
            }
            if (pn == CarPartNames.Wheel)
            {
                // Tekerlek noktası jant+lastiği tek eşyada tutuyor (geçmiş log: arıza noktaları Hub/Brakepad/…/Wheel, Tire yok).
                try { var r = RepairWheel(ctrl, change, ReqFilter(req)); if (r == null) already++; else { fixedN++; Plugin.L.LogInfo($"Onarım: {r} [{issue}]"); } }
                catch (Exception e) { failed.Add($"Wheel ({e.Message})"); }
                continue;
            }
            if (pn is CarPartNames.FuseSmall or CarPartNames.FuseMid or CarPartNames.FuseLarge)
            {
                // yanık sigortanın yuvası arızada yazılı (brokenIndexes); dayanıklılık %100 görünür, o yüzden tahmin edilmez
                // Oyunun canlı listesi: CarPartNodeProcessFuseBox.Broken{Small,Mid,Large}FuseIndexes (CarPartFuse.Index).
                // Arıza asset'indeki brokenIndexes yalnızca başlangıç; yuva sırası ≠ sigorta numarası.
                var fctrl = ctrl.TryCast<CarPartNodeControllerFuse>();
                var live = LiveBrokenFuses(fctrl, pn);
                // İstek oyuna göre tamamsa dokunma (canlı liste kayıt yüklenince eski hâline dönebiliyor)
                if (req != null && (ReqScore(car, req) ?? 0f) >= 0.999f) { already++; continue; }
                var slots = live ?? BrokenSlots(src);
                if (slots.Count == 0) slots = BrokenSlots(src);   // liste elle boşaltıldıysa (eski sürüm) arızanın kendi yuvaları
                if (slots.Count == 0) { already++; continue; }
                foreach (var fuseIdx in slots)
                {
                    CarPartFuse fp = null; int slot = -1;
                    try { fp = fctrl?.GetFuseByIndex(fuseIdx); } catch { }
                    for (int k = 0, n = PartCount(ctrl); k < n; k++)
                    {
                        try
                        {
                            var cp = ctrl.GetCarPartByIndex(k);
                            if (fp != null ? cp.Pointer == fp.Pointer : cp.TryCast<CarPartFuse>()?.Index == fuseIdx) { slot = k; fp ??= cp.TryCast<CarPartFuse>(); break; }
                        }
                        catch { }
                    }
                    if (fp == null || slot < 0) { failed.Add($"{pn} (sigorta #{fuseIdx} bulunamadı)"); continue; }
                    InventoryItem slotItem = null;
                    try { slotItem = fp.carPartItem.Value; } catch { }
                    if (!ValidItem(slotItem)) { failed.Add($"{pn} (sigorta #{fuseIdx} okunamadı)"); continue; }
                    int oldH = InventoryItemExtensions.GetHashSafe(slotItem);
                    var fc = Candidates(ctrl, pn, oldH);   // aynı amper öncelikli
                    if (fc.Count == 0) { failed.Add($"{pn} (uygun sigorta yok)"); continue; }
                    try
                    {
                        // Düz takas yeterli: oyunun yanık listesi bir sonraki karelerde güncellenir (473 KA 67'de %100 oldu).
                        // Listeden elle silmek Validate'i bozuyor (%0) → yapma.
                        InstallSlot(ctrl, pn, fc[0], slot, slotItem);
                        bool still = false;
                        fixedN++;
                        Plugin.L.LogInfo($"Onarım: {pn} sigorta #{fuseIdx} (yuva {slot}) ← {fc[0].GetName} [{fc[0].GetIdentical}]{(still ? " — oyun hâlâ yanık diyor!" : "")} [{issue}]");
                    }
                    catch (Exception e) { failed.Add($"{pn} ({e.Message})"); }
                }
                continue;
            }
            var cands = Candidates(ctrl, pn, CurrentHash(ctrl));   // eski parçayla aynı özellik öncelikli
            var need = ReqFilter(req);   // istek belirli bir özellik istiyorsa (ör. spacer genişliği 12) eskisine değil isteğe uy
            if (need != null)
            {
                cands = Candidates(ctrl, pn).Where(need).ToList();
                if (cands.Count == 0) { failed.Add($"{pn} (isteğe uyan eşya yok: {req.name})"); continue; }
            }
            if (cands.Count == 0) { failed.Add($"{pn} (uygun eşya yok)"); continue; }
            var best = cands[0];
            if (CurrentHash(ctrl) == itemHash[best.Pointer] && Mathf.Approximately(InventoryItemExtensions.GetNormalizedDurability(CurrentItem(ctrl)), 1f))
            {
                if (FixTrace(ctrl)) { fixedN++; Plugin.L.LogInfo($"Onarım: {pn} değişim izi düzeltildi [{issue}]"); }
                else already++;
                continue;
            }
            try
            {
                int before = CurrentHash(ctrl);
                Install(ctrl, pn, best, PartCount(ctrl));
                if (CurrentHash(ctrl) == before && before != itemHash[best.Pointer] && InstallBrokenSlot(ctrl, pn, best))
                    Plugin.L.LogInfo($"Onarım: {pn} bozuk yuva tek tek değiştirildi");
                fixedN++;
                Plugin.L.LogInfo($"Onarım: {pn} ← {best.GetName} ({best.rarity}) [{issue}]");
            }
            catch (Exception e) { failed.Add($"{pn} ({e.Message})"); }
        }

        // kaporta: paslı parçaları temizle + boyalı say, yıkama istendiyse yıka
        string body = "";
        if (only == null || only.Contains(BodyIdx))
        try
        {
            var layer = car.carBodyLayerController;
            var rust = scen.rustyParts;
            if (layer != null && rust != BodyPartTypes.None)
            {
                layer.SetRustyParts(BodyPartTypes.None);
                layer.AddGrindParts(rust);
                layer.AddPaintedParts(rust);
                body += $", pas/boya: {rust}";
            }
            if (layer != null && scen.serviceRequests != null)
                foreach (var r in scen.serviceRequests)
                {
                    var pr = r?.TryCast<ScriptablePaintBodyPartServiceRequest>();
                    if (pr == null) continue;
                    try { var res = PaintRequest(car, layer, pr); if (res != null) { body += ", " + res; Plugin.L.LogInfo("Onarım: " + res); } }
                    catch (Exception e) { failed.Add(e.Message); }
                }
            bool wash = false;
            if (scen.extraIssues != null) foreach (var i in scen.extraIssues) wash |= i != null && i.affectedPart == CarPartNames.CarWash;
            if (scen.serviceRequests != null)
                foreach (var r in scen.serviceRequests)
                    if (r?.carIssues != null) foreach (var i in r.carIssues) wash |= i != null && i.affectedPart == CarPartNames.CarWash;
            if (layer != null && wash) { layer.WashCar(); layer.SetCarDirtPercentage(0f); layer.SetGlassDirtPercentage(0f); body += ", yıkandı"; }
        }
        catch (Exception e) { body += ", kaporta hatası: " + e.Message; }

        var msg = $"{fixedN} nokta onarıldı, {already} zaten tamamdı{body}   |   oyuna göre: {ReqSummary(car, scen)}";
        if (failed.Count > 0) { msg += $", {failed.Count} yapılamadı"; Plugin.L.LogWarning("Onarılamayan: " + string.Join(", ", failed)); }
        return msg;
    }

    // Hızlı teslim: oyunun kendi teslim akışı (CarServiceManager.StartDeliverCarCoroutine). Araç teslim alanında değilse
    // boş bir teslim alanına konur. Ödeme/itibar oyunun hesabıyla gelir.
    static string Deliver(Car car)
    {
        NotifyTypes nt = default;
        bool ok = true;
        try { ok = car.CanDeliver(out nt); } catch (Exception e) { return "CanDeliver hatası: " + e.Message; }
        if (!ok && !nt.ToString().Contains("DeliveryArea")) return $"teslim edilemez: {nt}";
        var csm = UnityEngine.Object.FindObjectOfType<CarServiceManager>();
        if (csm == null) return "CarServiceManager yok";
        CustomerCarDeliveryArea area = null; bool more = false;
        bool inArea = false;
        try { inArea = car.GetIsInDeliveryArea(out area, out more); } catch { }
        if (delivered.Contains(car.NetworkObjectId)) return "bu araç zaten teslim edildi (takıldıysa Kaldır)";
        if (!inArea || area == null)
        {
            // Alanın dışındaki araçla teslim akışı yarıda kalıyor (ücret gelir, araç gitmez) → önce alana koy, sonra teslim
            area = csm.GetEmptyCustomerCarDeliveryArea();
            if (area == null) return "boş teslim alanı yok";
            car.SnapCarPhysicToDeliveryAreaClientRpc(area.transform.position, area.transform.rotation);
            pendingDeliver.Add((car, Time.time + 1.5f));
            return "araç teslim alanına konuldu, birazdan teslim edilecek";
        }
        StartDeliver(csm, car, area);
        return "teslim başlatıldı";
    }

    static readonly HashSet<ulong> delivered = new();
    static readonly List<(Car Car, float T)> pendingDeliver = new();

    static void StartDeliver(CarServiceManager csm, Car car, CustomerCarDeliveryArea area)
    {
        delivered.Add(car.NetworkObjectId);
        csm.StartDeliverCarCoroutine(car, area);
        Plugin.L.LogInfo($"Teslim: {CarLabel(car)} → {area.name}");
    }

    static void PumpDeliveries()
    {
        for (int i = pendingDeliver.Count - 1; i >= 0; i--)
        {
            var (car, t) = pendingDeliver[i];
            if (Time.time < t) continue;
            pendingDeliver.RemoveAt(i);
            if (car == null) continue;
            CustomerCarDeliveryArea area = null; bool more = false;
            bool inArea = false;
            try { inArea = car.GetIsInDeliveryArea(out area, out more); } catch { }
            var csm = UnityEngine.Object.FindObjectOfType<CarServiceManager>();
            if (!inArea || area == null || csm == null) { Toast($"{CarLabel(car)}: teslim alanına girmedi, teslim edilmedi"); Plugin.L.LogWarning($"Teslim: {CarLabel(car)} alana girmedi"); continue; }
            if (more) { Toast("teslim alanında birden fazla araç var"); continue; }
            StartDeliver(csm, car, area);
            Toast($"{CarLabel(car)}: teslim başlatıldı");
        }
    }

    // Teslim akışı takılırsa aracı ödeme almadan sahneden kaldırır.
    static string RemoveCar(Car car)
    {
        string label = CarLabel(car);
        try { var nc = car.GetComponent<NetworkCar>(); if (nc != null) nc.DespawnCarWithPhysic(); else car.NetworkObject.Despawn(true); }
        catch (Exception e) { try { car.NetworkObject.Despawn(true); } catch { return "kaldırılamadı: " + e.Message; } }
        delivered.Remove(car.NetworkObjectId);
        Plugin.L.LogInfo($"Araç kaldırıldı: {label}");
        return "araç kaldırıldı";
    }

    static Car CarById(string nid)
    {
        var sm = NM?.SpawnManager;
        if (sm == null || !ulong.TryParse(nid, out var id) || !sm.SpawnedObjects.ContainsKey(id)) return null;
        return sm.SpawnedObjects[id].GetComponent<Car>();
    }

    static string Yn(Func<bool> f) { try { return f() ? "evet" : "hayır"; } catch { return "?"; } }

    // Tabletteki gibi "RADA 2107 · 907 AL 64"; bulunamazsa müşteri kodu
    static string CarLabel(Car car)
    {
        string brand = "", model = "", plate = "";
        try
        {
            var st = car.GetComponentInChildren<CarStats>(true);
            if (st != null)
            {
                try { brand = st.CarBrandData?.BrandName ?? ""; } catch { }
                try { model = st.CarModel?.GetLocalizedString() ?? ""; } catch { }
                try { plate = st.GetCarPlate?.PlateText ?? ""; } catch { }
            }
        }
        catch { }
        var left = $"{brand} {model}".Trim();
        if (left.Length == 0 && plate.Length == 0) { try { return car.GetCustomerName; } catch { return "#" + car.NetworkObjectId; } }
        return plate.Length > 0 ? $"{left} · {plate}".Trim(' ', '·') : left;
    }

    // Tamir edildi = bütün müşteri istekleri oyuna göre %100 (ya da Teslim'e basıldı). 0.5 sn'de bir RefreshWorld'de hesaplanır.
    static readonly HashSet<ulong> carDone = new();
    static int carView;   // 0 = bakılacak, 1 = tamir edildi / teslim bekliyor
    static bool IsCarDone(Car car)
    {
        if (delivered.Contains(car.NetworkObjectId)) return true;
        var reqs = car.CarServiceScenario?.serviceRequests;
        if (reqs == null || reqs.Count == 0) return false;
        foreach (var r in reqs) if (r != null && (ReqScore(car, r) ?? 0f) < 0.999f) return false;
        return true;
    }

    static void DrawCars()
    {
        int nDone = cars.Count(c => c != null && carDone.Contains(c.NetworkObjectId));
        Sec($"Araçlar ({cars.Count})");
        H();
        if (Pill(Next(170, LineH), $"Bakılacak ({cars.Count - nDone})", carView == 0)) carView = 0;
        if (Pill(Next(230, LineH), $"Tamir edildi / teslim bekliyor ({nDone})", carView == 1)) carView = 1;
        EH();
        repairExtras = Tgl(repairExtras, "Onar: müşteri aranmamış olsa da ek arızaları onar (aranmışsa zaten dahil)");
        if (cars.Count == 0) { Lbl("<color=#9aa0a6>Sahnede müşteri aracı yok.</color>"); return; }
        var me = LocalPlayer();
        foreach (var car in cars)
        {
            if (car == null) continue;
            ulong id = car.NetworkObjectId;
            if (carDone.Contains(id) != (carView == 1)) continue;
            string name = CarLabel(car), scen = "?";
            try { scen = car.CarServiceScenario != null ? car.CarServiceScenario.name : "-"; } catch { }
            bool open = carOpen.Contains(id);
            H();
            if (Pill(Next(26, LineH), open ? "-" : "+", open)) { if (open) carOpen.Remove(id); else carOpen.Add(id); }
            Lbl($"<b>{name}</b> <color=#9aa0a6>• {scen}</color>", 300);
            Lbl(me != null ? $"{Vector3.Distance(me.transform.position, car.transform.position):0} m" : "", 50);
            if (Btn("Git", 40)) Teleport(car.transform.position + car.transform.right * 2.5f + Vector3.up);
            if (Btn("Onar", 50)) Game("repair", id.ToString());
            if (Btn("Döküm", 60)) Game("dump", id.ToString());
            if (Btn("Teslim", 60)) Game("deliver", id.ToString());
            if (Btn("Kaldır", 60)) Game("removecar", id.ToString());
            EH();
            if (!open) continue;
            Lbl($"   Teslim günü: {car.GetExpectedDeliveryDay}   Teslim alanında: {Yn(() => car.GetIsInDeliveryArea())}   Müşteri arandı: {Yn(() => car.GetIsCustomerCalled)}");
            var sc = car.CarServiceScenario;
            if (sc == null) { Lbl("   senaryo yok"); continue; }
            if (sc.serviceRequests != null)
                foreach (var r in sc.serviceRequests) if (r != null) Lbl($"   <color=#ff8c1f>İstek:</color> {r.name}   {ReqStatus(car, r)}");
            var rl = RulesOf(sc);
            if (rl.Count > 0) Lbl($"   <color=#ff8c1f>Müşteri kuralı:</color> {string.Join(", ", rl)}");
            try { if (sc.rustyParts != BodyPartTypes.None) Lbl($"   <color=#ff8c1f>Pas:</color> {sc.rustyParts}"); } catch { }
            var targets = IssueTargets(car, sc);
            if (!repairSel.TryGetValue(id, out var sel))   // varsayılan: hepsi seçili
                repairSel[id] = sel = Enumerable.Range(0, targets.Count).Append(BodyIdx).ToHashSet();
            if (targets.Count == 0) Lbl("   <color=#9aa0a6>Parça değişimi gerektiren arıza yok</color>");
            for (int i = 0; i < targets.Count; i++)
            {
                var (ctrl, pn, issue, change, _, _) = targets[i];
                H();
                cx += 14;
                bool on = sel.Contains(i);
                if (Tgl(on, $"{pn}") != on) { if (on) sel.Remove(i); else sel.Add(i); }
                cx = lx + 200;
                Lbl($"<color=#9aa0a6>{issue}</color> {(change ? "[değiştir]" : "[doldur/şarj]")}", 260);
                Lbl(ItemLabel(ctrl), 220);
                EH();
            }
            bool hasBody = false;
            try { hasBody = sc.rustyParts != BodyPartTypes.None || (sc.serviceRequests != null && sc.serviceRequests.ToArray().Any(r => r?.TryCast<ScriptablePaintBodyPartServiceRequest>() != null)); } catch { }
            H();
            cx += 14;
            if (hasBody) { bool b = sel.Contains(BodyIdx); if (Tgl(b, "Kaporta (pas/boya/yıkama)") != b) { if (b) sel.Remove(BodyIdx); else sel.Add(BodyIdx); } }
            if (Btn("Tümünü seç", 90)) { sel.UnionWith(Enumerable.Range(0, targets.Count)); sel.Add(BodyIdx); }
            if (Btn("Hiçbiri", 70)) sel.Clear();
            if (Btn($"Seçilenleri onar ({sel.Count(x => x != BodyIdx) + (hasBody && sel.Contains(BodyIdx) ? 1 : 0)})", 170))
                Game("repair", $"{id};{string.Join(",", sel)}");
            EH();
        }
    }

    static void DrawBuildings()
    {
        Sec($"Binalar ({slots2.Count})");
        if (slots2.Count == 0) { Lbl("<color=#9aa0a6>Bina slotu bulunamadı.</color>"); return; }
        Lbl("<color=#9aa0a6>Yükselt, oyunun normal yükseltmesini çağırır (para/itibar gerekir — yukarıdan ekleyebilirsin).</color>");
        foreach (var s in slots2)
        {
            if (s == null) continue;
            string label;
            try
            {
                var d = s.buildingSlotData.Value;
                label = d.isNull ? "<color=#9aa0a6>boş slot</color>" : $"{(d.Data != null ? d.Data.name : "?")}  •  seviye {d.GetCurrentLevel()}";
            }
            catch (Exception e) { label = "? " + e.Message; }
            H();
            Lbl($"#{s.NetworkObjectId}  {label}", 420);
            if (Btn("Yükselt", 70)) { try { s.TryToUpgradeServerRpc(default); Toast("Yükseltme istendi"); } catch (Exception e) { Toast("hata: " + e.Message); } }
            if (Btn("Git", 40)) Teleport(s.transform.position + Vector3.up * 1.5f + s.transform.forward * 2f);
            EH();
        }
    }

    // ── market ──
    static bool fastCargo;
    static float[] cargoOrig;

    static string SetFastCargo(bool on)
    {
        if (on && cargoOrig == null) cargoOrig = new[] { PlayersMarket.maxDeliveryTime, PlayersMarket.defaultDeliveryTime, PlayersMarket.deliveryTimePerKG };
        if (on) { PlayersMarket.maxDeliveryTime = 1f; PlayersMarket.defaultDeliveryTime = 1f; PlayersMarket.deliveryTimePerKG = 0f; }
        else if (cargoOrig != null) { PlayersMarket.maxDeliveryTime = cargoOrig[0]; PlayersMarket.defaultDeliveryTime = cargoOrig[1]; PlayersMarket.deliveryTimePerKG = cargoOrig[2]; }
        fastCargo = on;
        return on ? "Anında kargo açık (yeni siparişler için)" : "Kargo süreleri normale döndü";
    }

    static void DrawTest()
    {
        if (PureClient) Lbl("<i><color=#9aa0a6>İstemcisin: komutlar host'ta çalışır (host'ta mod + 'istemcilere izin ver' gerekir).</color></i>");
        var inv = Inv;

        Sec("Ekonomi");
        Lbl(inv == null ? "PlayersInventory bulunamadı — bir kayıt yükle / oturum aç."
            : $"Para: <b>{inv.GetMoney:N0} $</b>     İtibar: <b>{inv.GetReputationPoints}</b>");
        H();
        foreach (var v in new[] { 1000, 10000, 100000 }) if (Btn($"+{v:N0} $")) Game("addmoney", v.ToString());
        moneyIn = Txt(moneyIn, 110, "moneyIn");
        if (Btn("Ayarla")) Game("setmoney", moneyIn);
        EH();
        H();
        foreach (var v in new[] { 10, 100, 1000 }) if (Btn($"+{v} itibar")) Game("addrep", v.ToString());
        repIn = Txt(repIn, 110, "repIn");
        if (Btn("Ayarla ")) Game("setrep", repIn);
        EH();

        Sec("İş akışı");
        H();
        if (Btn("Yeni müşteri aracı getir")) Game("spawn", "");
        bool fast = fastCargo;
        if (Tgl(fast, "Anında kargo (market)") != fast) Game("fastcargo", fast ? "0" : "1");
        EH();

        DrawCars();
        DrawBuildings();

        Sec("Zaman");
        var clock = Clock;
        int day = clock != null ? clock.GetCurrentDay : 1;
        Lbl(clock == null ? "DayNightCycle bulunamadı" : $"Gün <b>{day}</b>  ({(clock.IsDay ? "gündüz" : "gece")})");
        H();
        foreach (var h in new[] { 8, 12, 17, 21 }) if (Btn($"{h:00}:00")) Game("time", $"{day},{h}");
        if (Btn("Sonraki gün 08:00")) Game("time", $"{day + 1},8");
        EH();

        Sec("Parça ver");
        H();
        Lbl("Ara:", 36);
        itemQuery = Txt(itemQuery, 220, "itemQuery");
        Lbl("Adet:", 42);
        if (Btn("-", 26)) itemCount = Math.Max(1, itemCount - 1);
        Lbl($"<b>{itemCount}</b>", 26);
        if (Btn("+", 26)) itemCount = Math.Min(50, itemCount + 1);
        EH();
        var list = Items();
        if (list.Count == 0) { Lbl("Parça kataloğu henüz yüklenmedi (oyuna girince dolar)."); return; }
        if (itemInfo == null || itemInfo.Count != list.Count)
            itemInfo = list.Select(i => (Cls: i.GetIl2CppType().Name.Replace("ScriptableCarPart", ""), Rarity: i.rarity.ToString())).ToList();

        // kategori
        H();
        if (Pill(Next(TextW("Tümü"), LineH), "Tümü", itemCat == null)) itemCat = null;
        foreach (var c in itemInfo.Select(x => x.Cls).Distinct().OrderBy(x => x))
            if (Pill(Next(TextW(c), LineH), c, itemCat == c)) itemCat = itemCat == c ? null : c;
        EH();
        // nadirlik + seçenekler
        H();
        Lbl("Nadirlik:", 64);
        if (Pill(Next(TextW("Tümü"), LineH), "Tümü", itemRarity == null)) itemRarity = null;
        foreach (var r in itemInfo.Select(x => x.Rarity).Distinct().OrderBy(x => x))
            if (Pill(Next(TextW(r), LineH), r, itemRarity == r)) itemRarity = itemRarity == r ? null : r;
        EH();
        H();
        itemUnlockedOnly = Tgl(itemUnlockedOnly, "Sadece itibarım yetenler");
        Lbl("Sırala:", 50);
        foreach (var (k, label) in new[] { (0, "Ad"), (1, "Fiyat artan"), (2, "Fiyat azalan"), (3, "İtibar") })
            if (Pill(Next(TextW(label), LineH), label, itemSort == k)) itemSort = k;
        EH();

        int rep = Inv != null ? Inv.GetReputationPoints : int.MaxValue;
        var rows = Enumerable.Range(0, list.Count).Where(i =>
        {
            var it = list[i];
            var (cls, rarity) = itemInfo[i];
            if (itemCat != null && cls != itemCat) return false;
            if (itemRarity != null && rarity != itemRarity) return false;
            if (itemUnlockedOnly && it.GetRequiredReputationPoints > rep) return false;
            return itemQuery.Length == 0 || (it.GetName ?? "").IndexOf(itemQuery, StringComparison.OrdinalIgnoreCase) >= 0
                || cls.IndexOf(itemQuery, StringComparison.OrdinalIgnoreCase) >= 0;
        });
        rows = itemSort switch
        {
            1 => rows.OrderBy(i => list[i].GetBuyPrice),
            2 => rows.OrderByDescending(i => list[i].GetBuyPrice),
            3 => rows.OrderBy(i => list[i].GetRequiredReputationPoints),
            _ => rows,   // Items() zaten ada göre sıralı
        };
        var shownRows = rows.ToList();
        Lbl($"<color=#9aa0a6>{shownRows.Count} parça</color>");
        foreach (var i in shownRows.Take(150))
        {
            var it = list[i];
            var (cls, rarity) = itemInfo[i];
            bool locked = it.GetRequiredReputationPoints > rep;
            H();
            Lbl(locked ? $"<color=#9aa0a6>{it.GetName}</color>" : it.GetName, 230);
            Lbl($"<color=#9aa0a6>{cls}</color>", 130);
            Lbl($"<color={RarityColor(rarity)}>{rarity}</color>", 80);
            Lbl($"{it.GetBuyPrice:0} $  •  itibar {it.GetRequiredReputationPoints}", 140);
            if (Btn("Ver", 50)) Game("item", $"{it.ItemArrayIndex},{itemCount}");
            EH();
        }
        if (shownRows.Count > 150) Lbl("<color=#9aa0a6>… ilk 150 gösteriliyor, filtreyi daralt</color>");
    }

    // Items() ile aynı sırada; kategori/nadirlik her karede reflection'la hesaplanmasın
    static List<(string Cls, string Rarity)> itemInfo;
    static string itemCat, itemRarity;
    static bool itemUnlockedOnly;
    static int itemSort;

    static string RarityColor(string r) => r.ToLowerInvariant() switch
    {
        var s when s.Contains("legend") => "#ffb02e",
        var s when s.Contains("epic") => "#c77dff",
        var s when s.Contains("uncommon") => "#5fd068",
        var s when s.Contains("rare") => "#4da3ff",
        _ => "#c8ccd2",
    };

    static void DrawGeneral()
    {
        Sec("Menü");
        bool h = Tgl(hud, "HUD göster (FPS, ms, grafik API, rol, ping, konum, hata sayısı)");
        if (h != hud) { hud = h; if (hudCfg != null) hudCfg.Value = h; }
        Lbl("<color=#9aa0a6>Menü açıkken oyunun bütün tuş/fare girdileri kapalıdır; kapatınca geri gelir.</color>");
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
