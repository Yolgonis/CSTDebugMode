# CST Debug Mode (Car Service Together)

BepInEx 6 IL2CPP plugin'i. Arayüz Unity IMGUI ile çizilir; Unity kendi renderer'ıyla çizdiği için
DX11/DX12/Vulkan farketmez, Present/DXGI hook'u yok → **DX12'de çalışır**.

## Kurulum
1. `version.dll` oyun klasöründe zaten var (kaynağı belirsiz) — dokunma. BepInEx `winhttp.dll` kullanır, çakışmaz.
2. BepInEx 6 **bleeding edge** IL2CPP x64 indir (Unity 6 için be.735+):
   https://builds.bepinex.dev/projects/bepinex_be → `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.XXX.zip`
   Oyun klasörüne aç (`winhttp.dll`, `doorstop_config.ini`, `BepInEx\` exe'nin yanında olmalı).
3. Oyunu **bir kez** aç, ana menü gelince kapat → `BepInEx\interop\` oluşur (ilk açılış birkaç dakika sürer).
4. Derle: `dotnet build -c Release` (bu klasörde, .NET 6+ SDK).
   Derleme sonunda kopyalama hatası alırsan (Program Files izni): dll derlendi, `bin\Release\net6.0\CSTDebugMode.dll` dosyasını `BepInEx\plugins\` içine elle kopyala ya da terminali yönetici olarak aç.
5. Multiplayer'da her oyuncu kurar; **host'ta kurulu olması şart** (istemci komutlarını host çalıştırır).

## Tuşlar
| Tuş | İş |
|-----|----|
| F8  | Debug menüsü (imleç serbest kalır) |
| F9  | HUD: FPS, ms, grafik API, rol, ping, konum, hata sayısı |
| F10 | Uçma/noclip (WASD, Space yukarı, Ctrl aşağı, Shift hızlı) |

## Sekmeler
- **Genel**: grafik API/GPU/VRAM, sahne, bellek, VSync, FPS limiti, zaman ölçeği (host → istemcilere yayınlanır).
- **Ağ**: rol, tick, transport, ping, host'ta mod var mı, oyuncular (mesafe/RTT/"Git"), tüm NetworkObject'ler (filtre, İncele, Git).
  Host'ta "İstemcilerin debug komutlarına izin ver" kutusu (varsayılan KAPALI).
- **Oyuncu**: uçma, 3 konum slotu, nişangah altındaki nesne (yol, NetworkObject id/owner, bileşenler, buraya ışınlan).
- **İnceleyici**: oyun bileşenlerini bul (hazır: CarServiceManager, JobDispatchManager, …; ya da tip ara),
  int/float/bool/string/enum alanlarını ve `NetworkVariable<T>` değerlerini oku/yaz, parametresiz metotları çağır.
- **Konsol**: Unity logları, hata filtresi, `BepInEx\cstdebug_log.txt`'e kaydet.

## Multiplayer nasıl çalışıyor
- Mod mesajları NGO `CustomMessagingManager` named message (`CSTDebug`) üzerinden, oyunun kendi Steam bağlantısıyla gider.
- Host her şeyi doğrudan uygular. İstemci, ağ nesnelerindeki değişiklikleri host'a yollar; host izin verdiyse çalıştırır ve sonucu geri bildirir.
- `NetworkVariable` alanları host'ta yazılınca herkese senkronlanır (ör. para bir NetworkVariable ise).
- `...ServerRpc` metotları istemcide doğrudan çağrılır (oyunun kendi RPC'si zaten host'a gider).
- Işınlanma: hem yerelde hem host üzerinden uygulanır; `NetworkTransform` yetkisi kimdeyse onunki tutar.
