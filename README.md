# 🎯 Meta3D Scanner

**Meta Quest 3S ile 3D nesne tarama ve model oluşturma sistemi.**

Quest 3S'in kamera ve sensörlerini kullanarak gerçek dünya nesnelerini tarayıp, ultra detaylı 3D modeller oluşturur.

## 🏗️ Mimari

```
Meta Quest 3S                    PC (Windows)
┌──────────────────┐            ┌──────────────────────────┐
│ Passthrough Cam  │            │ Python Server (FastAPI)  │
│ 1280x960 @ 30fps │───WiFi───▶│ ├─ Frame Receiver        │
│ Depth API        │ WebSocket │ ├─ COLMAP MVS Pipeline   │
│ 6DoF Tracking    │            │ ├─ Nerfstudio Gaussian   │
│ Scan UI          │◀──────────│ └─ Mesh Export (OBJ/GLB) │
│ Quality Feedback │  Feedback │                          │
└──────────────────┘            │ Web Viewer (Three.js)    │
                                └──────────────────────────┘
```

## 📋 Gereksinimler

| Bileşen | Gerekli | Durum |
|---------|---------|-------|
| Meta Quest 3S | Developer Mode açık | ✅ |
| NVIDIA GPU | RTX A4000 (16GB VRAM) | ✅ |
| RAM | 32GB | ✅ |
| Python | 3.11.9 | ✅ |
| Unity | 6.0 (veya 2022.3.58f1+) | ⬜ Kurulacak |
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

### 4. Unity Projesi (Quest)
1. Unity Hub'dan Unity 6 kurun
2. `QuestScanner` klasörünü Unity ile açın
3. Meta XR SDK, Passthrough Camera API paketlerini ekleyin
4. Build Settings → Android → Quest 3S'e deploy edin

### 5. Tarama
1. Quest'te uygulamayı açın
2. PC'nin IP adresini girin → Connect
3. Scan butonuna basın
4. Nesne etrafında yavaşça dönün (360°)
5. Stop butonuna basın
6. PC'de rekonstrüksiyon başlatın

## 📁 Proje Yapısı

```
meta3d/
├── server/                 # PC sunucusu (Python)
│   ├── main.py            # FastAPI + WebSocket sunucu
│   ├── config.py          # Tüm ayarlar
│   ├── frame_handler.py   # Frame işleme + kalite kontrolü
│   ├── reconstruction.py  # COLMAP + Nerfstudio pipeline
│   └── requirements.txt   # Python bağımlılıkları
├── QuestScanner/          # Unity projesi (Quest 3S)
│   └── Assets/Scripts/
│       ├── CameraCapture.cs   # Kamera yakalama
│       ├── DepthCapture.cs    # Derinlik sensörü
│       ├── DataStreamer.cs    # WebSocket streaming
│       └── ScanManager.cs    # Ana yönetici
├── viewer/                # Web tabanlı 3D viewer
│   └── index.html         # Three.js viewer
├── data/                  # Tarama verileri (otomatik oluşur)
│   ├── sessions/          # Ham tarama oturumları
│   └── exports/           # OBJ/GLB/PLY çıktılar
└── scripts/
    └── setup.ps1          # Kurulum scripti
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
