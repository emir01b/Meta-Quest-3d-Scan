**MetaScan — 22 Ekim 2026 teknik inceleme ve geliştirme yol haritası**

Bu belge uygulama öncesi denetimin tarihsel kaydıdır. 23 Ekim 2026'da kod değişiklikleri başlatılmıştır; güncel davranış ve kalan cihaz testleri için [README](../README.md) esas alınmalıdır.

Tarih: 22 Ekim 2026 · İncelenen commit: `4299d5a2ea6513166d03533dccfc9536a2c024bb` · Son commit tarihi: 24 Mayıs 2026.

Bu rapor, kullanıcının paylaştığı önceki kaynak kod denetimini, bu sohbet içinde yapılan kod incelemesini, yerel kontrolleri ve birincil kaynak araştırmasını birleştirir. Uygulama kodu değiştirilmemiştir. Rapor oluşturulması, önerilen mimarinin uygulanmış olduğu anlamına gelmez.

Kullanım hedefi, Meta Quest 3S ile gerçek nesnenin geometrisini mümkün olduğunca doğru elde etmek, gerektiğinde modeli düzenlemek ve 3D baskı almaktır. Kullanıcı yaklaşık 150 karelik bir denemeden söz etmiştir; işlem süresi belirtilmemiştir. “30cm3” ifadesi bu raporda yaklaşık 30 cm doğrusal boyut olarak varsayılmıştır. Eğer 30 cm³ hacim kastediliyorsa ayrıntı ve çalışma mesafesi hedefleri yeniden değerlendirilmelidir. Nesne malzemesi ve yüzey özellikleri henüz bilinmiyor.

**Ana değerlendirme:** Projede sensör verisinin fiziksel anlamını, veri aktarımının güvenilirliğini, rekonstrüksiyon maliyetini ve baskı ölçeğini etkileyen doğrulanmış uygulama sorunları bulunuyor. Öncelik; kamera/depth geometrisi ile güvenilir oturum kaydını düzeltmek, ardından ölçülmüş kalite–süre karşılaştırmasıyla rekonstrüksiyon yöntemlerini seçmek olmalıdır. RTX 5060 Ti için küçük örnekte GPU özellik çıkarımı başarıyla çalışmıştır; gerçek taramanın tamamında GPU uyumluluğu veya yeterli performans sağlandığı henüz gösterilmemiştir.

Kanıt düzeyi aşağıdaki anlamlarda kullanılır:

| Düzey | Anlamı |
|---|---|
| Kodla doğrulandı | Mevcut kaynakta davranış veya eksiklik doğrudan görüldü. Geçmiş taramadaki etkisinin büyüklüğü ayrıca ölçülmelidir. |
| Küçük çalıştırmayla doğrulandı | Yerel, sınırlı bir kontrol çalıştırıldı. Sonuç gerçek Quest taraması benchmark'ı değildir. |
| Resmî kaynakla desteklendi | SDK/kütüphane davranışı birincil dokümantasyon veya kaynak kodla karşılaştırıldı. |
| Cihaz/veri testi gerekli | Fiziksel hata, kesin süre, kalite veya cihazın gerçek çıktısı henüz ölçülmedi. |

**Önceki raporun değerlendirilmesi**

| Önceki iddia veya öneri | Nihai değerlendirme |
|---|---|
| WebSocket gönderim görevleri beklenmiyor | Doğrulandı. Tek bir gönderici tarafından seri gönderim ve kapanış bariyeri gerekli. Her taramada veri bozulduğu iddia edilemez. |
| WebSocket mesaj parçalanması işlenmiyor | Doğrulandı. Tek ReceiveAsync sonucunun tam mesaj olduğu varsayılıyor. Gerçek küçük JSON yanıtlarının ne sıklıkla parçalandığı ölçülmedi. |
| RGB ve depth eşzamanlılığı doğrulanmıyor | Doğrulandı. Buna fiziksel RGB kamera pozu ve görüntü zamanı uyuşmazlığı da ekleniyor. |
| Kalite kontrolü fiilen kapalı | Doğrulandı. RGB kalite uyarıları zorla başarıya çevriliyor. Geometri için ayrıca depth kalite ölçütleri gerekir. |
| Depth değeri yanlış yorumlanıyor olabilir | Daha güçlü kanıt var: kullanılan global doku için Meta örneği doğrusallaştırma yapıyor; projede bu dönüşüm bulunmuyor. Cihazda gerçekten okunan doku ve ölçülen mesafe ayrıca doğrulanmalı. |
| Merkez göz kamerasının kalibrasyonu kullanılıyor | Doğrulandı; ayrıca normal rig yolunda erken return nedeniyle bu hesap hiç çalışmayıp tahmini değerler kalabiliyor. |
| RGB'yi yeniden boyutlandırmak hizalama değildir | Doğrulandı. Sunucuda kalibrasyon boyutunun yeniden boyutlandırılmış görüntüyle uyumsuz kalması da ayrı hata. |
| Protokolde depth boyutları eksik | Doğrulandı. 320×240 örneği varsayımsal bir girdidir; Quest 3S'in bu projede gözlenen gerçek çıkışı değildir. |
| Quaternion işaretleri yanlış; dönüşüm yeniden yazılmalı | Bu kesinlikte söylenemez. Mevcut işaret eşlemesi, S=diag(1,-1,1) için S·R·S ile matematiksel olarak tutarlı. Fiziksel kamera, görüntü yönü ve dünya koordinat zinciri henüz doğrulanmış değil. |
| RGB-D odometry ve ICP yeni ana akış olmalı | Denenebilir adaylar. Başlangıçta zorunlu kılınmamalı; tracking pozları, fotogrametri ve stereo alternatifleri aynı veriyle karşılaştırılmalı. |
| Segmentasyon sonraya bırakılabilir | Kullanıcı nesne taradığı için hedef hacmin sunucuya taşınması erken aşamada gerekli. Öğrenilmiş segmentasyon daha sonra eklenebilir. |
| GPU optimizasyonu en son yapılmalı | GPU TSDF sonradan ele alınabilir; mevcut SIFT CPU seçimi ve eşleştirme maliyeti ilk performans aşamasında düzeltilmeli. |

