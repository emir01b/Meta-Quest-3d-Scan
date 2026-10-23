using UnityEngine;

namespace MetaScan
{
    // Environment Depth is a reprojected, screen-space occlusion texture.
    // Its red channel is not a registered metric RGB camera depth image.
    // A future depth source must supply its own calibration, image dimensions,
    // timestamp and camera-to-world pose before it can enter the wire protocol.
    public class DepthCapture : MonoBehaviour
    {
        public bool IsDepthAvailable => false;
        public int DepthWidth => 0;
        public int DepthHeight => 0;
        public byte[] GetDepthData() => System.Array.Empty<byte>();
        public byte[] GetLastDepthData() => System.Array.Empty<byte>();
        public bool IsDepthFresh(float maxAge = 0.2f) => false;
    }
}
