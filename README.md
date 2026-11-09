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
| F8  | Debug menüsü (imleç serbest kalır, menü açıkken oyunun tüm tuş/fare girdileri kapalı) |
| F10 | Uçma/noclip (WASD, Space yukarı, Ctrl aşağı, Shift hızlı) |

## Sekmeler
- **Araçlar**: müşteri araçları üç grupta — *Görüşme* (müşteri aranmadı), *Tamir* (istekler eksik), *Bekliyor* (hepsi %100).
  Satırda plaka, tamamlanan istek sayısı, Git ve aşamaya göre Onar / Teslim et. `+` ile ayrıntı: istekler, müşteri kuralı,
  tek tek seçerek onarım, Döküm (log'a teşhis), Kaldır (ödemesiz sil — takılan araç için).
- **Ekonomi**: para, itibar, yeni müşteri aracı, anında kargo, gün/saat.
- **Parçalar**: parça ver (ara, kategori, nadirlik, itibarım yetenler, sıralama).
- **Binalar**: bina slotları, yükselt, git.
- **Oyuncu**: uçma, 3 konum slotu, nişangah altındaki nesne.
- **Ayarlar**: HUD (varsayılan kapalı), grafik bilgisi, VSync, FPS limiti, zaman ölçeği.
- **Geliştirici**: Ağ (rol, ping, oyuncular, NetworkObject'ler, "istemcilere izin ver"), İnceleyici (bileşen alanları/metotları), Konsol (Unity logları).

## Multiplayer nasıl çalışıyor
- Mod mesajları NGO `CustomMessagingManager` named message (`CSTDebug`) üzerinden, oyunun kendi Steam bağlantısıyla gider.
- Host her şeyi doğrudan uygular. İstemci, ağ nesnelerindeki değişiklikleri host'a yollar; host izin verdiyse çalıştırır ve sonucu geri bildirir.
- `NetworkVariable` alanları host'ta yazılınca herkese senkronlanır (ör. para bir NetworkVariable ise).
- `...ServerRpc` metotları istemcide doğrudan çağrılır (oyunun kendi RPC'si zaten host'a gider).
- Işınlanma: hem yerelde hem host üzerinden uygulanır; `NetworkTransform` yetkisi kimdeyse onunki tutar.
