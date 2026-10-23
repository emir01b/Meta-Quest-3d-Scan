# MetaScan — Quest 3S nesne taraması

MetaScan, Quest 3/3S fiziksel passthrough kamerasından kalibre edilmiş RGB karelerini ve aynı karelerin kamera pozlarını toplar. PC tarafında COLMAP ile fotogrametri yapar, modeli Quest kamera merkezleriyle metre ölçeğine hizalar ve düzenlenebilir geometri üretir. Kapalı bir yüzey elde edilirse ayrıca milimetre birimli baskı adayı STL çıkarır.

Bu sürümün temel ilkesi yanlış veriyi başarı gibi göstermemektir. Meta Environment Depth dokusu ekran için yeniden projekte edilen occlusion verisidir; fiziksel RGB kameraya kayıtlı, metrik bir depth haritası değildir. Bu nedenle önceki doğrudan uint16 depth/TSDF yolu ve rastgele nokta önizlemesi kaldırıldı. İleride depth eklemek için ayrı poz, zaman, projeksiyon, kayıt ve ölçü testleri gerekecek.

## Akış

1. Quest uygulaması PassthroughCameraAccess üzerinden tek bir fiziksel sol kamerayı açar. Aynı kamera görüntüsünün zamanını, dünya pozunu ve SDK ışınlarından hesaplanan etkin iç parametrelerini yakalar. Varsayılan 4 FPS ve 2 cm / 5° hareket eşiği gereksiz benzer kareleri azaltır. 40 cm veya 60° üstü ani kamera sıçraması olası tracking kayması olarak taramayı durdurur.
2. İstemci MSF2 WebSocket paketiyle JPEG ve JSON metadata gönderir. Gönderimler sıralıdır; bağlantı yavaşsa tarama durur ve hata bildirilir. Sunucu her kare için kabul sayısını döner.
3. Sunucu JPEG'i değiştirmeden kaydeder, gerçek boyut/kalibrasyon ve kalite ölçülerini doğrular. Oturum, COLMAP girdisi ve metadata diske yazıldıktan sonra kapanış onayı alır. Kopmuş oturum interrupted olarak işaretlenir.
4. COLMAP CUDA SIFT ve eşleştirmeyi çalıştırır. Ardışık görüntüler ve fiziksel kamera konumlarına yakın tur kapanışı adayları eşleştirilir. Sparse model PCA kamera merkezleriyle hizalanır; kayıtlı kamera oranı ve merkez hatası doğrulamayı geçmezse metrik çıktı üretilmez. Ardından dense stereo, fusion ve Poisson mesh çalışır. `low/medium/high` profilleri görüntü boyutu, SIFT özellik sayısı, stereo örnek/iterasyon sayısı ve Poisson derinliğini ayrı ayrı ayarlar; gerçek süre/kalite farkı cihaz verisiyle ölçülmelidir.
5. Küre seçimi varsa dense fusion görüntü maskeleriyle hedef hacme sınırlandırılır; sahne PLY'si bu seçimin ardından üretilen mesh'tir. OBJ/GLB/PLY metre biriminde, dünya koordinatlarında Y-yukarıdır. Kapalı, pozitif hacimli onarım adayı oluşursa STL Z-yukarı, tabla üzerinde ve milimetre cinsindedir. mesh_validation.json boyutları ve yüzey durumunu belirtir. UV doku atlası bu sürümde üretilmez.

## Kurulum

Python 3.11 önerilir. Windows'ta sunucu klasöründe sanal ortamı kurun:

    cd server
    py -3.11 -m venv .venv
    .venv\Scripts\python.exe -m pip install -r requirements.txt
    .venv\Scripts\python.exe main.py

Sunucu http://localhost:8765/viewer adresinde görüntüleyiciyi sunar. CUDA derlemeli COLMAP server/colmap_bin/COLMAP.bat konumundan çağrılır. İlk çalıştırmadan önce COLMAP.bat -h ve ekran kartı sürücüsünün çalıştığı doğrulanmalıdır.

### Günlük çalıştırma (Windows)

Kurulum bir kez tamamlandıktan sonra depo kökünde PowerShell açıp sunucuyu başlatın; tarama ve rekonstrüksiyon bitene kadar pencereyi açık bırakın:

    cd server
    .\.venv\Scripts\python.exe main.py

