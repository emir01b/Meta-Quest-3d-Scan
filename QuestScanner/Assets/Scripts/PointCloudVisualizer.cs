/*
 * MetaScan — Point Cloud Visualizer
 * Displays scanned points as a particle system using REAL depth data.
 * Depth pixels are deprojected into 3D world coordinates using camera intrinsics + pose.
 * Color is sampled from the camera image or coded by distance.
 */

using System.Collections.Generic;
using UnityEngine;

namespace MetaScan
{
    public class PointCloudVisualizer : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private int maxPoints = 100000;
        [SerializeField] private float pointSize = 0.006f;
        [SerializeField] private Color goodColor = new Color(0.2f, 0.9f, 0.4f, 0.9f);
        [SerializeField] private Color mediumColor = new Color(1.0f, 0.8f, 0.2f, 0.7f);
        [SerializeField] private Color poorColor = new Color(1.0f, 0.3f, 0.3f, 0.6f);

        [Header("Depth Point Cloud")]
        [Tooltip("Her frame'den örneklenecek piksel sayısı (düşük = hızlı, yüksek = yoğun)")]
        [SerializeField] private int samplesPerFrame = 512;
        [Tooltip("Minimum derinlik (metre) — daha yakın noktalar atlanır")]
        [SerializeField] private float minDepth = 0.1f;
        [Tooltip("Maksimum derinlik (metre) — daha uzak noktalar atlanır")]
        [SerializeField] private float maxDepth = 5.0f;
        [Tooltip("İki nokta arası minimum mesafe (metre) — tekrar engelleme")]
        [SerializeField] private float deduplicationDistance = 0.005f;

        [Header("Fallback (depth yoksa)")]
        [SerializeField] private int fallbackPointsPerFrame = 20;
        [SerializeField] private float fallbackPointSpread = 0.3f;
        [SerializeField] private float fallbackPointNoise = 0.02f;

        // Particle system
        private ParticleSystem _ps;
        private int currentPointCount;

        // Spatial hashing for deduplication
        private HashSet<long> occupiedCells;
        private float cellSize;

        // Reference to object selector for bounded visualization
        private ObjectSelector objectSelector;

        // Reusable list to avoid GC
        private System.Collections.Generic.List<Vector3> tempPointBuffer =
            new System.Collections.Generic.List<Vector3>(1024);

        private void Start()
        {
            objectSelector = FindFirstObjectByType<ObjectSelector>();
            cellSize = deduplicationDistance;
            occupiedCells = new HashSet<long>();
            CreateParticleSystem();
        }

        private void CreateParticleSystem()
        {
            GameObject psObj = new GameObject("PointCloud");
            psObj.transform.SetParent(transform, false);

            _ps = psObj.AddComponent<ParticleSystem>();

            // Stop default emission
            var emission = _ps.emission;
            emission.enabled = false;

            // Main module
            var main = _ps.main;
            main.startLifetime = float.MaxValue;
            main.startSpeed = 0;
            main.startSize = pointSize;
            main.maxParticles = maxPoints;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            main.loop = false;

            // Shape — disable
            var shape = _ps.shape;
            shape.enabled = false;

            // Renderer
            var renderer = psObj.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.material = new Material(Shader.Find("Particles/Standard Unlit"));
            renderer.material.SetFloat("_Mode", 0);

            currentPointCount = 0;

            _ps.Play();

            Debug.Log("[MetaScan-PointCloud] Visualizer created (max: " + maxPoints + " points, samples/frame: " + samplesPerFrame + ")");
        }

        // =============================================================
        // ANA METOD: Gerçek depth verisi ile 3D nokta bulutu oluştur
        // =============================================================

