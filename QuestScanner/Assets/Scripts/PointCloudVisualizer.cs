/*
 * MetaScan — Point Cloud Visualizer
 * Displays scanned points as a particle system.
 * Color-coded by quality: green = good, yellow = medium, red = poor.
 */

using UnityEngine;

namespace MetaScan
{
    public class PointCloudVisualizer : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private int maxPoints = 50000;
        [SerializeField] private float pointSize = 0.008f;
        [SerializeField] private Color goodColor = new Color(0.2f, 0.9f, 0.4f, 0.9f);
        [SerializeField] private Color mediumColor = new Color(1.0f, 0.8f, 0.2f, 0.7f);
        [SerializeField] private Color poorColor = new Color(1.0f, 0.3f, 0.3f, 0.6f);

        [Header("Point Distribution")]
        [SerializeField] private int pointsPerFrame = 20;
        [SerializeField] private float pointSpread = 0.3f;
        [SerializeField] private float pointNoise = 0.02f;

        // Particle system
        private ParticleSystem _ps;
        private ParticleSystem.Particle[] particles;
        private int currentPointCount;

        // Reference to object selector for bounded visualization
        private ObjectSelector objectSelector;

        private void Start()
        {
            objectSelector = FindFirstObjectByType<ObjectSelector>();
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
            renderer.material.SetFloat("_Mode", 0); // Opaque-ish

            particles = new ParticleSystem.Particle[maxPoints];
            currentPointCount = 0;

            _ps.Play();

            Debug.Log("[MetaScan-PointCloud] Visualizer created (max: " + maxPoints + " points)");
        }

        /// <summary>
        /// Add points representing a captured frame's coverage area.
        /// </summary>
        public void AddFramePoints(Vector3 cameraPos, Vector3 forward,
            Vector3 right, Vector3 up, bool qualityOk, float hitDistance)
        {
            if (_ps == null || currentPointCount >= maxPoints) return;

            Color pointColor = qualityOk ? goodColor : poorColor;
            float dist = hitDistance > 0.1f ? hitDistance : 1.0f;

            int pointsToAdd = Mathf.Min(pointsPerFrame, maxPoints - currentPointCount);

            for (int i = 0; i < pointsToAdd; i++)
            {
                // Generate point in a cone from camera towards forward direction
                float offsetX = Random.Range(-pointSpread, pointSpread);
                float offsetY = Random.Range(-pointSpread, pointSpread);
                float offsetZ = Random.Range(-0.1f, 0.1f);

                Vector3 pos = cameraPos
                    + forward * dist
                    + right * offsetX
                    + up * offsetY;

                // Add some noise
                pos += new Vector3(
                    Random.Range(-pointNoise, pointNoise),
                    Random.Range(-pointNoise, pointNoise),
                    Random.Range(-pointNoise, pointNoise)
                );

                // If we have a selection, only add points within it
                if (objectSelector != null && objectSelector.HasSelection)
                {
                    if (!objectSelector.IsPointInSelection(pos))
                        continue;
                }

                EmitPoint(pos, pointColor);
            }
        }

        /// <summary>
        /// Add a single point at a specific position.
        /// </summary>
        public void AddPoint(Vector3 position, Color color)
        {
            EmitPoint(position, color);
        }

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
        /// Clear all points.
        /// </summary>
        public void ClearPoints()
        {
            if (_ps != null)
            {
                _ps.Clear();
                currentPointCount = 0;
            }
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