PC tarayıcısında `http://localhost:8765/viewer` adresini açın. Quest 3S'te geliştirici modu ve USB hata ayıklama açıkken cihazı veri aktarabilen USB-C kablosuyla bağlayın. Depo kökünde ikinci bir PowerShell penceresinde bir kez derlenmiş APK'yı yükleyin:

    $adb = "${env:ProgramFiles}\Unity\Hub\Editor\6000.6.7f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
    & $adb devices
    & $adb install -r .\QuestScanner\Builds\MetaScan-Quest3S-6000.6.7f1-roi-fix.apk

`adb devices` çıktısında cihaz `device` olarak görünmelidir. Liste boşsa USB kablosunu, geliştirici modunu, gözlükteki USB hata ayıklama onayını ve Windows Oculus ADB sürücüsünü kontrol edin. Uygulamayı Quest kitaplığındaki **Unknown Sources / Bilinmeyen Kaynaklar** bölümünden açın.

Quest ve PC aynı Wi-Fi ağındayken sol kontrolcüdeki panelin IP alanını sağ kontrolcünün lazeriyle gösterip sağ tetikle seçin. Açılan uygulama içi tuş takımında `Temizle` ile eski adresi silip PC'nin güncel yerel IPv4 adresini girin ve `Tamam` düğmesine basın. Port `8765` olmalıdır; gerekirse port alanını da aynı şekilde düzenleyin. Panel ilk açılışta görünür; sol grip tuşuna basmak paneli açar/kapatır. `Baglan`, `Cisim Sec`, `>> Tara`, `[] Dur` sırasını izleyin. Seçim merkezini sağ kontrolcüyle gösterip tetik tuşunu sürükleyerek yarıçapı ayarlayın; sağ çubukla derinliği değiştirin. `Dur` sonrası en az 30 kare kabul edildiyse rekonstrüksiyon otomatik başlar. Sonucu PC'deki viewer üzerinden açın. Windows Güvenlik Duvarı ilk bağlantıda sorarsa özel ağ erişimine izin verin.

Unity projesi QuestScanner klasöründedir; Unity 6000.6.7f1, Meta XR Core SDK 207 ve MR Utility Kit 207 kullanır. Unity Hub üzerinden Android Build Support (SDK, NDK ve OpenJDK dahil) kurulmalıdır. `Assets/Settings/Build Profiles/Android.asset` ile Android APK derlemesi yapılır. META_XR_SDK scripting define, Passthrough Camera Access ayarı ve `horizonos.permission.HEADSET_CAMERA` izni gereklidir; manifest izni depoda var. Unity 6000.6.7f1 ve SDK 207 ile Android APK derlemesi PC üzerinde başarıyla doğrulandı. Quest 3S'te uygulamanın açılması, uygulama içi IP tuş takımı, sunucu bağlantısı ve gerçek kamera taraması doğrulandı. İlk taramanın düzenlenebilir ağ çıktısı kurtarıldı; baskıya uygun kapalı yüzey henüz elde edilmedi.

Seçim alanı düzeltmesini içeren APK `QuestScanner/Builds/MetaScan-Quest3S-6000.6.7f1-roi-fix.apk` konumundadır. Aynı derleme Unity komut satırından `-activeBuildProfile "Assets/Settings/Build Profiles/Android.asset" -executeMethod BuildMetaScan.BuildQuest -batchmode -quit` ile tekrar üretilebilir. Yeni APK Quest 3S'e `adb install -r` ile yüklendi; kullanıcı kısa tetik tıklamasından sonra panelde 0,48 m çap gördüğünü ve kaskın küre içinde kaldığını doğruladı.

Quest ve PC aynı yerel ağda olmalıdır. Uygulamada sunucunun IP adresini girin; sağ çubukla seçim küresinin derinliğini ayarlayıp nesneyi tamamen içine alın; taramayı başlatın ve sabit duran nesnenin çevresinde yavaşça hareket edin. Yeni seçim önizlemesi 24 cm yarıçaplıdır; kısa tetik tıklaması görünen küreyi seçer, basılı tutup sürüklemek boyutu değiştirir. Seçim sonrasında gösterilen çapın nesneyi bütünüyle kapsadığını kontrol edin. 30 cm civarı nesnede alt/üst ve yan yüzey örtüşmesini koruyun. Nesneyi döndürürseniz Quest dünya pozları aynı nesne koordinatlarını temsil etmez. Yüzey mat ve aydınlatma tutarlı olmalıdır. İlk gerçek 85 karelik kask taramasının bulguları [gerçek tarama raporunda](docs/GERCEK_TARAMA_2026-10-23.md) yer alır.