        /// <summary>
        /// Depth haritasından gerçek 3D noktalar üretir.
        /// depthData: uint16 little-endian milimetre cinsinden depth piksel dizisi
        /// depthWidth/depthHeight: depth haritası boyutları
        /// fx,fy,cx,cy: kamera intrinsics
        /// camPos: kamera world pozisyonu
        /// camRot: kamera world rotasyonu
        /// </summary>
        public void AddDepthPoints(byte[] depthData, int depthWidth, int depthHeight,
            float fx, float fy, float cx, float cy,
            Vector3 camPos, Quaternion camRot)
        {
            if (_ps == null || currentPointCount >= maxPoints) return;
            if (depthData == null || depthData.Length < 4) return;
            if (depthWidth <= 0 || depthHeight <= 0) return;

            int totalPixels = depthWidth * depthHeight;
            int expectedBytes = totalPixels * 2; // uint16
            if (depthData.Length < expectedBytes)
            {
                Debug.LogWarning($"[MetaScan-PointCloud] Depth veri boyutu uyumsuz: {depthData.Length} < {expectedBytes}");
                return;
            }

            // Kaç piksel örnekleyeceğiz
            int samplesToTake = Mathf.Min(samplesPerFrame, maxPoints - currentPointCount);
            if (samplesToTake <= 0) return;

            // Stride — kaç pikselde bir örnekle (eşit dağılım)
            int stride = Mathf.Max(1, totalPixels / samplesToTake);

            // Kamera dönüşüm matrisi
            Matrix4x4 camMatrix = Matrix4x4.TRS(camPos, camRot, Vector3.one);

            int pointsAdded = 0;

            for (int idx = 0; idx < totalPixels && pointsAdded < samplesToTake; idx += stride)
            {
                // uint16 little-endian oku → milimetre
                int byteIdx = idx * 2;
                ushort depthMM = (ushort)(depthData[byteIdx] | (depthData[byteIdx + 1] << 8));
                float depthM = depthMM * 0.001f; // metreye çevir

                // Geçersiz derinlikleri atla
                if (depthM < minDepth || depthM > maxDepth) continue;

                // Piksel koordinatlarını hesapla
                int py = idx / depthWidth;
                int px = idx % depthWidth;

                // Deprojection: piksel → kamera uzayı 3D nokta
                // X_cam = (px - cx) * depth / fx
                // Y_cam = (py - cy) * depth / fy
                // Z_cam = depth
                float xCam = (px - cx) * depthM / fx;
                float yCam = -(py - cy) * depthM / fy; // Unity Y yukarı, görüntü Y aşağı
                float zCam = depthM;

                // Kamera uzayından dünya uzayına dönüştür
                Vector3 localPoint = new Vector3(xCam, yCam, zCam);
                Vector3 worldPoint = camMatrix.MultiplyPoint3x4(localPoint);

                // Seçim alanı kontrolü
                if (objectSelector != null && objectSelector.HasSelection)
                {
                    if (!objectSelector.IsPointInSelection(worldPoint))
                        continue;
                }

                // Spatial deduplication — aynı hücrede zaten nokta var mı?
                long cellKey = GetCellKey(worldPoint);
                if (occupiedCells.Contains(cellKey))
                    continue;
                occupiedCells.Add(cellKey);

                // Derinliğe göre renk hesapla
                float normalizedDepth = Mathf.InverseLerp(minDepth, maxDepth, depthM);
                Color pointColor = Color.Lerp(goodColor, mediumColor, normalizedDepth);

                // Nokta emit et
                EmitPoint(worldPoint, pointColor);
                pointsAdded++;
            }

            if (pointsAdded > 0)
            {
                Debug.Log($"[MetaScan-PointCloud] +{pointsAdded} gerçek depth noktası (toplam: {currentPointCount})");
            }
        }

        /// <summary>
        /// Derinlik verisi yokken fallback: kamera yönünde tahmini noktalar saçar.
        /// Eski davranışın korunması.
        /// </summary>
        public void AddFramePoints(Vector3 cameraPos, Vector3 forward,
            Vector3 right, Vector3 up, bool qualityOk, float hitDistance)
        {
            if (_ps == null || currentPointCount >= maxPoints) return;

            Color pointColor = qualityOk ? goodColor : poorColor;
            float dist = hitDistance > 0.1f ? hitDistance : 1.0f;

            int pointsToAdd = Mathf.Min(fallbackPointsPerFrame, maxPoints - currentPointCount);

            for (int i = 0; i < pointsToAdd; i++)
            {
                float offsetX = Random.Range(-fallbackPointSpread, fallbackPointSpread);
                float offsetY = Random.Range(-fallbackPointSpread, fallbackPointSpread);

                Vector3 pos = cameraPos
                    + forward * dist
                    + right * offsetX
                    + up * offsetY;

                pos += new Vector3(
                    Random.Range(-fallbackPointNoise, fallbackPointNoise),
                    Random.Range(-fallbackPointNoise, fallbackPointNoise),
                    Random.Range(-fallbackPointNoise, fallbackPointNoise)
                );

                if (objectSelector != null && objectSelector.HasSelection)
                {
                    if (!objectSelector.IsPointInSelection(pos))
                        continue;
                }

                EmitPoint(pos, pointColor);
            }
        }

        // =============================================================
        // Spatial hashing
        // =============================================================

        private long GetCellKey(Vector3 pos)
        {
            int cx = Mathf.FloorToInt(pos.x / cellSize);
            int cy = Mathf.FloorToInt(pos.y / cellSize);
            int cz = Mathf.FloorToInt(pos.z / cellSize);
            // 21 bit per axis → max ±1048576 cells (~5km at 5mm cell)
            long key = ((long)(cx & 0x1FFFFF) << 42)
                     | ((long)(cy & 0x1FFFFF) << 21)
                     | (long)(cz & 0x1FFFFF);
            return key;
        }

        // =============================================================
        // Emit / Clear
        // =============================================================

        private void EmitPoint(Vector3 position, Color color)
        {
            if (currentPointCount >= maxPoints) return;

            var emitParams = new ParticleSystem.EmitParams();
            emitParams.position = position;
            emitParams.velocity = Vector3.zero;
            emitParams.startLifetime = float.MaxValue;
            emitParams.startSize = pointSize;
            emitParams.startColor = color;

            _ps.Emit(emitParams, 1);
            currentPointCount++;
        }

        /// <summary>
        /// Clear all points and reset spatial hash.
        /// </summary>
        public void ClearPoints()
        {
            if (_ps != null)
            {
                _ps.Clear();
                currentPointCount = 0;
            }
            occupiedCells?.Clear();
        }

        /// <summary>
        /// Get current point count.
        /// </summary>
        public int GetPointCount()
        {
            return currentPointCount;
        }
    }
}
