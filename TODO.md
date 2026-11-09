# CST Debug Mode — yapılacaklar (2026-11-10, commit c5d40ed)

## Oyunda denenmedi (son sürümde)
- [ ] Onar 2. tur (Validate ile aday arama): %100 olmayan istekte "Doğrulama:" log satırları çıkıyor mu, istek %100 oluyor mu?
- [ ] Ekstra kabul eden müşteride ek arızaların otomatik onarımı; teslimde para doğru mu (Extra part Cost ceza mı ek ücret mi — hâlâ bilinmiyor)
- [ ] Teslim: yalnız spawn edilmiş alan (FreeArea) + aracı alana taşıma + 3 yeniden deneme. Araç gerçekten gidiyor mu, ödeme geliyor mu?
- [ ] Yeni menü düzeni (Araçlar/Ekonomi/Parçalar/Binalar/Oyuncu/Ayarlar/Geliştirici), Ön görüşme / Tamirde / Teslime hazır grupları
- [ ] Renkli boya isteği (PaintRequest renk taraması)

## Açık sorunlar
- [ ] Jant isteği ("Rim Uncommon Customer Request") %0 kaldı — Legend jant takılı. Aracı Kaldır'dan ÖNCE Döküm al (teker parçaları + istek sınıfı yazılıyor).
- [ ] Jant/göbek inç istekleri ("Hub Inch 18", "Wheel Inch 18") — inç filtresi + 2. tur eklendi, denenmedi.
- [ ] Şanzıman istekleri (Liquid Gear Oil, Output Shaft, GearR) — eşleşmeyen arızada "onarım gerekli" noktalar alınıyor; şanzıman yuva tabanlıysa yetmeyebilir. Döküm gerek.
- [ ] Sigorta: 907 AL 64 far, 473 KA 67 soğutma, 611 ME 03 far — eski sürüm yanık listesini elle boşalttı; düz takas sürümünde tekrar Onar ile dene.
- [ ] Kaldır: teslim alanını/araç sayacını temizliyor mu bilinmiyor.
- [ ] Kayıt bazen eski hâline dönüyor (teslim edilen araç geri geliyor) — oyunun kayıt zamanlamasıyla ilgili olabilir.

## Notlar
- Oyun açıkken DLL kilitli; oyun kapanınca bin\Release\net6.0\CSTDebugMode.dll → BepInEx\plugins kopyalanmalı.
- Log izleme: BepInEx\LogOutput.log + Player_Debug.log (grep -a).