**Doğrulanan bulgular ve öncelikleri**

P0: fiziksel veri doğruluğu, güvenilir kayıt veya baskı ölçeği için engelleyici. P1: kalite, süre ve kullanıcı akışı için yüksek öncelik. P2: temel doğrulandıktan sonra ayrıntı ve kullanım geliştirmesi. Bu etiketler ölçülmüş ekonomik etki veya donanım arızası anlamına gelmez.

1. **F01 — P0: RGB kamera kalibrasyonu ve erken çıkış.** [CameraCapture.cs:155](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/QuestScanner/Assets/Scripts/CameraCapture.cs:155) rig bulunduğunda merkez göz transform'unu atayıp dönüyor. Sonraki intrinsics hesabına ulaşılamıyor. Bu nedenle başlangıçtaki tahmini odak değerleri kalabilir. Hesap çalışsa da display/center-eye projection matrisi fiziksel passthrough kamerasının kalibrasyonu olarak kullanılıyor. Çözüm, seçilen fiziksel kameranın intrinsics değerlerini gerçek görüntü çözünürlüğüne ve yönüne göre kaydetmektir. Meta bu verileri PCA üzerinden sunuyor. [Resmî PCA dokümantasyonu](https://developers.meta.com/vr/documentation/unity/unity-pca-documentation/)

2. **F02 — P0: Görüntü zamanı ve fiziksel kamera pozu eşleşmiyor.** [CameraCapture.cs:231](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/QuestScanner/Assets/Scripts/CameraCapture.cs:231) görüntü kodlandıktan sonra sistem saatini ve o andaki centerEyeAnchor pozunu okuyor. RGB'nin gerçek çekim anındaki fiziksel kamera pozu kullanılmıyor. [DataStreamer.cs:157](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/QuestScanner/Assets/Scripts/DataStreamer.cs:157) depth'i ayrıca alıyor. Görüntü/depth kimliği, ayrı zamanlar, saat alanları ve eşleştirme farkı kaydedilmeli. PCA'nin GetCameraPose ve Timestamp davranışı hedef SDK sürümünde doğrulanmalı; görüntü, poz ve kalibrasyon aynı örnek için birlikte saklanmalı. [Meta API açıklaması](https://developers.meta.com/vr/documentation/unity/unity-pca-documentation/)

