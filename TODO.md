# CST Debug Mode — yapılacaklar (2026-11-10)

## Oyunda doğrulandı (2026-11-10)
- [x] Araç grupları: Ön görüşme (kabul edilmedi) → Tamirde (kabul edildi; "parçalara dokunulabilir" açılıyor) → Teslime hazır (müşteri arandı)
- [x] Otomatik log (`[oto]` satırları: araç durumu, para, sahneden ayrılan araç)
- [x] Onar: Xonda 345 ZY 21 5/5; ikinci basış bir şey bozmuyor
- [x] Renkli boya isteği ("mevcut renk uydu" yolu)
- [x] İtibar ekleme, Binalar → Yükselt isteği

## Oyunda denenmedi (son sürümde)
- [ ] Teslim et: oyunun DeliverCarCoroutine'i eklenti tarafından adım adım yürütülüyor, beklemeler atlanıyor (StepDelivery).
      Logda "Teslim adım N: …" ve "Teslim bitti" satırlarına bak; araç gidiyor mu, para geliyor mu?
      (Önceki sürüm: randevu saati "şimdi" yapıldı ama araç gitmedi, para gelmedi.)
- [ ] Teslim sırası + alan seçimi: collider'ı kapalı alan artık boş sayılmıyor; "Teslim: … taşındı" satırlarındaki konumlara bak.
      (Önceki sürüm: 5 araç 5'er kez taşındı, hiçbiri alanda sayılmadı.)
- [ ] Basılı tutma yok (Ayarlar): CarPart süre getter'larına Harmony yaması + arayüz düğmeleri. Parça sökme tek tık oluyor mu?
- [ ] Ağ numarası tekrar kullanımı: giden aracın kayıtları 2 sn'de bir siliniyor (delivered / seçimler)
- [ ] Onar 2. tur (Validate ile aday arama): "Doğrulama:" satırları; aday bulunamazsa envantere kopya parça gitmemeli
- [ ] "Seçilenleri onar": seçim artık nokta işaretçisiyle tutuluyor (satır kayması olmamalı)
- [ ] Ekstra kabul eden müşteride ek arızalar: teslimde para doğru mu (Extra part Cost ceza mı ek ücret mi — hâlâ bilinmiyor)

## Açık sorunlar
- [ ] Sigorta istekleri hâlâ %0: Mostwagon 109 NS 77 (Fuse Large + Fuse Small Headlights), Rada 502 CL 45 (Fuse Large). Sigortalar değişiyor ama Validate %0.
- [ ] Lastik tipi isteği: Rada 473 KA 67 "Tire Type Customer Request_Drift" %0 (11 nokta "zaten tamam").
- [ ] Jant isteği ("Rim Uncommon Customer Request") %0 kaldı — Legend jant takılı. Aracı Kaldır'dan ÖNCE Döküm al.
- [ ] Jant/göbek inç istekleri ("Hub Inch 18", "Wheel Inch 18") — inç filtresi + 2. tur eklendi, denenmedi.
- [ ] Şanzıman istekleri (Liquid Gear Oil, Output Shaft, GearR) — Döküm gerek.
- [ ] Kaldır: teslim alanını/araç sayacını temizliyor mu bilinmiyor.
- [ ] Oto log, kayıt yüklenirken bir kez "hepsi kapalı / 0 istek" satırı yazıyor (zararsız gürültü).

## Notlar
- Oyun açıkken DLL kilitli; oyun kapanınca bin\Release\net6.0\CSTDebugMode.dll → BepInEx\plugins kopyalanmalı.
- Log izleme: BepInEx\LogOutput.log (yoklamayla; `tail -F` Git Bash'te satır yakalamıyor). Player_Debug.log oyunun kendi logu.
- Oyun, giden aracın ağ numarasını ve plakasını yeni araca verebiliyor.
- Kabul edilmemiş araçlar kayda yazılmıyor gibi (yüklemede yoklar).