## Protokol ve çıktı sözleşmesi

MSF2 çerçevesi little endian <4sIII başlıkla başlar: MSF2, JSON metadata boyutu, JPEG boyutu, depth boyutu. Ardından JSON ve JPEG gelir. Depth boyutu şu anda sıfır olmalıdır; kayıtlı metrik depth şeması uygulanana dek dolu depth paketi reddedilir. Metadata protocol_version=2, camera_id, gerçek görüntü boyutları, top_left yönü, görüntü zamanı, unity_world_camera_to_world poz ve aynı kareye ait fx/fy/cx/cy taşır. Eksik veya değişen kalibrasyon reddedilir. Eski MSF1 kayıtları metrik olarak otomatik yükseltilmez.

server/data/sessions/<id> kaynak kare ve metadata deposudur. Kaynak ve COLMAP kopyalarının SHA-256 değerleri işlem öncesi doğrulanır. Varsayılan çıktı klasörü server/data/exports/<id>'dir; özel alt klasör adı seçilirse dosyalar bunun altında saklanır ve API'deki son başarılı iş manifest'i üzerinden görüntülenip indirilir. Yol ayırıcı veya mutlak dosya yolu kabul edilmez. /api/sessions/<id>/reconstruction/status gerçek iş durumunu, /api/sessions/<id>/exports yalnızca son başarılı işin dosyalarını döner. /api/sessions/<id>/reconstruct?method=colmap&quality=medium ile aynı kayıt tekrar işlenebilir. Aşama logları ve süreleri sessions/<id>/colmap/runs/<run-id> altında tutulur. Girdi/ayar değişmediğinde başarılı aşamalar tekrar kullanılabilir.

OBJ, GLB ve PLY metre birimindedir. STL birimsiz biçim olduğundan *_print_candidate_mm.stl dosyasının koordinatları milimetreye çevrilmiştir. Viewer yalnızca kamerayı konumlandırır; mesh geometrisini görünüm uğruna yeniden ölçeklemez. Kullanıcı mesh'i viewer içinden STL olarak dışa aktarırsa Y-yukarı metre modelini Z-yukarı milimetreye dönüştürüp tabana yerleştirir. Viewer'dan indirilen STL, sunucunun yüzey onarım/doğrulama sürecinden geçmez. Sunucu STL'si de bir baskı adayıdır: slicer'da boyut, kopuk parçalar, destek ve yüzey hataları mutlaka incelenmelidir.

## Doğrulama

Depo kökünden:

    server/.venv/Scripts/python.exe -m pip install -r server/requirements-dev.txt
    server/.venv/Scripts/python.exe -m unittest discover -s server/tests -v
    server/.venv/Scripts/python.exe server/tests/colmap_smoke.py
    server/.venv/Scripts/python.exe server/tests/colmap_alignment_smoke.py
    node server/tests/viewer_smoke.js

Birim ve entegrasyon testleri protokol uzunluklarını, JPEG'in aynen saklanmasını, oturum kapanışını, poz dönüşümünü, eşleştirme adaylarını ve metre→milimetre STL dönüşümünü kontrol eder. GPU smoke testi, yerel COLMAP'te özellik çıkarımı ve eşleştirmenin çalıştığını denetler. İlk gerçek Quest taramasında kamera poz hizalaması ve açık yüzeyli düzenlenebilir çıktı doğrulandı; fiziksel ölçü hatası, 150 karelik işlem süresi ve baskı kalitesi henüz doğrulanmadı. [Quest 3S kabul testi](docs/QUEST_3S_KABUL_TESTI.md) bu ölçümleri adım adım tanımlar; [ilk teknik rapor](docs/METASCAN_NIHAI_TEKNIK_RAPOR_2026-10-22.md) önceki kodun denetimidir.

Sunucu aynı yerel ağdaki cihazların bağlanacağı şekilde 0.0.0.0:8765 üzerinde çalışır; dış ağa açılacak bir servis için ayrıca erişim kontrolü gerekir.
