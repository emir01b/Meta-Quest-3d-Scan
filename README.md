# 🎯 Meta3D Scanner

**Meta Quest 3S ile Mixed Reality 3D nesne tarama ve model oluşturma sistemi.**

Quest 3S'in kamera, derinlik sensörü ve MR Passthrough özelliğini kullanarak gerçek dünya nesnelerini tarayıp, ultra detaylı 3D modeller oluşturur.

## 🥽 MR Passthrough Deneyimi

Uygulama **Mixed Reality** modunda çalışır — gerçek dünyayı görmeye devam edersiniz:

- **Sol el**: Bağlantı paneli (sunucu IP, Connect/Tara/Dur butonları, durum bilgisi)
- **Sağ el**: Tarama lazeri (nesneyi seçip taramak için)
- **Nokta bulutu**: Taradıkça yüzeylerde yeşil noktalar oluşur

## 🏗️ Mimari

```
Meta Quest 3S (MR Passthrough)    PC (Windows)
┌─────────────────────────┐      ┌──────────────────────────┐
│ 🥽 OVRCameraRig         │      │ Python Server (FastAPI)  │
│ ├─ MR Passthrough       │      │ ├─ Frame Receiver        │
│ ├─ Sol El: UI Panel     │WiFi  │ ├─ COLMAP MVS Pipeline   │
│ ├─ Sağ El: Scan Pointer │─────▶│ ├─ Nerfstudio Gaussian   │
│ ├─ Passthrough Camera   │ WS   │ └─ Mesh Export (OBJ/GLB) │
│ ├─ Depth API            │      │                          │
│ └─ Point Cloud Viz      │◀─────│ Web Viewer (Three.js)    │
└─────────────────────────┘      └──────────────────────────┘
```

## 📋 Gereksinimler

| Bileşen | Gerekli | Durum |
|---------|---------|-------|
| Meta Quest 3/3S | Developer Mode açık | ✅ |
| Meta XR SDK | Core SDK veya All-in-One SDK | ⬜ Kurulacak |
| NVIDIA GPU | RTX A4000 (16GB VRAM) | ✅ |
| RAM | 32GB | ✅ |
| Python | 3.11.9 | ✅ |
| Unity | 6.0 (veya 2022.3 LTS) | ⬜ Kurulacak |
| COLMAP | Son sürüm | ⬜ Kurulacak |
| WiFi | 5GHz (Quest + PC aynı ağ) | ✅ |

## 🚀 Hızlı Başlangıç

### 1. Python Bağımlılıkları
```powershell
cd c:\Users\stnm\Desktop\meta3d
.\scripts\setup.ps1
```

### 2. Sunucuyu Başlat
```powershell
cd server
python main.py
```
Sunucu `http://0.0.0.0:8765` adresinde başlar.

### 3. Web Viewer
Tarayıcıda açın: `http://localhost:8765/viewer`

### 4. Unity Projesi (Quest - MR Passthrough)
1. Unity Hub'dan Unity 6 (veya 2022.3 LTS) kurun
2. `QuestScanner` klasörünü Unity ile açın
3. **Meta XR Core SDK** (veya All-in-One SDK) paketini import edin
4. **OVRCameraRig** prefab'ını sahneye ekleyin
5. Sahneye boş bir GameObject ekleyin ve **SceneBootstrapper** bileşenini atayın
6. Build Settings → Android → Quest 3/3S'e deploy edin

### 5. MR Tarama
1. Quest'te uygulamayı açın → **Gerçek dünyayı göreceksiniz**
2. Sol elinize bakın → **Bağlantı panelini** göreceksiniz
3. Sağ kontrol ile paneldeki IP'yi girin → **Bağlan** butonuna basın
4. Bağlantı sağlandıktan sonra **▶ Tara** butonuna basın
5. Sağ kontrol ile nesneyi gösterin → **Tarama başlar, noktalar oluşur**
6. Tamamlandığında **■ Dur** butonuna basın
7. PC'de rekonstrüksiyon otomatik başlar

## 📁 Proje Yapısı

```
meta3d/
├── server/                     # PC sunucusu (Python)
│   ├── main.py                # FastAPI + WebSocket sunucu
│   ├── config.py              # Tüm ayarlar
│   ├── frame_handler.py       # Frame işleme + kalite kontrolü
│   ├── reconstruction.py      # COLMAP + Nerfstudio pipeline
│   └── requirements.txt       # Python bağımlılıkları
├── QuestScanner/              # Unity projesi (Quest 3/3S - MR)
│   └── Assets/
│       ├── Scripts/
│       │   ├── MRSetup.cs             # 🥽 MR Passthrough kurulumu
│       │   ├── ControllerManager.cs   # 🎮 Kontroller yönetimi
│       │   ├── HandUIManager.cs       # ✋ Sol el UI paneli
│       │   ├── ScanPointer.cs         # 👉 Sağ el tarama lazeri
│       │   ├── PointCloudVisualizer.cs # 🔵 Nokta bulutu görselleştirme
│       │   ├── SceneBootstrapper.cs   # 🏗️ Sahne başlatıcı
│       │   ├── CameraCapture.cs       # 📷 Kamera yakalama
│       │   ├── DepthCapture.cs        # 📏 Derinlik sensörü
│       │   ├── DataStreamer.cs        # 📡 WebSocket streaming
│       │   └── ScanManager.cs        # 🎯 Ana yönetici
│       └── Plugins/Android/
│           └── AndroidManifest.xml   # Quest MR izinleri
├── viewer/                    # Web tabanlı 3D viewer
│   └── index.html             # Three.js viewer
├── data/                      # Tarama verileri (otomatik oluşur)
│   ├── sessions/              # Ham tarama oturumları
│   └── exports/               # OBJ/GLB/PLY çıktılar
└── scripts/
    └── setup.ps1              # Kurulum scripti
```

## 🔧 API Endpoints

| Method | Endpoint | Açıklama |
|--------|----------|----------|
| GET | `/api/health` | Sunucu durumu |
| GET | `/api/sessions` | Tüm oturumları listele |
| POST | `/api/sessions/create` | Yeni oturum oluştur |
| POST | `/api/sessions/{id}/reconstruct` | 3D rekonstrüksiyon başlat |
| GET | `/api/sessions/{id}/reconstruction/status` | Rekonstrüksiyon durumu |
| GET | `/api/sessions/{id}/exports` | Export dosyalarını listele |
| WS | `/ws/scan` | Real-time frame streaming |

## 🎨 Rekonstrüksiyon Yöntemleri

- **`full`** (varsayılan): Her iki pipeline birden — en yüksek kalite
- **`colmap`**: Sadece COLMAP MVS — geometrik doğruluk
- **`nerfstudio`**: Sadece Gaussian Splatting — visual kalite
- **`tsdf`**: Depth map'lerden TSDF Fusion — hızlı preview
