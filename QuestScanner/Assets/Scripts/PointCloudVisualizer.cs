/*
 * Meta3D Scanner - Point Cloud Visualizer
 * Displays scan progress as colored dots (particles) in 3D space.
 * Uses Unity ParticleSystem for efficient rendering on Quest.
 * 
 * Green = good quality, Yellow = medium, Red = low quality.
 */

using System.Collections.Generic;
using UnityEngine;

namespace Meta3DScanner
{
    public class PointCloudVisualizer : MonoBehaviour
    {
        [Header("Point Cloud Settings")]
        [SerializeField] private float pointSize = 0.006f;
        [SerializeField] private int maxPoints = 50000;
        [SerializeField] private float headCaptureRadius = 0.3f; // Radius around head gaze to add points

        [Header("Colors")]
        [SerializeField] private Color goodQualityColor = new Color(0.2f, 0.95f, 0.4f);
        [SerializeField] private Color mediumQualityColor = new Color(1.0f, 0.85f, 0.2f);
        [SerializeField] private Color lowQualityColor = new Color(1.0f, 0.3f, 0.2f);
        [SerializeField] private Color defaultColor = new Color(0.4f, 0.8f, 1.0f);

        [Header("Animation")]
        [SerializeField] private float fadeInDuration = 0.3f;
        [SerializeField] private float pulseSpeed = 2.0f;
        [SerializeField] private float pulseAmplitude = 0.002f;

        // ParticleSystem for rendering points
        private ParticleSystem particleSys;
        private ParticleSystemRenderer particleRenderer;
        private ParticleSystem.Particle[] particles;

        // Point data storage
        private List<Vector3> pointPositions = new List<Vector3>();
        private List<Color> pointColors = new List<Color>();
        private int currentPointCount = 0;

        // Stats
        private int goodQualityCount;
        private int mediumQualityCount;
        private int lowQualityCount;

        public int PointCount => currentPointCount;
        public int GoodQualityCount => goodQualityCount;
        public int TotalCapacity => maxPoints;

        private void Start()
        {
            CreateParticleSystem();
            Debug.Log("[Meta3D-PointCloud] Point cloud visualizer initialized");
        }

        private void CreateParticleSystem()
        {
            // Create a child GameObject for the particle system
            GameObject psObj = new GameObject("PointCloud_Particles");
            psObj.transform.SetParent(transform, false);
            psObj.transform.localPosition = Vector3.zero;
            psObj.transform.localRotation = Quaternion.identity;

            particleSys = psObj.AddComponent<ParticleSystem>();

            // Stop the auto-play
            particleSys.Stop();

            // Main module - particles live forever
            var main = particleSys.main;
            main.maxParticles = maxPoints;
            main.startLifetime = float.MaxValue;
            main.startSpeed = 0f;
            main.startSize = pointSize;
            main.startColor = defaultColor;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            main.loop = false;

            // Disable all default modules
            var emission = particleSys.emission;
            emission.enabled = false;

            var shape = particleSys.shape;
            shape.enabled = false;

            var velocityOverLifetime = particleSys.velocityOverLifetime;
            velocityOverLifetime.enabled = false;

            var colorOverLifetime = particleSys.colorOverLifetime;
            colorOverLifetime.enabled = false;

            var sizeOverLifetime = particleSys.sizeOverLifetime;
            sizeOverLifetime.enabled = false;

            var noise = particleSys.noise;
            noise.enabled = false;

            // Renderer - use simple billboard
            particleRenderer = psObj.GetComponent<ParticleSystemRenderer>();
            particleRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            particleRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            particleRenderer.receiveShadows = false;

            // Create a simple unlit material for particles
            Material particleMat = new Material(Shader.Find("Particles/Standard Unlit"));
            particleMat.SetFloat("_Mode", 0); // Opaque
            particleMat.color = Color.white;
            particleRenderer.material = particleMat;

            // Allocate particle array
            particles = new ParticleSystem.Particle[maxPoints];
        }

        /// <summary>
        /// Add a single point to the visualizer.
        /// </summary>
        /// <param name="worldPosition">World position of the point</param>
        /// <param name="color">Color of the point (quality indicator)</param>
        public void AddPoint(Vector3 worldPosition, Color color)
        {
            if (currentPointCount >= maxPoints)
            {
                Debug.LogWarning("[Meta3D-PointCloud] Max points reached, ignoring new points");
                return;
            }

            pointPositions.Add(worldPosition);
            pointColors.Add(color);
            currentPointCount++;

            // Track quality stats based on color
            if (IsColorClose(color, goodQualityColor))
                goodQualityCount++;
            else if (IsColorClose(color, mediumQualityColor))
                mediumQualityCount++;
            else if (IsColorClose(color, lowQualityColor))
                lowQualityCount++;

            // Update particles
            UpdateParticles();
        }