3. **F03 — P0: Depth doku değeri doğrusal metre kabul ediliyor.** [DepthCapture.cs:88](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/QuestScanner/Assets/Scripts/DepthCapture.cs:88) global dokuyu Blit ile kopyalayıp kırmızı kanalı doğrudan metre kabul ediyor. Meta'nın aynı global dokuya ait örneği NDC dönüşümü ve Z-buffer parametreleriyle doğrusallaştırma uyguluyor. Projede bu adım yok. Stereo array katmanı seçimi de açık değil. Çözüm, hedef SDK'nin doku semantiğine uygun GPU dönüşümü, açık göz/katman seçimi, geçersiz değer maskesi ve metrik mesafe testidir. NaN, sonsuz, ölçüm dışı veya geçersiz örnekler yüzey olarak birleştirilmemeli. [Meta depth açıklaması](https://developers.meta.com/vr/blog/mixed-reality-motif-instant-content-placement-virtual-objects-project-setup/), [Meta shader örneği](https://github.com/oculus-samples/Unity-MRMotifs/blob/main/Assets/MRMotifs/InstantContentPlacement/Shaders/DepthLookingGlassMotif.shader)

4. **F04 — P0: Depth hazır durumu gerçeği temsil etmiyor.** [DepthCapture.cs:51](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/QuestScanner/Assets/Scripts/DepthCapture.cs:51) manager bileşenini etkinleştirince kendi IsDepthAvailable değerini true yapıyor. Gerçek SDK desteği, izin sonucu ve geçerli depth karesi beklenmiyor. Durum; destek, izin, başlangıç, ilk geçerli kare, tazelik ve hata olarak izlenmeli. Manager'ın varlığı veri bulunduğunun kanıtı sayılmamalı.

5. **F05 — P0: Veri sözleşmesinde kamera/depth geometrisi eksik.** [config.py:66](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/server/config.py:66) ve [frame_handler.py:122](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/server/frame_handler.py:122) incelendiğinde; gerçek RGB/depth boyutları, kamera kimliği, depth projeksiyonu, birim/encoding, ayrı zamanlar ve kalibrasyon kimliği bulunmuyor. Sunucu genişlik/yüksekliği 1280×960 yazıyor. İlk intrinsics değerinin oturum boyunca tutulması, sabit ve doğrulanmış modda uygundur; mod değişikliklerinin saptanmaması hatadır. Sürümlü protokol ve mod değişince yeni kalibrasyon kaydı gerekli. Eski oturumların eksik alanları tahminle güvenilir hale getirilemez.

6. **F06 — P0: RGB-depth yeniden projeksiyonu ve TSDF boyut uyumu eksik.** [reconstruction.py:492](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/server/reconstruction.py:492) depth boyutunu veri uzunluğundan tahmin ediyor; kare olmayan ve 1280×960 olmayan girdiyi atlıyor. RGB küçültülüyor fakat TSDF intrinsics boyutu/parametreleri buna uydurulmuyor. Yalnızca boyut eşitlemek iki farklı kamera geometrisini hizalamaz. Depth kendi kamera uzayında açılmalı; gerektiğinde dünya/RGB uzayına dönüştürülerek görünürlük denetimiyle renk atanmalı. Alternatif olarak tamamen RGB'ye kayıtlı depth üretilmeli. Open3D'nin boyut kontrolü bu uyuşmazlığı hata olarak ele alır. [Open3D kaynak kodu](https://github.com/isl-org/Open3D/blob/main/cpp/open3d/pipelines/integration/ScalableTSDFVolume.cpp)

7. **F07 — P0: WebSocket gönderimi ve oturum kapanışı güvenilir değil.** [DataStreamer.cs:461](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/QuestScanner/Assets/Scripts/DataStreamer.cs:461) SendAsync görevini beklemiyor. Kuyruk yalnızca bir Unity karesi bekliyor; bu, önceki ağ gönderiminin tamamlandığını garanti etmiyor. Metin komutları da aynı seri kuyruğa bağlı değil. Microsoft aynı bağlantıda eşzamanlı birden fazla gönderimi desteklemiyor. Son karelerden önce stop komutu çalışabilir; FramesSent sunucuda kabul edilen kare sayısı değildir. Tek gönderici, görev hatalarının gözlenmesi, sınırlı kuyruk, sunucu kabul sayacı ve stop öncesi boşaltma/onay bariyeri gerekli. Başarılı seri TCP mesajlarının kendiliğinden sırasının bozulduğu iddia edilmiyor. [Microsoft ClientWebSocket belgesi](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocket.sendasync?view=net-9.0)

8. **F08 — P1: Alınan WebSocket mesajı birleştirilmiyor; JSON ayrıştırma kırılgan.** [DataStreamer.cs:428](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/QuestScanner/Assets/Scripts/DataStreamer.cs:428) EndOfMessage beklemiyor. HandleMessage metin arama ve sabit ofsetler kullanıyor. Tam mesajı sınırlı bir tamponda birleştirmek, standart JSON ayrıştırması, iptal/timeout ve tüm çıkışlarda tutarlı bağlantı durumu gerekir. 64 KB tampon mesaj sınırının yerine geçmez. Mevcut küçük yanıtların her zaman parçalandığı söylenemez.

9. **F09 — P1: Kalite sonucu zorla başarılı yapılıyor.** [frame_handler.py:90](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/server/frame_handler.py:90) bulanıklık/pozlama sorunlarını sildikten sonra görüntüyü kabul ediyor. Eşikler gerçek Quest görüntüleriyle ayarlanmalı; yalnızca eski filtreyi açmak yeterli değil. RGB kalite puanı, depth geçerlilik oranı, zaman farkı, hareket hızı ve tracking güvenilirliği ayrı tutulmalı. RGB bulanıklığı ile geometrik depth hatası aynı ölçüt değildir. Tanılama için reddedilen karelerin nedeni korunmalı.

10. **F10 — P1: Nesne seçimi sunucuda kullanılmıyor.** [ScanManager.cs:218](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/QuestScanner/Assets/Scripts/ScanManager.cs:218) seçimi kullanıcı arayüzünde işler; seçim hacmi veri protokolüne eklenmez. Sınır, [PointCloudVisualizer.cs:164](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/QuestScanner/Assets/Scripts/PointCloudVisualizer.cs:164) içindeki yerel noktaları etkiler. Oturuma hedef hacim ve koordinat referansı eklenmeli. Fotogrametri poz hesabında arka plan yararlı olabileceğinden, ham görüntüyü baştan tahrip ederek kırpmak yerine izleme ve nihai nesne ayrıştırması farklı amaçlarla yönetilmeli.

11. **F11 — P1: Önizleme ölçülmemiş noktalar gösterebiliyor.** [PointCloudVisualizer.cs:192](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/QuestScanner/Assets/Scripts/PointCloudVisualizer.cs:192) depth yokken rastgele noktalar üretir. Bunlar sunucudaki nihai mesh'in doğrudan girdisi değildir; sorun kullanıcıya tarama kapsamı hakkında güvenilir olmayan geri bildirim verilmesidir. Ölçüm yoksa açıkça belirtilmeli. Kapsam, doğrulanmış yüzey/görüşlerden hesaplanmalı; yalnızca kare sayısıyla yüzde verilmemeli.

12. **F12 — P1: SIFT seçeneği CPU çıkarımını seçtiriyor.** [reconstruction.py:196](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/server/reconstruction.py:196) use_gpu=1 ile domain_size_pooling=1 gönderiyor. Kurulu COLMAP 4.1.0.dev0 / 5b76f53 kaynak kodu DSP durumunda CPU covariant extractor seçiyor. Aynı makinedeki küçük görüntü deneyi bunu doğruladı; DSP kapalıyken GPU extractor başarıyla çalıştı. GPU SIFT temel karşılaştırma olmalı, DSP kalite katkısı varsa ayrı yavaş seçenek olarak ölçülmeli. Bu test GPU bundle adjustment, dense stereo veya bütün pipeline'ın uyumluluğunu kanıtlamaz. [Kurulu sürümle eşleşen COLMAP kaynağı](https://github.com/colmap/colmap/blob/5b76f53/src/colmap/feature/sift.cc)

13. **F13 — P1: Konfigürasyon ile uygulanan eşleştirme farklı.** [config.py:51](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/server/config.py:51) sequential derken [reconstruction.py:217](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/server/reconstruction.py:217) exhaustive çalıştırıyor. MAX_FRAMES_FOR_RECONSTRUCTION, MIN_OVERLAP_RATIO ve COLMAP_MAX_IMAGE_SIZE değerlerinin ilgili davranışları yöneten kullanımları da yok. Aday çiftler zaman/poz/ortak görünürlükle azaltılmalı; tur kapanışı için ek eşleşmeler korunmalı. Az görüntülü bir sette exhaustive karşılaştırma olarak tutulabilir. [COLMAP öğreticisi](https://colmap.github.io/tutorial.html)

14. **F14 — P0/P1: Known poses yolu uygulanmamış; metrik ölçek bağlanmıyor.** [reconstruction.py:233](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/server/reconstruction.py:233) poz kullanımını False ile kapatıyor. Koşullu yolun gövdesi de pass; yalnızca bayrağı True yapmak çözüm değildir. Kamera parametresi vermek, bundle adjustment sırasında tüm intrinsics'in sabit kalmasını da sağlamaz; mapper refine seçenekleri ayrıca yönetilmeli. Tracking pozları doğru fiziksel kamera örneklerine bağlandıktan sonra başlangıç/prior ve metrik hizalama için değerlendirilmeli. Hatalı pozları sorgusuz sabitlemek kaliteyi düşürebilir. Görüntüden oluşan ölçeğin gerçek ölçüye bağlanması bağımsız kabul testidir.

15. **F15 — P1: CPU işlem yolları ve tekrar hesaplama.** [reconstruction.py:465](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/server/reconstruction.py:465) legacy ScalableTSDFVolume kullanıyor; Poisson ve ilgili legacy geometri adımları da CPU yolunda. 16 GB VRAM bu çağrıları otomatik GPU'ya taşımaz. Veritabanı her COLMAP çalıştırmasında siliniyor; yeniden çalıştırma önceki özellik/eşleşmeleri kullanamıyor. GPU TSDF için tensor VoxelBlockGrid ve uygun cihaz/paket kurulumu doğrulanmalı. Windows paket desteği kurulacak sürüm için test edilmeli. Ara sonuçlar girdi/ayar/sürüm kimliğiyle yeniden kullanılmalı; değişen aşamanın bağımlıları yenilenmeli. [Open3D TSDF](https://www.open3d.org/docs/latest/tutorial/t_reconstruction_system/integration.html), [kurulum bilgisi](https://www.open3d.org/docs/latest/getting_started.html)

16. **F16 — P1: Yakalama ve sunucu iş döngüsünde pahalı senkron işler.** [CameraCapture.cs:254](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/QuestScanner/Assets/Scripts/CameraCapture.cs:254) ve depth yolu sürekli texture oluşturma, ReadPixels ve JPEG kodlama yapıyor. [main.py:331](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/server/main.py:331) görüntü işleme/disk kaydını async alım döngüsünde senkron çağırıyor. Yeniden kullanılan tamponlar, uygun async GPU readback ve sınırlı iş kuyruğu değerlendirilmeli. Kuyruk doluluğu ve düşürülen kareler ölçülmeli. Gerçek passthrough alınamadığında sanal kamera RenderTexture çıktısına sessiz geçiş, geçerli nesne görüntüsü sayılmamalı. Asenkronlaştırırken görüntü ile poz snapshot'ı korunmalı.

17. **F17 — P1: Kaydedilen görüntü ham kaydı korumuyor.** [frame_handler.py:98](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/server/frame_handler.py:98) CLAHE, bilateral filtre ve keskinleştirme uygulayıp yeniden JPEG yazıyor. Orijinal JPEG korunmadığından aynı oturumda işlemenin katkısı sonradan temiz biçimde karşılaştırılamıyor; renk tutarlılığı ve ayrıntı etkileri belirsiz. Gelen RGB/depth ve metadata değişmez saklanmalı; işlenmiş görüntüler türetilmiş ürün olmalı. Filtrelerin tamamının her sahnede kötü olduğu sonucuna varılmıyor.

18. **F18 — P1: İş yönetimi ve başarı ölçütleri eksik.** [main.py:311](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/server/main.py:311) WebSocket reconstruction komutunda görev başlatmadan başladı yanıtı gönderiyor. Başlıktaki Processing durumu gerçek iş yaşam döngüsüne bağlı değil. Normal disconnect mesajıyla break yolu, except WebSocketDisconnect içindeki finalize işlemini çalıştırmıyor. Tek bir finalization yolu, idempotent start/stop, ortak job servisi, hata/iptal/yeniden başlama durumu gerekli. Full akış başarılı COLMAP sonrasında geçersiz depth yoluna girip genel hata verebilir; dense başarısızlığı ise bazı yollarda yalnızca uyarı olarak kalır. Sparse, dense, mesh ve baskı doğrulaması ayrı başarı durumları olmalı.

19. **F19 — P0: Viewer yeniden export sırasında fiziksel ölçeği değiştiriyor.** [viewer/index.html:462](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/viewer/index.html:462) modelin geometrisini merkezleyip en büyük boyutu 2 birime ölçekliyor. [viewer/index.html:661](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/viewer/index.html:661) aynı nesneyi export ediyor; [STLExporter.js:125](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/viewer/js/STLExporter.js:125) ve [OBJExporter.js:54](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/viewer/js/OBJExporter.js:54) world transform'u uygular. Böylece viewer'dan yeniden export edilen geometri ölçü değiştirir. Görüntüleme kameraya göre ayarlanmalı veya gösterim transform'u export'tan ayrılmalı. Sunucu STL'sinde de birim dönüşümü açık değil. İç geometri için metre standardı, STL baskı çıktısı için açık milimetre dönüşümü ve boyut doğrulaması gerekli. Sunucudan hazır dosyayı indirmek ile viewer'dan yeniden export farklı yollar olarak test edilmeli.

20. **F20 — P1/P2: Çıktı sözleşmesi, doku ve baskı denetimi eksik.** [reconstruction.py:600](C:/Users/emirh/OneDrive/Belgeler/GitHub/Meta-Quest-3d-Scan/server/reconstruction.py:600) export fonksiyonları seçilen özel output_dir yerine EXPORTS_DIR kullanıyor; colmap-only yol export parametresini fiilen kullanmıyor. Vertex renkleri var fakat çok görüntülü UV atlas/baking uygulanmamış. Dosya oluşması, yüzeyin baskıya uygun olduğunu kanıtlamıyor. Kapalı yüzey, manifold durum, ters normaller, kopuk parçalar, öz-kesişim ve gerçek boyut kontrolleri eklenmeli. Hangi çıktı özelliklerinin üretildiği açık bir manifest'te belirtilmeli. Delik kapatma ve Poisson'un ürettiği bölgeler ölçülmüş geometriyle karıştırılmamalı; düzenlenebilir ana mesh ve onarılmış baskı kopyası ayrı saklanmalı.

**Koordinat dönüşümü için özel düzeltme**

Mevcut quaternion eşlemesi `(x,y,z,w) -> (-x,y,-z,w)`, normalize bir quaternion için `R' = S R S`, `S = diag(1,-1,1)` eşlemesine karşılık gelir. Bu çalışmada 1.000 normalize örnekte iki hesabın matris elemanları arasındaki maksimum fark 0 bulundu. Dolayısıyla bu işaret işlemini tek başına hata olarak etiketlemek doğru değildir.

Bu cebir testi, RGB veya depth karesinin doğru fiziksel kamera pozu, görüntü yönü ve koordinat sistemiyle kaydedildiğini göstermez. Yeni uygulama dönüşümleri açık sözleşmeyle kurmalıdır: `T_A_B`, B uzayından A uzayına dönüşüm olarak tanımlanmalı; fiziksel kamera ofseti, tracking origin değişimi ve ters matris işlemleri ayrı test edilmelidir. Eğer API zaten fiziksel kameranın world pozunu sağlıyorsa head-to-camera ofseti ikinci kez uygulanmamalıdır.

Tek karede sağ/sol/yukarı/aşağı yön testi, metrik düzlem testi ve iki kamera arasındaki reprojection ölçümü geçmeden çok kareli fusion'a güvenilmemelidir.

**Performansın gerçekçi değerlendirilmesi**

| Girdi | Exhaustive aday çift | Sonraki 10 komşuyla aday çift | Yorum |
|---|---:|---:|---|
| 150 kare | 11.175 | 1.445 | Aday çift sayısında yaklaşık 7,7 kat fark. |
| 500 kare | 124.750 | 4.945 | Aday çift sayısında yaklaşık 25,2 kat fark. |

Bu oranlar toplam hızlanma değildir. Özellik çıkarımı, geometrik doğrulama, bundle adjustment, dense stereo, mesh ve export süreleri ayrıca ölçülmelidir. Komşu eşleştirmeye tur kapanışı/ortak görünürlük çiftleri eklenecektir. Her sahnede aynı pencere boyutu kullanılmamalıdır.

150 kare, bu projede maliyetin yalnızca “çok kare” ile açıklanmasını desteklemez. Gerçek bekleme süresi ve hangi aşamada beklendiği bilinmiyor. SIFT'in CPU seçimi, gereksiz çiftler, tekrar hesaplama, düşük kaliteli/tekrarlı kareler ve işlem yolları güçlü inceleme adaylarıdır. Veri olmadan her birinin süreye katkısına yüzde verilemez.

Kurulu COLMAP sürümü CUDA içeren geliştirme derlemesi olarak tanındı. GPU özellik çıkarımı örneği geçti. GPU bundle adjustment'ın ve PatchMatch'in aynı sistemde gerçek veriyle çalışması ayrıca kontrol edilmelidir. Geliştirme sürümü yerine test edilmiş bir sürüm/derleme kombinasyonu sabitlenmesi önerilir. İncelenen requirements dosyası bağımlılıkların tümünü sabitlemiyor; yeniden üretilebilir ortam ve başlangıç tanılama komutu gereklidir.

**Quest 3S ile kalite sınırı ve benzer projelerin rolü**

Meta'nın güncel dokümantasyonu Quest 3S'i Depth API için destekler. API'nin temel hedefi gerçek zamanlı çevre algılama/occlusion'dır; bu destek belirli bir nesnede milimetrik tarama doğruluğu garantisi değildir. Yaklaşık 20 cm'nin altında depth güvenilirliği düşer. [Resmî Depth API açıklaması](https://developers.meta.com/vr/documentation/unity/unity-depthapi-overview/)

Mevcut 5 mm TSDF voxel'i, yaklaşık 30 cm doğrusal boyutta 60 hücreye karşılık gelir. Bu ayrıntı bütçesi kaba olabilir; voxel boyutunu küçültmek sensörde bulunmayan ayrıntıyı üretmez. Voxel boyutu ölçülmüş doğruluk veya minimum çözülebilir ayrıntıyla birebir aynı kavram değildir. 40 mm truncation değerinin uygunluğu da veri üzerinde karşılaştırılmalı; değerin büyük olması tek başına hata kanıtı değildir.

| Referans | Kullanılabilecek yaklaşım | Sınır ve karar |
|---|---|---|
| [Meta Passthrough Samples](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples) | Fiziksel RGB kamera kalibrasyonu, poz, zaman ve iki kamera erişimi | Yakalama doğruluğu için ana referans. Tam tarama ürünü değil. |
| [Meta DepthAPI / MR Motifs](https://github.com/oculus-samples/Unity-DepthAPI) | Doku, katman ve reprojection örnekleri | Gerçek hedef SDK sürümünün koduyla eşleştirilmeli. |
| [QuestRoomScan](https://github.com/genesisinteractive/QuestRoomScan) | GPU TSDF, hareketle kare seçimi, kapsam ve doku işleme | README Quest 3 gerektiriyor; oda taraması ve farklı SDK bağımlılıkları nedeniyle 3S'e doğrudan uyarlama garantisi yok. Mimari referans. |
| [COLMAP](https://colmap.github.io/tutorial.html) + [OpenMVS](https://github.com/cdcseacave/openMVS) | Fotogrametri, yoğun yüzey, iyileştirme ve texture | Ayrıntılı nesne mesh'i için karşılaştırma adayı. Girdi kalitesine bağlı. |
| [LightGlue](https://github.com/cvg/LightGlue) | Öğrenilmiş özellik eşleştirme | Zor eşleşmelerde klasik GPU SIFT ile aynı veri üzerinde karşılaştırılmalı. |
| [FoundationStereo](https://github.com/NVlabs/FoundationStereo) / [Fast-FoundationStereo](https://github.com/NVlabs/Fast-FoundationStereo) | İki RGB görüntüden PC üzerinde depth | Stereo kalibrasyonu, rectification, zaman eşleşmesi ve sürüm/VRAM kontrolü gerekli. Varsayılan mimariye deneyden önce bağlanmamalı. |
| [gsplat](https://github.com/nerfstudio-project/gsplat) | Görsel sunum için Gaussian splat | Kullanıcının baskı hedefinde ikincil çıktı; kullanılabilir mesh ayrıca doğrulanmalı. |

Bu projelerin yayınladığı süre ve görseller, RTX 5060 Ti + Quest 3S sisteminde ölçülmüş sonuçlar değildir. Adaylar seçildikten sonra lisans ve hedef sürüm uygunluğu uygulama aşamasında kontrol edilmelidir.

**Önerilen MetaScan V2 davranışı**

Unity arayüzü, FastAPI servis temeli ve viewer korunabilir. Yakalama, veri sözleşmesi, kayıt ve job yönetimi yeniden düzenlenmeli; rekonstrüksiyon tek bir zorunlu algoritmaya bağlanmamalıdır.

1. Quest; açıkça seçilmiş gerçek RGB kameradan görüntü, aynı örneğin pozu/kalibrasyonu ve varsa doğrulanmış depth verisi toplar. Başlatma yalnızca gerekli kaynaklar hazır olduğunda tamamlanır.
2. Sürümlü kayıt; orijinal görüntüleri, depth encoding/birimini, boyutları, kamera kimliklerini, zaman alanlarını, dönüşümleri, kalite ölçülerini ve seçim hacmini korur. Kalibrasyon sabitse referansla tekrar kullanılır; mod değişimi sessizce kabul edilmez.
3. Tek ağ göndericisi mesaj sırasını yönetir. Sunucu kaydedilen kareleri onaylar. Oturum ancak son kabul edilen kare ve metadata kalıcı kayda ulaştıktan sonra kapanmış sayılır.
4. Sunucu aynı kaydı yeniden yakalama gerektirmeden işler. Orijinal veri değişmez; maskeler, özellikler, depth tahminleri ve mesh'ler ayar/sürüm kimliğiyle türetilir.
5. Hızlı önizleme, doğrulanmış depth/pozla oluşturulur. Ölçüm yoksa gösterilir. Önizleme kare yüzdesi yerine yüzey kapsamı ve veri kalitesini anlatır.
6. Ayrıntılı nesne modeli için seçilmiş karelerden fotogrametri/MVS; alternatif olarak doğrulanmış RGB-D veya stereo fusion karşılaştırılır. Poz refinement yalnızca ölçülmüş fayda sağladığında uygulanır.
7. Ana çıktı gerçek ölçekli, düzenlenebilir mesh ve isteğe bağlı doku olur. Baskı için onarılmış kopya, onarım kaydı ve milimetreye çevrilmiş STL üretilir. Viewer her iki çıktının ölçülerini korur.

Tek görüntüden üretken modelleme, ölçülmemiş bölgeler için yaratıcı tamamlama sağlayabilir; gerçeğe sadık taramanın doğruluk kanıtı sayılamaz. Kullanıcının doğrudan veya düzenleme sonrası baskı hedefinde bu seçenek ana rekonstrüksiyonun yerine konmamalıdır.

**Uygulama sırası ve her aşamanın çıkış koşulu**

| Aşama | İş kapsamı | Tamamlanma koşulu |
|---|---|---|
| A — Ölçüm ve tekrar üretim | Test ortamı, capability raporu, aşama süreleri, sabit nesne ve kayıt formatı | Aynı kayıt yeniden işlenebilir; gerçek kullanılan CPU/GPU yolu ve aşama süreleri görülebilir. |
| B — Güvenilir sensör kaydı | F01–F08: gerçek kalibrasyon/poz/zaman, depth semantiği, protokol, seri aktarım, finalize | Geçerli tek kare metrik ve yön testini geçer; ağ/disk sayımları uzlaşır; eksik kayıt açık hata olur. |
| C — Nesne kapsamı ve verimli temel | F09–F14, F16–F18: kalite, anahtar kare, seçim hacmi, GPU SIFT, aday çiftler, job akışı | Aynı kayıtla temel çıktı üretilebilir; kötü/tekrarlı kare kararı açıklanabilir; iş gerçek durumunu bildirir. |
| D — Geometri karşılaştırması | RGB-D/pose refinement, fotogrametri/MVS ve gerekirse stereo deneyleri | Referans ölçülere göre geometri ve süre karşılaştırması tamamlanır; varsayılan yol veriye göre seçilir. |
| E — Düzenleme ve baskı | F19–F20: ölçek, mesh denetimi, onarım, doğru export; ardından doku | Viewer, dosya ve dilimleyici boyutları uzlaşır; baskı kontrolü ve onarımlar raporlanır. |
| F — GPU fusion ve kullanım iyileştirmesi | F15, kalıcı ara sonuçlar, canlı kapsam, texture kalitesi | Profilde hedeflenen darboğaz azalır; kalite gerilemesi yoktur; aynı girdiyle tekrar gösterilir. |

B ve C içindeki bağımsız küçük performans düzeltmeleri erken yapılabilir. Ancak başarısı doğrulanmamış sensör verisiyle tüm algoritmaları aynı anda değiştirmek, hangi değişikliğin yararlı olduğunu ölçmeyi zorlaştırır. Her aşama ayrı ve gözden geçirilebilir değişikliklerle uygulanmalıdır. Bu rapor kod uygulamasını başlatmaz.

**Kabul deneyleri: henüz tamamlanmamış hedefler**

| Deney | Ölçüm ve karar |
|---|---|
| Sabit başlık, bilinen mesafede düz yüzey | Depth'in metre karşılığı, düzleme uzaklık hatası, geçersiz piksel oranı ve tekrarlar arası değişim. Başlangıçta en az üç bilinen mesafe. |
| Bilinen ölçülü kutu ve asimetrik desen | Tek karede yönler, ölçek ve RGB-depth kenar hizası. Sonuçlar mm ve piksel cinsinden raporlanır. |
| Kontrollü baş hareketi | Kamera poz-zaman farkı, reprojection sapması ve hareketle artan hata. |
| Yavaş bağlantı / dolan kuyruk / kesinti | Yakalanan, elenen, gönderilen, kabul edilen ve kaydedilen kare sayıları açıklanır; kayıpsız hedef veya belgelenmiş düşürme politikası sağlanır. |
| Yaklaşık 150 karelik sabit oturum | Aşama süreleri, CPU/GPU yolu, RAM/VRAM, kayıtlı/rekonstrükte görüntü oranı ve çıktı boyutları karşılaştırılır. |
| Nesnenin etrafında tam tur | Kapanış kayması, çift yüzey, kopuk parça ve ortak yüzey mesafesi değerlendirilir. |
| Bilinen boyutlu sentetik mesh round-trip | Sunucu export, viewer yükleme, viewer yeniden export ve dilimleyici boyutları karşılaştırılır; görünüm ölçeği geometriye taşınmaz. |
| Baskı uygunluğu | Açık kenar, manifold durumu, normaller, öz-kesişim ve küçük parçalar raporlanır; fiziksel baskıda ölçüler kontrol edilir. |
| Farklı malzemeler | Mat dokulu, mat dokusuz, koyu/parlak ve ince parçalı nesnelerle başarı sınırları ayrı kaydedilir. |

Geometrik doğruluk ve işlem süresi için şu an sağlanmış sayısal bir garanti yoktur. Kabul toleransları nesne boyutu, kullanım amacı ve ilk ölçümlerle belirlenmelidir. Hedefi sırf dosya üretilmesi veya çok kare toplanmasıyla başarılı saymak uygun değildir. Ölçülen yüzeylerin korunması, kullanıcıya eksik bölgelerin gösterilmesi ve tekrar üretilebilir iyileşme birlikte değerlendirilmelidir.

**Bu incelemede gerçekten yapılan kontroller**

| Kontrol | Sonuç | Kapsam sınırı |
|---|---|---|
| Mevcut kaynak ile önceki metnin karşılaştırılması | İletişim, kalibrasyon, depth ve protokol bulguları karşılaştırıldı; quaternion iddiası düzeltildi | Geçmiş taramadaki etki büyüklüğü ölçülmedi. |
| Dört Python sunucu dosyasının AST/sözdizimi kontrolü | Geçti | Import, bağımlılık, uygulama çalıştırması veya sayısal doğruluk testi değildir. |
| NVIDIA sistem bilgisi | RTX 5060 Ti, yaklaşık 16 GB VRAM görüldü | Boşta kullanım görüntüsü geçmiş tarama yükünü göstermez. |
| COLMAP sürüm ve komut seçenekleri | CUDA içeren 4.1.0.dev0, commit 5b76f53; ilgili seçenekler mevcut | Tüm solver/CUDA yolları çalıştırılmadı. |
| Geçici 320×320 BMP ile iki özellik çıkarımı | DSP açıkken CPU; kapalıyken GPU extractor. İki komut da exit 0 | Tek sentetik görüntü; gerçek kalite/süre karşılaştırması değildir. Bu kontrol önceki sohbet adımında yapıldı. |
| Binary parser ve pose için sınırlı kontrol | Kısa paket reddedildi; ek baytlar ve sıfır quaternion kabul edildi; header 32 bayt | İsteğe bağlı pose/depth alanı bazı modlarda geçerli olabilir. Doğrulama moda göre yapılmalı. |
| Quaternion eksen dönüşümü | 1.000 normalize örnekte S·R·S eşitliği; maksimum fark 0 | Fiziksel kamera ve görüntü yönünü doğrulamaz. |
| Aday çift sayıları | 150 ve 500 kare için tablo aritmetiği doğrulandı | Süre ölçümü değildir. |
| Gerçek 150 karelik oturumun yeniden işlenmesi | Yapılmadı; çalışma kopyasında kayıtlı oturum bulunmuyor | Kalite ve uçtan uca hız henüz ölçülmedi. |
| Unity build / Quest 3S denemesi / baskı | Yapılmadı | Cihaz davranışı ve fiziksel sonuç doğrulanmadı. |

Seçili Python ortamında numpy, scipy, OpenCV, Open3D, FastAPI, uvicorn ve trimesh bulunmadığı kontrol edildi. Bu, bilgisayardaki diğer Python ortamlarında da bulunmadıkları anlamına gelmez. Gerçek oturum ve çalışır sunucu ortamı hazırlandığında kalan doğrulamalar yapılmalıdır.

**Nihai karar**

Mevcut projenin kullanıcı arayüzü ve servis temeli üzerinde MetaScan V2 geliştirilebilir. İlk uygulama paketi; ölçüm altyapısı, fiziksel kamera kalibrasyonu/poz/zaman kaydı, doğru depth çözümleme ve sürümlü güvenilir veri aktarımı olmalıdır. Bunu nesne kapsamı, GPU SIFT ve kontrollü eşleştirme izlemelidir. Ayrıntılı rekonstrüksiyon yöntemi aynı kayıt üzerinde ölçülerek seçilmelidir. Ölçek ve baskı çıktısı bağımsız olarak doğrulanmalıdır.

Önerilerin uygulanması, Quest 3S'in ölçülmemiş donanım sınırlarını kaldırmaz. Buna karşılık mevcut yazılım hatalarının giderilmesi, cihazın sağlayabildiği verinin doğru değerlendirilmesi ve hangi kaliteye hangi sürede ulaşılabildiğinin kanıtlanması için gereklidir.