        /// <summary>
        /// Add multiple points at once (batch operation, more efficient).
        /// </summary>
        /// <param name="positions">Array of world positions</param>
        /// <param name="color">Color for all points in this batch</param>
        public void AddPoints(Vector3[] positions, Color color)
        {
            foreach (Vector3 pos in positions)
            {
                if (currentPointCount >= maxPoints) break;

                pointPositions.Add(pos);
                pointColors.Add(color);
                currentPointCount++;
            }

            UpdateParticles();
        }

        /// <summary>
        /// Add points based on a camera frame capture.
        /// Generates points along the camera's forward direction at varying depths.
        /// </summary>
        /// <param name="cameraPosition">Camera world position</param>
        /// <param name="cameraForward">Camera forward direction</param>
        /// <param name="cameraRight">Camera right direction</param>
        /// <param name="cameraUp">Camera up direction</param>
        /// <param name="qualityOk">Whether the frame quality is acceptable</param>
        /// <param name="hitDistance">Distance to the surface (if known, 0 = estimate)</param>
        public void AddFramePoints(Vector3 cameraPosition, Vector3 cameraForward,
            Vector3 cameraRight, Vector3 cameraUp, bool qualityOk, float hitDistance = 0f)
        {
            Color pointColor = qualityOk ? goodQualityColor : lowQualityColor;
            float distance = hitDistance > 0f ? hitDistance : 1.5f; // Default 1.5m if unknown

            // Generate a small cluster of points at the estimated surface position
            int pointsPerFrame = 5;
            for (int i = 0; i < pointsPerFrame; i++)
            {
                if (currentPointCount >= maxPoints) break;

                // Random offset within headCaptureRadius
                float offsetX = Random.Range(-headCaptureRadius, headCaptureRadius);
                float offsetY = Random.Range(-headCaptureRadius, headCaptureRadius);

                Vector3 surfacePoint = cameraPosition
                    + cameraForward * distance
                    + cameraRight * offsetX
                    + cameraUp * offsetY;

                pointPositions.Add(surfacePoint);
                pointColors.Add(pointColor);
                currentPointCount++;

                if (qualityOk) goodQualityCount++;
                else lowQualityCount++;
            }

            UpdateParticles();
        }

        /// <summary>
        /// Clear all points from the visualizer.
        /// </summary>
        public void ClearPoints()
        {
            pointPositions.Clear();
            pointColors.Clear();
            currentPointCount = 0;
            goodQualityCount = 0;
            mediumQualityCount = 0;
            lowQualityCount = 0;

            if (particleSys != null)
            {
                particleSys.Clear();
            }

            Debug.Log("[Meta3D-PointCloud] Point cloud cleared");
        }

        private void UpdateParticles()
        {
            if (particleSys == null || currentPointCount == 0) return;

            // Ensure particle system is playing
            if (!particleSys.isPlaying)
            {
                particleSys.Play();
            }

            // Set all particles
            int count = Mathf.Min(currentPointCount, maxPoints);

            for (int i = 0; i < count; i++)
            {
                particles[i].position = pointPositions[i];
                particles[i].startColor = pointColors[i];
                particles[i].startSize = pointSize;
                particles[i].remainingLifetime = float.MaxValue;
                particles[i].startLifetime = float.MaxValue;
            }

            particleSys.SetParticles(particles, count);
        }

        /// <summary>
        /// Set the quality color for a specific point index.
        /// </summary>
        public void SetPointQuality(int index, bool isGood)
        {
            if (index < 0 || index >= currentPointCount) return;

            pointColors[index] = isGood ? goodQualityColor : lowQualityColor;
            UpdateParticles();
        }

        /// <summary>
        /// Get the quality percentage (good quality points / total points).
        /// </summary>
        public float GetQualityPercent()
        {
            if (currentPointCount == 0) return 0f;
            return (float)goodQualityCount / currentPointCount * 100f;
        }

        private bool IsColorClose(Color a, Color b)
        {
            float threshold = 0.3f;
            return Mathf.Abs(a.r - b.r) < threshold &&
                   Mathf.Abs(a.g - b.g) < threshold &&
                   Mathf.Abs(a.b - b.b) < threshold;
        }

        /// <summary>
        /// Set the point size.
        /// </summary>
        public void SetPointSize(float size)
        {
            pointSize = size;
            if (currentPointCount > 0)
            {
                UpdateParticles();
            }
        }

        private void OnDestroy()
        {
            pointPositions.Clear();
            pointColors.Clear();
        }
    }
}
